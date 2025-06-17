using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.Abstractions.Mock;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Grpc.Abstractions;
using OutOfProcModel.Mock;
using OutOfProcModel.Workers;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorkerStream
{
    private readonly IJobHostManager _jobHostManager;
    private readonly CancellationTokenSource _stopTokenSource = new();

    // TODO: It's possible for a single worker to connect to us with several streams. They should share
    //       this channel, so it should likely move to a factory where that can be managed.
    private readonly BidirectionalChannel _channel = new();

    private Task? _readTask;

    // Keep these separate for better tracking of state
    private WorkerState? _workerState;
    private WorkerState? _placeholderWorkerState;

    public GrpcWorkerStream(IJobHostManager jobHostManager)
    {
        _jobHostManager = jobHostManager;
    }

    public StreamState StreamState { get; private set; } = StreamState.None;

    private WorkerState GetCurrentWorkerState()
    {
        return _workerState ?? _placeholderWorkerState ?? throw new InvalidOperationException("WorkerState is not initialized. Cannot get current worker state.");
    }

    public async IAsyncEnumerable<GrpcToWorker> StartAsync(IAsyncEnumerable<GrpcFromWorker> requests)
    {
        _readTask = ReadStreamAsync(requests, _stopTokenSource.Token);

        // Return all outgoing messages to the worker
        while (await _channel.WorkerMessageReader.WaitToReadAsync())
        {
            while (_channel.WorkerMessageReader.TryRead(out var message))
            {
                // Send messages to worker via grpc stream
                var grpcMsg = new GrpcToWorker
                {
                    MessageType = message.MessageType,
                    Id = message.ApplicationId,
                    Properties = message.Properties
                };
                yield return grpcMsg;
            }
        }
    }

    public async Task StopAsync()
    {
        if (_readTask == null)
        {
            return;
        }

        if (_channel.WorkerMessageWriter.TryWrite(new MessageToWorker(GetCurrentWorkerState().Definition.ApplicationId, FunctionsGrpcMessage.ShutdownMessage, new Dictionary<string, string>())))
        {
            // Signal the worker to shut down gracefully
            var completed = await Task.WhenAny(Task.Delay(5000), _readTask);
            if (completed != _readTask)
            {
                // If the read task didn't complete in time, we cancel it            
                _stopTokenSource.Cancel();
            }
        }
        else
        {
            // The worker has already been stopped or the channel is closed
            _stopTokenSource.Cancel();
        }

        await _readTask;
    }

    private async Task ReadStreamAsync(IAsyncEnumerable<GrpcFromWorker> requests, CancellationToken stopToken)
    {
        // Allow callers to await this method without blocking the thread
        await Task.Yield();

        try
        {
            await foreach (var req in requests.WithCancellation(stopToken))
            {
                // Handle the request
                switch (req.MessageType)
                {
                    case FunctionsGrpcMessage.StartStream: // note: 
                        _ = HandleStartStreamAsync(req);
                        break;
                    case FunctionsGrpcMessage.MetadataResponse:
                        HandleJobHostMessage(req);
                        break;
                    case FunctionsGrpcMessage.InvocationResponse:
                        StreamState = ChangeState(Action.InvocationResponse);
                        _channel.HostMessageWriter.TryWrite(new MessageFromWorker(GetCurrentWorkerState().Definition.ApplicationId, req.MessageType, req.Properties));
                        break;
                    case FunctionsGrpcMessage.EnvironmentReloadResponse:
                        _ = HandleEnvironmentReloadResponseAsync(req);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported message type: {req.MessageType}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Handle cancellation gracefully
            StreamState = StreamState.Stopped;
        }
        catch (Exception ex)
        {
            // Log the exception or handle it as needed
            Console.WriteLine($"Error reading stream: {ex.Message}");
            StreamState = StreamState.Stopped;
        }

        // TODO: revisit this. If we get here, it could mean:
        // - stop token was called and we exited b/c of that
        // - the worker disconnected and is no longer sending us data
        // What all do we need to clean up?
        // await StopAsync();

        await _jobHostManager.TryGetJobHostAsync(GetCurrentWorkerState().Definition.ApplicationId, out var jobHost);
        if (jobHost != null)
        {
            await jobHost.WorkerManager.RemoveWorkerAsync(GetCurrentWorkerState().Definition.WorkerId);
        }
    }

    private void HandleJobHostMessage(GrpcFromWorker req)
    {
        var msgType = MapMessageType(req.MessageType);
        ThrowIfInvalidState(msgType);

        // translate from grpc and send to JobHost
        var msgFromWorker = new MessageFromWorker(GetCurrentWorkerState().Definition.ApplicationId, req.MessageType, req.Properties);
        _ = _jobHostManager.HandleMessageAsync(msgFromWorker);

        StreamState = ChangeState(MapMessageType(req.MessageType));

        static Action MapMessageType(string messageType) =>
            messageType switch
            {
                FunctionsGrpcMessage.MetadataResponse => Action.MetadataResponse, // the only one for now
                _ => throw new InvalidOperationException($"Unknown message type: {messageType}")
            };
    }

    private bool IsPlaceholder => GetCurrentWorkerState() == _placeholderWorkerState;

    private void ThrowIfInvalidState(Action action)
    {
        _ = ChangeState(action);
    }

    private StreamState ChangeState(Action action) =>
        (StreamState, action) switch
        {
            (StreamState.None, Action.StartStream) => StreamState.Connected,
            (StreamState.Connected, Action.MetadataResponse) => StreamState.Initialized,
            (StreamState.Connected, Action.Specialize) when IsPlaceholder => StreamState.Specializing,
            (StreamState.Connected, Action.InvocationResponse) => StreamState.Running, // This can happen when we don't need a  metadata response
            (StreamState.Initialized, Action.InvocationResponse) when IsPlaceholder => StreamState.RunningAsPlaceholder,
            (StreamState.Initialized, Action.InvocationResponse) when !IsPlaceholder => StreamState.Running,
            (StreamState.RunningAsPlaceholder, Action.Specialize) => StreamState.Specializing,
            (StreamState.RunningAsPlaceholder, Action.InvocationResponse) => StreamState.RunningAsPlaceholder,
            (StreamState.Specializing, Action.EnvironmentReloadResponse) => StreamState.Connected,
            (StreamState.Running, Action.InvocationResponse) => StreamState.Running,
            (StreamState.Running, Action.Specialize) => StreamState.Specializing,
            _ => throw new InvalidOperationException($"Cannot change state from '{StreamState}' with '{action}'.")
        };

    private async Task HandleStartStreamAsync(GrpcFromWorker startStream)
    {
        ThrowIfInvalidState(Action.StartStream);

        var runtime = startStream.Properties[nameof(RuntimeEnvironment.Runtime)];
        var version = startStream.Properties[nameof(RuntimeEnvironment.Version)];
        var architecture = startStream.Properties[nameof(RuntimeEnvironment.Architecture)];
        var isPlaceholder = bool.TryParse(startStream.Properties[nameof(RuntimeEnvironment.IsPlaceholder)], out var placeholder) && placeholder;

        var environment = new RuntimeEnvironment(runtime, version, architecture, isPlaceholder);
        var capabilities = startStream.Properties["Capabilities"].Split(';');

        var workerDef = new WorkerDefinition(startStream.Id, startStream.Properties["ApplicationId"], startStream.Properties["ApplicationVersion"], capabilities, environment);

        if (environment.IsPlaceholder)
        {
            _placeholderWorkerState = new WorkerState(workerDef);
        }
        else
        {
            _workerState = new WorkerState(workerDef);
        }

        StreamState = ChangeState(Action.StartStream);

        await StartNewJobHostAsync(workerDef, _channel, _jobHostManager);
    }

    private static async Task StartNewJobHostAsync(WorkerDefinition workerDef, IWorkerChannel channel, IJobHostManager jobHostManager)
    {
        var jobHost = await CreateJobHostAsync(workerDef, channel, jobHostManager);

        var contextProperties = new Dictionary<string, object>
        {
            { "Channel", channel },
        };

        var context = new WorkerCreationContext(workerDef, contextProperties);
        await jobHost.WorkerManager.CreateWorkerAsync(context);
    }

    // TODO: use the JobHostBuilder like in Functions.
    private static Task<JobHost> CreateJobHostAsync(WorkerDefinition workerDef, IWorkerChannel channel, IJobHostManager jobHostManager)
    {
        return jobHostManager.GetOrAddJobHostAsync(workerDef.ApplicationId, () =>
        {
            var context = new JobHostStartContext(workerDef.ApplicationId, workerDef.ApplicationVersion);
            return context;
        },
        services =>
        {
            // register our provider that knows how to use the grpc details below            
            services.AddSingleton(p => new GrpcFunctionMetadataFactory(workerDef.ApplicationId, channel));
            services.AddSingleton<IFunctionMetadataFactory>(p => p.GetRequiredService<GrpcFunctionMetadataFactory>());
            services.AddSingleton<IMessageHandler>(p => p.GetRequiredService<GrpcFunctionMetadataFactory>());
            services.AddSingleton<IWorkerFactory, GrpcWorkerFactory>();

            if (workerDef.RuntimeEnvironment.IsPlaceholder)
            {
                services.AddSingleton<IWorkerResolver, PlaceholderWorkerResolver>();
            }
        });
    }

    // Note this is prototype - would likely come from an OptionsMonitor or some token similar to how it does today.
    public async Task<bool> TrySpecializeAsync(string applicationId, string applicationVersion, RuntimeEnvironment runtimeEnvironmentToMatch)
    {
        if (_placeholderWorkerState is null || !IsPlaceholder)
        {
            return false;
        }

        if (_placeholderWorkerState.Definition.RuntimeEnvironment != runtimeEnvironmentToMatch with { IsPlaceholder = true })
        {
            return false;
        }

        StreamState = ChangeState(Action.Specialize);

        // This lets the channel know that it will be re-used and its internal Channels should not be completed.
        _channel.MarkForSpecialization();

        // This will drain/stop this specific IWorker and preserve the Channels for specialization. Others will be shutdown outside of this class.
        // TODO -- is there a way we can guarantee this? Like a chained Channel that we can disconnect and Close()?
        var currentDef = GetCurrentWorkerState().Definition;
        await _jobHostManager.TryGetJobHostAsync(currentDef.ApplicationId, out var jobHost);
        await jobHost.WorkerManager.RemoveWorkerAsync(currentDef.WorkerId);

        await _channel.SpecializationCompletion;

        // Tell the worker to specialize with new environment details.
        // It will respond back to us with its details (like capabilities).
        _channel.WorkerMessageWriter.TryWrite(new MessageToWorker(applicationId, FunctionsGrpcMessage.EnvironmentReloadRequest,
            new Dictionary<string, string>
            {
                { "ApplicationId", applicationId },
                { "ApplicationVersion", applicationVersion },
                // TODO: Env vars, etc
            }));

        return true;
        // Continues in HandleEnvironmentReloadResponseAsync()
    }

    private async Task HandleEnvironmentReloadResponseAsync(GrpcFromWorker rpc)
    {
        if (_placeholderWorkerState is null || _workerState is not null)
        {
            throw new InvalidOperationException("WorkerState is not initialized as expected. Cannot handle environment reload response.");
        }

        StreamState = ChangeState(Action.EnvironmentReloadResponse);

        var capabilities = rpc.Properties["Capabilities"].Split(';');
        _workerState = _placeholderWorkerState.Specialize(rpc.Properties["ApplicationId"], rpc.Properties["ApplicationVersion"], capabilities);
        await StartNewJobHostAsync(_workerState.Definition, _channel, _jobHostManager);
    }
}

internal enum StreamState
{
    None,
    Connected,
    Initialized,
    RunningAsPlaceholder,
    Specializing,
    Running,
    Draining,
    Stopped
}

internal enum Action
{
    StartStream,
    MetadataResponse,
    InvocationResponse,
    Specialize,
    EnvironmentReloadResponse,
}
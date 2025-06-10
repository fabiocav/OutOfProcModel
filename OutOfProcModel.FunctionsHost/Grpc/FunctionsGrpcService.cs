using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Mock;
using System.ServiceModel;
using System.Threading.Channels;

namespace OutOfProcModel.FunctionsHost.Grpc;

[ServiceContract]
public interface IFunctionsHostGrpcService
{
    IAsyncEnumerable<GrpcToWorker> StartStreamAsync(IAsyncEnumerable<GrpcFromWorker> requests);
}

internal record PlaceholderDetails(WorkerGrpcStream WorkerStream, WorkerState WorkerState);


internal class FunctionsHostGrpcService(IJobHostManager jobHostManager, ILogger<FunctionsHostGrpcService> logger) : IFunctionsHostGrpcService
{
    private readonly ILogger<FunctionsHostGrpcService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IJobHostManager _jobHostManager = jobHostManager ?? throw new ArgumentNullException(nameof(jobHostManager));

    private readonly Dictionary<RuntimeEnvironment, PlaceholderDetails> _placeholderDetails = [];
    private CancellationTokenSource _placeholderReadLoopToken = new();
    private CancellationTokenSource _readLoopToken = new();

    public async IAsyncEnumerable<GrpcToWorker> StartStreamAsync(IAsyncEnumerable<GrpcFromWorker> requests)
    {
        // move to the first message
        var enumerator = requests.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();

        // first request must be StartStream so populate details
        var startStream = enumerator.Current;

        var environment = new RuntimeEnvironment(startStream.Properties["Runtime"], startStream.Properties["Version"],
            startStream.Properties["Architecture"], bool.Parse(startStream.Properties["IsPlaceholder"]));

        var capabilities = startStream.Properties["Capabilities"].Split(';');

        var workerState = new WorkerState(startStream.Properties["ApplicationId"], startStream.Properties["ApplicationVersion"], startStream.Id, environment, capabilities);

        var workerStream = new WorkerGrpcStream();

        if (environment.IsPlaceholder)
        {
            // Need to expose these to re-use when specialization occurs
            _placeholderDetails[environment] = new(workerStream, workerState);
        }

        _ = StartNewJobHostAsync(workerState, workerStream.Incoming.Reader, workerStream.Outgoing.Writer,
            environment.IsPlaceholder ? _placeholderReadLoopToken.Token : _readLoopToken.Token);

        // loop and read incoming, pumping them to the channel
        _ = Task.Run(async () =>
        {
            await foreach (var req in AsAsyncEnumerable(enumerator))
            {
                // write to the worker stream
                await workerStream.Incoming.Writer.WriteAsync(req);
            }
        });

        // Return all outgoing messages to the worker
        while (await workerStream.Outgoing.Reader.WaitToReadAsync())
        {
            while (workerStream.Outgoing.Reader.TryRead(out var message))
            {
                // Send messages to worker via grpc stream
                yield return message;
            }
        }
    }

    private async Task StartNewJobHostAsync(WorkerState workerState, ChannelReader<GrpcFromWorker> incoming, ChannelWriter<GrpcToWorker> outgoing, CancellationToken cancellationToken)
    {
        await CreateJobHostAsync(workerState, outgoing);

        await _jobHostManager.AssignWorkerAsync(new WorkerCreationContext(workerState.WorkerId, workerState.ApplicationId, workerState.ApplicationVersion, workerState.Capabilities));

        await ReadFromWorkerAsync(workerState.ApplicationId, workerState.WorkerId, incoming, outgoing, cancellationToken);
    }

    // helper
    private static async IAsyncEnumerable<GrpcFromWorker> AsAsyncEnumerable(IAsyncEnumerator<GrpcFromWorker> source)
    {
        while (await source.MoveNextAsync())
        {
            yield return source.Current;
        }
    }

    // main read loop from the GrpcStream -> JobHost
    private async Task ReadFromWorkerAsync(string applicationId, string workerId, ChannelReader<GrpcFromWorker> incoming, ChannelWriter<GrpcToWorker> outgoing, CancellationToken cancellationToken)
    {
        try
        {
            while (await incoming.WaitToReadAsync(cancellationToken))
            {
                while (!cancellationToken.IsCancellationRequested && incoming.TryRead(out var req))
                {
                    switch (req.MessageType)
                    {
                        // Messages for the JobHost
                        case FunctionsGrpcMessage.MetadataResponse:
                        case FunctionsGrpcMessage.InvocationResponse:
                            // translate from grpc and send to JobHost
                            var msgFromWorker = new MessageFromWorker(applicationId, req.MessageType, req.Properties);
                            _ = _jobHostManager.HandleMessageAsync(msgFromWorker);
                            break;
                        case FunctionsGrpcMessage.EnvironmentReloadResponse:
                            _ = HandleEnvironmentReloadResponseAsync(req, incoming, outgoing);
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Worker {WorkerId} read loop was cancelled.", workerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while processing messages for worker {WorkerId}.", workerId);
            throw; // rethrow to let the caller handle it
        }

        // drain existing JobHost
        var jobHost = await _jobHostManager.GetJobHostAsync(applicationId);
        Task drainTask;
        if (jobHost != null)
        {
            drainTask = jobHost.DrainAsync();

            _logger.LogInformation("Worker {WorkerId} has finished processing messages.", workerId);

            // Remove this IWorker from the load balancer        
            var workerManager = jobHost.Services.GetRequiredService<IWorkerManager>();
            var workers = workerManager.GetWorkers();
            var workerToRemove = workers.First(h => h.WorkerId == workerId);
            workerManager.RemoveWorker(workerToRemove);

            // if that was the last one, stop and remove the JobHost
            if (workers.Count == 1)
            {
                _logger.LogInformation("Stopping JobHost for application {ApplicationId} as no more handlers are available.", applicationId);
                await jobHost.StopAsync();
                await _jobHostManager.RemoveJobHostAsync(applicationId);
            }
        }
    }

    private Task HandleEnvironmentReloadResponseAsync(FunctionsGrpcMessage req, ChannelReader<GrpcFromWorker> incoming, ChannelWriter<GrpcToWorker> outgoing)
    {
        var environment = new RuntimeEnvironment(req.Properties["Runtime"], req.Properties["Version"], req.Properties["Architecture"], bool.Parse(req.Properties["IsPlaceholder"]));
        var capabilities = req.Properties["Capabilities"].Split(';');
        var workerState = new WorkerState(req.Properties["ApplicationId"], req.Properties["ApplicationVersion"], req.Id, environment, capabilities);
        workerState.Status = WorkerStatus.Running;

        // we now have what we need to start a new JobHost for this worker       
        _ = StartNewJobHostAsync(workerState, incoming, outgoing, _readLoopToken.Token);

        return Task.CompletedTask;
    }

    private Task<JobHost> CreateJobHostAsync(WorkerState workerState, ChannelWriter<GrpcToWorker> outgoing)
    {
        return _jobHostManager.GetOrAddJobHostAsync(workerState.ApplicationId, () =>
        {
            var context = new JobHostStartContext(workerState.ApplicationId, workerState.ApplicationVersion);
            return context;
        },
        services =>
        {
            // register our provider that knows how to use the grpc details below
            services.AddSingleton<MessageHandlerPipeline>();
            services.AddSingleton(p => new GrpcFunctionMetadataFactory(workerState.WorkerId, outgoing));
            services.AddSingleton<IFunctionMetadataFactory>(p => p.GetRequiredService<GrpcFunctionMetadataFactory>());
            services.AddSingleton<IMessageHandler>(p => p.GetRequiredService<GrpcFunctionMetadataFactory>());
            services.AddSingleton<IWorkerChannelFactory>(p => new GrpcWorkerChannelFactory(p.GetRequiredService<MessageHandlerPipeline>(), outgoing, p.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IWorkerFactory, GrpcWorkerFactory>();
        });
    }

    public Task SpecializeWorkerAsync(ApplicationContext appContext, RuntimeEnvironment environment)
    {
        _placeholderReadLoopToken.Cancel();

        // create new JobHost, using existing stream
        if (_placeholderDetails.Remove(environment, out var workerDetails))
        {
            // We have a channel for this environment, so we can specialize it
            _logger.LogInformation("Specializing worker for environment: {Environment}", environment);

            var workerState = workerDetails.WorkerState.Specialize(appContext.ApplicationId, appContext.ApplicationVersion, []);

            _ = StartNewJobHostAsync(workerState, workerDetails.WorkerStream.Incoming.Reader, workerDetails.WorkerStream.Outgoing.Writer, _readLoopToken.Token);

            // Send a specialization message to the channel
            workerDetails.WorkerStream.Outgoing.Writer.TryWrite(new GrpcToWorker
            {
                MessageType = FunctionsGrpcMessage.EnvironmentReloadRequest,
                Properties = new Dictionary<string, string>
                {
                    { "ApplicationId", appContext.ApplicationId },
                    { "ApplicationVersion", appContext.ApplicationVersion },
                    // TODO: Env vars, etc
                }
            });
        }
        else
        {
            _logger.LogWarning("No placeholder channel found for environment: {Environment}", environment);
        }

        // Every remaining placeholder worker should exit
        foreach ((var env, var placeholderDetails) in _placeholderDetails)
        {
            _logger.LogInformation("Sending shutdown message to placeholder worker for environment: {Environment}", env);

            placeholderDetails.WorkerStream.Outgoing.Writer.TryWrite(new GrpcToWorker
            {
                MessageType = FunctionsGrpcMessage.ShutdownMessage,
                Properties = new Dictionary<string, string>
                {
                    { "Reason", "Not chosen for specialization." },
                }
            });
        }

        return Task.CompletedTask;
    }
}

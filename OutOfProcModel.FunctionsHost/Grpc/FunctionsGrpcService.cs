using System.ServiceModel;
using System.Threading.Channels;
using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Mock;

namespace OutOfProcModel.FunctionsHost.Grpc;

[ServiceContract]
public interface IFunctionsHostGrpcService
{
    IAsyncEnumerable<FunctionsGrpcMessage> StartStreamAsync(IAsyncEnumerable<FunctionsGrpcMessage> requests);
}

internal class FunctionsHostGrpcService(IJobHostManager jobHostManager, ILogger<FunctionsHostGrpcService> logger) : IFunctionsHostGrpcService
{
    private readonly ILogger<FunctionsHostGrpcService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IJobHostManager _jobHostManager = jobHostManager ?? throw new ArgumentNullException(nameof(jobHostManager));

    private readonly Dictionary<RuntimeEnvironment, Channel<FunctionsGrpcMessage>> _placeholderChannels = [];

    public async IAsyncEnumerable<FunctionsGrpcMessage> StartStreamAsync(IAsyncEnumerable<FunctionsGrpcMessage> requests)
    {
        // move to the first message
        var enumerator = requests.GetAsyncEnumerator();
        await enumerator.MoveNextAsync();

        // first request must be StartStream so populate details
        var startStream = enumerator.Current;

        var environment = new RuntimeEnvironment(startStream.Properties["Runtime"], startStream.Properties["Version"],
            startStream.Properties["Architecture"], bool.Parse(startStream.Properties["IsPlaceholder"]));

        var workerState = new WorkerState(startStream.Properties["ApplicationId"], startStream.Properties["ApplicationVersion"], startStream.Id, environment)
        {
            Capabilities = startStream.Properties["Capabilities"].Split(';')
        };

        Channel<FunctionsGrpcMessage> channel = Channel.CreateUnbounded<FunctionsGrpcMessage>();

        if (environment.IsPlaceholder)
        {
            // Need to expose these to re-use when specialization occurs
            _placeholderChannels[environment] = channel;
        }

        // Send a metadata request
        channel.Writer.TryWrite(new FunctionsGrpcMessage { MessageType = FunctionsGrpcMessage.MetadataRequest, Id = workerState.WorkerId });

        // Messages for the JobHost get written here.
        var jobHostChannel = Channel.CreateUnbounded<FunctionsGrpcMessage>();
        _ = ReadFromWorkerAsync(AsAsyncEnumerable(enumerator), channel.Writer, workerState);

        async Task ReadFromWorkerAsync(IAsyncEnumerable<FunctionsGrpcMessage> incoming, ChannelWriter<FunctionsGrpcMessage> channelWriter, WorkerState workerState)
        {
            await foreach (var req in incoming)
            {
                switch (req.MessageType)
                {
                    case FunctionsGrpcMessage.MetadataResponse:
                        // This is where we create the channel interface to a JobHost/Invoker for this worker. Some incoming messages
                        // are handled here at the WebHost level, but things like RpcLog, InvocationRequests, etc, need to go to the 
                        // JobHost/Invoker for processing.     
                        _ = HandleMetadataResponseAsync(req, workerState, jobHostChannel.Reader, channelWriter);
                        break;
                    case FunctionsGrpcMessage.InvocationResponse:
                        // Write to the JobHost's channel for processing
                        jobHostChannel.Writer.TryWrite(req);
                        break;
                    case FunctionsGrpcMessage.EnvironmentReloadResponse:
                        workerState.ApplicationVersion = req.Properties["ApplicationVersion"];
                        workerState.ApplicationId = req.Properties["ApplicationId"];
                        workerState.RuntimeEnvironment.IsPlaceholder = false;
                        workerState.Status = WorkerStatus.Running;

                        _ = HandleEnvironmentReloadResponseAsync(req, workerState, channelWriter);
                        break;
                    default:
                        throw new InvalidOperationException();
                }
            }

            // TODO: drain?

            _logger.LogInformation("Worker {WorkerId} has finished processing messages.", workerState.WorkerId);

            // Remove this handler from the load balancer
            var jobHost = (await _jobHostManager.GetJobHostAsync(workerState.ApplicationId))!;
            var handlerManager = jobHost.Services.GetRequiredService<IInvocationHandlerManager>();
            var handlers = handlerManager.GetHandlers(workerState.ApplicationId);
            var handlerToRemove = handlers.First(h => h.WorkerId == workerState.WorkerId);
            handlerManager.RemoveHandler(handlerToRemove);

            channelWriter.TryComplete();
            workerState.Status = WorkerStatus.Stopped;

            // if that was the last one, stop and remove the JobHost
            if (handlers.Count == 1)
            {
                _logger.LogInformation("Stopping JobHost for application {ApplicationId} as no more handlers are available.", workerState.ApplicationId);
                await _jobHostManager.StopJobHostAsync(workerState.ApplicationId);
            }
        }

        while (await channel.Reader.WaitToReadAsync())
        {
            while (channel.Reader.TryRead(out var message))
            {
                // Send messages to worker via grpc stream
                yield return message;
            }
        }

        // helper
        static async IAsyncEnumerable<FunctionsGrpcMessage> AsAsyncEnumerable(IAsyncEnumerator<FunctionsGrpcMessage> source)
        {
            try
            {
                while (await source.MoveNextAsync())
                {
                    yield return source.Current;
                }
            }
            finally
            {
                await source.DisposeAsync();
            }
        }
    }

    private Task HandleEnvironmentReloadResponseAsync(FunctionsGrpcMessage req, WorkerState workerState, ChannelWriter<FunctionsGrpcMessage> channelWriter)
    {
        // now go get the new worker's functions so we can start a JobHost
        channelWriter.TryWrite(new FunctionsGrpcMessage { MessageType = FunctionsGrpcMessage.MetadataRequest, Id = workerState.WorkerId });
        return Task.CompletedTask;
    }

    private async Task HandleMetadataResponseAsync(FunctionsGrpcMessage response, WorkerState workerState, ChannelReader<FunctionsGrpcMessage> readFromWorker, ChannelWriter<FunctionsGrpcMessage> writeToWorker)
    {
        var jobHost = await _jobHostManager.GetOrAddJobHostAsync(workerState.ApplicationId, () =>
        {
            var context = new JobHostStartContext(workerState.ApplicationId, workerState.ApplicationVersion);
            context.FunctionMetadata = response.Properties.Keys; // dumb mock
            return context;
        },
        services =>
        {
            // register our provider that knows how to use the grpc details below
            services.AddSingleton<IInvocationHandlerProvider, GrpcInvocationHandlerProvider>();
        });

        var handlerManager = jobHost.Services.GetRequiredService<IInvocationHandlerManager>();
        await handlerManager.AddHandlerAsync(new HandlerCreationContext(workerState.ApplicationId, workerState.ApplicationVersion, workerState.Capabilities)
        {
            Properties =
            {
                [GrpcInvocationHandlerProvider.WorkerIdKey] = workerState.WorkerId,
                [GrpcInvocationHandlerProvider.WorkerChannelWriterKey] = writeToWorker,
                [GrpcInvocationHandlerProvider.WorkerChannelReaderKey] = readFromWorker
            }
        });
    }

    public Task SpecializeWorkerAsync(ApplicationContext appContext, RuntimeEnvironment environment)
    {
        if (_placeholderChannels.Remove(environment, out var workerChannelToSpecialize))
        {
            // We have a channel for this environment, so we can specialize it
            _logger.LogInformation("Specializing worker for environment: {Environment}", environment);

            // Send a specialization message to the channel
            workerChannelToSpecialize.Writer.TryWrite(new FunctionsGrpcMessage
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
        foreach ((var env, var workerChannelToShutdown) in _placeholderChannels)
        {
            _logger.LogInformation("Sending shutdown message to placeholder worker for environment: {Environment}", env);

            workerChannelToShutdown.Writer.TryWrite(new FunctionsGrpcMessage
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

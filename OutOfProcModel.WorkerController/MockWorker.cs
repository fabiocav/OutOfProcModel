using Grpc.Core;
using Grpc.Net.Client;
using OutOfProcModel.Grpc.Abstractions;
using ProtoBuf.Grpc.Client;
using System.Threading.Channels;

namespace OutOfProcModel.WorkerController;

internal class MockWorker(ControllerWorkerDefinition workerDef, IConfiguration configuration, ILogger<MockWorker> logger)
{
    private readonly ControllerWorkerDefinition _workerDef = workerDef ?? throw new ArgumentNullException(nameof(workerDef));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILogger<MockWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task StartAsync()
    {
        _ = StartNewWorkerAsync(_workerDef);
        return Task.CompletedTask;
    }

    // This loop represents a single worker running
    private async Task StartNewWorkerAsync(ControllerWorkerDefinition workerDef)
    {
        var hostUriPath = ConfigurationPath.Combine("services", "functionshost", "http", "0");
        var address = _configuration[hostUriPath];

        GrpcClientFactory.AllowUnencryptedHttp2 = true;
        var http = GrpcChannel.ForAddress(address);
        var hostService = http.CreateGrpcService<IFunctionsHostGrpcService>();

        // Create a stream for the worker
        var outgoing = Channel.CreateUnbounded<GrpcFromWorker>();
        //var incoming = Channel.CreateUnbounded<GrpcToWorker>();

        async IAsyncEnumerable<GrpcFromWorker> GetStream(Channel<GrpcFromWorker> channel)
        {
            while (await channel.Reader.WaitToReadAsync())
            {
                while (channel.Reader.TryRead(out var message))
                {
                    yield return message;
                }
            }
        }

        // Start the stream
        outgoing.Writer.TryWrite(new GrpcFromWorker
        {
            MessageType = FunctionsGrpcMessage.StartStream,
            Id = workerDef.WorkerId,
            Properties = new Dictionary<string, string>
                {
                    {"ApplicationId", workerDef.ApplicationContext.ApplicationId },
                    {"ApplicationVersion", workerDef.ApplicationContext.ApplicationVersion },
                    {"Runtime", workerDef.RuntimeEnvironment.Runtime },
                    {"Version", workerDef.RuntimeEnvironment.Version },
                    {"Architecture", workerDef.RuntimeEnvironment.Architecture },
                    {"IsPlaceholder", workerDef.RuntimeEnvironment.IsPlaceholder.ToString() },
                    {"Capabilities", "WorkerIndexingEnabled;HttpProxyEnabled" } // mock capabilities
                }
        });

        try
        {
            await foreach (var functionsGrpcMessage in hostService.StartStreamAsync(GetStream(outgoing)))
            {
                switch (functionsGrpcMessage.MessageType)
                {
                    case FunctionsGrpcMessage.MetadataRequest:
                        outgoing.Writer.TryWrite(new GrpcFromWorker
                        {
                            MessageType = FunctionsGrpcMessage.MetadataResponse,
                            Id = workerDef.WorkerId,
                            Properties = new Dictionary<string, string>
                                {
                                    { "HttpTrigger1", "random_metadata" },
                                    { "TimerTrigger1", "random_metadata" }
                                }
                        });
                        break;
                    case FunctionsGrpcMessage.InvocationRequest:
                        // this is the worker doing stuff... simulate some delay
                        var delay = Random.Shared.Next(100, 2000);
                        await Task.Delay(delay);
                        var invocationId = functionsGrpcMessage.Properties[FunctionsGrpcMessage.FunctionInvocationId];
                        outgoing.Writer.TryWrite(new GrpcFromWorker
                        {
                            MessageType = FunctionsGrpcMessage.InvocationResponse,
                            Id = workerDef.WorkerId,
                            Properties = new Dictionary<string, string>
                                {
                                    { FunctionsGrpcMessage.FunctionInvocationId, invocationId },
                                    { "Result", "random_result" }
                                }
                        });
                        break;
                    case FunctionsGrpcMessage.EnvironmentReloadRequest:
                        workerDef = workerDef.Specialize(functionsGrpcMessage.Properties["ApplicationId"], functionsGrpcMessage.Properties["ApplicationVersion"]);
                        outgoing.Writer.TryWrite(new GrpcFromWorker
                        {
                            MessageType = FunctionsGrpcMessage.EnvironmentReloadResponse,
                            Id = workerDef.WorkerId,
                            Properties = new Dictionary<string, string>
                                {
                                    {"ApplicationId", workerDef.ApplicationContext.ApplicationId},
                                    {"ApplicationVersion", workerDef.ApplicationContext.ApplicationVersion},
                                    {"WorkerId", workerDef.WorkerId},
                                    {"Runtime", workerDef.RuntimeEnvironment.Runtime },
                                    {"Version", workerDef.RuntimeEnvironment.Version },
                                    {"Architecture", workerDef.RuntimeEnvironment.Architecture },
                                    {"IsPlaceholder", workerDef.RuntimeEnvironment.IsPlaceholder.ToString() },
                                    {"Capabilities", "WorkerIndexingEnabled;HttpProxyEnabled" } // mock capabilities
                                }
                        });
                        break;
                    case FunctionsGrpcMessage.ShutdownMessage:
                        _logger.LogInformation("Received shutdown message. {WorkerId} | {Environment}", workerDef.WorkerId, workerDef.RuntimeEnvironment);
                        // do anything needed to gracefully shutdown the worker
                        outgoing.Writer.TryComplete();
                        await outgoing.Reader.Completion;
                        return;
                    default:
                        throw new InvalidOperationException();
                }
            }
        }
        catch (RpcException ex) { _logger.LogError(ex, "Error in worker loop"); }
        catch (OperationCanceledException) { }
    }
}

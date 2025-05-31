using System.Threading.Channels;
using Grpc.Core;
using Grpc.Net.Client;
using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.FunctionsHost.Grpc;
using ProtoBuf.Grpc.Client;

namespace OutOfProcModel.WorkerController;

public class MockWorker(WorkerState workerState, IConfiguration configuration, ILogger<MockWorker> logger)
{
    private readonly WorkerState _workerState = workerState ?? throw new ArgumentNullException(nameof(workerState));
    private readonly IConfiguration _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    private readonly ILogger<MockWorker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public Task StartAsync()
    {
        _ = StartNewWorkerAsync(_workerState);
        return Task.CompletedTask;
    }

    // This loop represents a single worker running
    private async Task StartNewWorkerAsync(WorkerState workerState)
    {
        var hostUriPath = ConfigurationPath.Combine("services", "functionshost", "http", "0");
        var address = _configuration[hostUriPath];

        GrpcClientFactory.AllowUnencryptedHttp2 = true;
        var http = GrpcChannel.ForAddress(address);
        var hostService = http.CreateGrpcService<IFunctionsHostGrpcService>();

        // Create a channel for the worker
        var channel = Channel.CreateUnbounded<FunctionsGrpcMessage>();

        async IAsyncEnumerable<FunctionsGrpcMessage> GetStream(Channel<FunctionsGrpcMessage> channel)
        {
            // take any message written to this channel and send it back to the functions host
            while (await channel.Reader.WaitToReadAsync())
            {
                if (channel.Reader.TryRead(out var message))
                {
                    yield return message;
                }
            }
        }

        // Start the stream
        channel.Writer.TryWrite(new FunctionsGrpcMessage
        {
            MessageType = FunctionsGrpcMessage.StartStream,
            Id = workerState.WorkerId,
            Properties = new Dictionary<string, string>
                {
                    {"ApplicationId", workerState.ApplicationId },
                    {"ApplicationVersion", workerState.ApplicationVersion },
                    {"Runtime", workerState.RuntimeEnvironment.Runtime },
                    {"Version", workerState.RuntimeEnvironment.Version },
                    {"Architecture", workerState.RuntimeEnvironment.Architecture },
                    {"IsPlaceholder", workerState.RuntimeEnvironment.IsPlaceholder.ToString() },
                    {"Capabilities", "WorkerIndexingEnabled;HttpProxyEnabled" } // mock capabilities
                }
        });

        try
        {
            await foreach (var functionsGrpcMessage in hostService.StartStreamAsync(GetStream(channel)))
            {
                switch (functionsGrpcMessage.MessageType)
                {
                    case FunctionsGrpcMessage.MetadataRequest:
                        channel.Writer.TryWrite(new FunctionsGrpcMessage
                        {
                            MessageType = FunctionsGrpcMessage.MetadataResponse,
                            Id = workerState.WorkerId,
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
                        channel.Writer.TryWrite(new FunctionsGrpcMessage
                        {
                            MessageType = FunctionsGrpcMessage.InvocationResponse,
                            Id = workerState.WorkerId,
                            Properties = new Dictionary<string, string>
                                {
                                    { FunctionsGrpcMessage.FunctionInvocationId, invocationId },
                                    { "Result", "random_result" }
                                }
                        });
                        break;
                    case FunctionsGrpcMessage.EnvironmentReloadRequest:
                        workerState.RuntimeEnvironment.IsPlaceholder = false;
                        workerState.ApplicationId = functionsGrpcMessage.Properties["ApplicationId"];
                        workerState.ApplicationVersion = functionsGrpcMessage.Properties["ApplicationVersion"];

                        channel.Writer.TryWrite(new FunctionsGrpcMessage
                        {
                            MessageType = FunctionsGrpcMessage.EnvironmentReloadResponse,
                            Id = workerState.WorkerId,
                            Properties = new Dictionary<string, string>
                                {
                                    {"ApplicationId", workerState.ApplicationId},
                                    {"ApplicationVersion", workerState.ApplicationVersion}
                                }
                        });
                        break;
                    case FunctionsGrpcMessage.ShutdownMessage:
                        _logger.LogInformation("Received shutdown message. {WorkerId} | {Environment}", workerState.WorkerId, workerState.RuntimeEnvironment);
                        // do anything needed to gracefully shutdown the worker
                        channel.Writer.TryComplete();
                        await channel.Reader.Completion;
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

//using System.Threading.Channels;
//using Microsoft.Extensions.DependencyInjection;
//using Microsoft.Extensions.Hosting;
//using Microsoft.Extensions.Options;
//using OutOfProcModel.Abstractions.ControlPlane;
//using OutOfProcModel.Abstractions.Core;
//using OutOfProcModel.Grpc;

namespace OutOfProcModel.Mock;

public class WebHostOptions
{
    public bool IsPlaceholderMode { get; set; } = false;
}

//// This simulates how the grpc worker will operate with connections, specialization, etc
//public class ConsoleMockListener(MockGrpcService mockGrpcService, IJobHostManager manager, IOptionsMonitor<WebHostOptions> options,
//    IOptionsMonitorCache<WebHostOptions> optionsCache, DefaultWorkerController workerController) : IHostedService
//{
//    private readonly IOptionsMonitor<WebHostOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
//    private readonly IOptionsMonitorCache<WebHostOptions> _optionsCache = optionsCache ?? throw new ArgumentNullException(nameof(optionsCache));
//    private readonly DefaultWorkerController _workerController = workerController ?? throw new ArgumentNullException(nameof(workerController));
//    private readonly IJobHostManager _jobHostManager = manager ?? throw new ArgumentNullException(nameof(manager));

//    internal static HashSet<string> AppNames = [];

//    private static string[] capabilities = ["WorkerIndexing", "HandlesWorkerTerminate"];

//    private Dictionary<string, (ChannelWriter<MessageFromWorker> Writer, ChannelReader<MessageToWorker> Reader)> _workerStreams = [];

//    public void Stop()
//    {
//        Console.WriteLine("ConsoleMockListener stopped");
//    }

//    public Task StartAsync(CancellationToken cancellationToken)
//    {
//        _ = Task.Run(async () =>
//        {
//            Console.WriteLine("ConsoleMockListener started. Issue command...");
//            // Keep reading the console input until "exit" is entered:
//            while (true)
//            {
//                Console.WriteLine();
//                Console.WriteLine("Waiting for command: ");
//                Console.WriteLine("  'w {app} {count}'  -> sets target worker {count} for {app}");
//                Console.WriteLine("  'e {app}          -> sends event to {app}");
//                Console.WriteLine("  'sp {id}'         -> specializes application {app}");
//                Console.Write("> ");
//                var input = Console.ReadLine();

//                switch (input?.ToLowerInvariant())
//                {
//                    case string s when s.StartsWith("w "):
//                        // Simulate creating a worker:
//                        var inputs = s.Split(" ", StringSplitOptions.RemoveEmptyEntries);
//                        var inputAppId = inputs[1].Trim();
//                        var inputCount = int.Parse(inputs[2].Trim());
//                        Console.WriteLine($"Setting target count of '{inputCount}' for app '{input}'");
//                        _workerController.SetWorkerTarget(inputAppId, inputCount);
//                        break;
//                    case string s when s.StartsWith("e "):
//                        var appId = s[1..].Trim();
//                        var jobHost = _jobHostManager.GetJobHostAsync(appId)!;
//                        var processor = jobHost.Services.GetRequiredService<IEventProcessor>();
//                        var result = await processor.ProcessEvent(new EventContext(appId, new InvocationContext(Guid.NewGuid().ToString(), "random_data")));
//                        Console.WriteLine($"Event complete: {result.InvocationResult.InvocationId} -> {result.InvocationResult.Result}");
//                        break;
//                    case string s when s.StartsWith("sp "):
//                        // Simulate specialization:
//                        Console.WriteLine("Specializing application with input: {input}");
//                        Environment.SetEnvironmentVariable("IsPlaceholderMode", null);
//                        _optionsCache.Clear(); // forces re-evaluation of options
//                        await mockGrpcService.SpecializeApplicationAsync(s[2..].Trim(), "1.0.0", capabilities);
//                        break;
//                    default:
//                        break;
//                }
//            }
//        }, cancellationToken);

//        return Task.CompletedTask;
//    }

//    public Task StopAsync(CancellationToken cancellationToken)
//    {
//        Stop();
//        return Task.CompletedTask;
//    }
//}
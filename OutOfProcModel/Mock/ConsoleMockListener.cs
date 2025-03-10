using Microsoft.Extensions.Hosting;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Mock;

public class ConsoleMockListener(IWorkerFactory workerFactory, IWorkerManager workerManager) : IHostedService
{
    private readonly IWorkerManager _workerManager = workerManager;
    private readonly IWorkerFactory _workerFactory = workerFactory;

    public void Stop()
    {
        Console.WriteLine("ConsoleMockListener stopped");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            Console.WriteLine("ConsoleMockListener started. Issue command to create worker...");
            // Keep reading the console input until "exit" is entered:
            while (true)
            {
                Console.WriteLine("Waiting for worker creation command...");
                var input = Console.ReadLine();
                
                if (!string.IsNullOrEmpty(input) && !input.StartsWith("worker", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Simulate creating a worker:
                Console.WriteLine($"Creating worker with input: {input}");

                var workerId = input.Substring("worker".Length).Trim();
                var context = new WorkerCreationContext(new ConsoleMockChannel(workerId), "")
                {
                    Properties =
                    {
                        ["workerid"] = workerId
                    }
                };

                var worker = await _workerFactory.Create(context);

                _workerManager.AddWorker(worker);
            }
        }, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Stop();
        return Task.CompletedTask;
    }
}
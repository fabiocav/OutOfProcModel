using Microsoft.Extensions.Hosting;
using OutOfProcModel.Abstractions.Core;

namespace OutOfProcModel.Mock;

public class EventGenerator(IEventProcessor eventProcessor) : IHostedService
{
    private readonly IEventProcessor _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            // Invoke the event processor:
            var eventProcessor = _eventProcessor;

            for (int i = 0; i < 1000; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await Task.Delay(1000, cancellationToken);
                    WriteToConsole(new string(' ', 25));

                    var result = await eventProcessor.ProcessEvent<object>(i.ToString());
                }
                catch
                {
                    WriteToConsole("Unable to process event.");
                }
            }

            void WriteToConsole(string message)
            {
                var a = Console.GetCursorPosition();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.SetCursorPosition(Console.WindowWidth - 25, 0);
                Console.Write(message);
                Console.ResetColor();
                Console.SetCursorPosition(a.Left, a.Top);
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
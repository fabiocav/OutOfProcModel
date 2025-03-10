using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Mock;

public class ConsoleMockChannel(string workerId) : IWorkerChannel
{
    public ChannelState State { get; private set; } = ChannelState.Created;

    public string ChannelType { get; } = "ConsoleMock";

    public void Start()
    {
        State = ChannelState.Started;
    }

    public ValueTask SendAsync(string message)
    {
        var a = Console.GetCursorPosition();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.SetCursorPosition(Console.WindowWidth - 30, 0);
        Console.Write($"Processed input {message} by {workerId}");
        Console.ResetColor();
        Console.SetCursorPosition(a.Left, a.Top);

        return ValueTask.CompletedTask;
    }
}
using OutOfProcModel.Abstractions.Core;

namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerChannel
{
    ChannelState State { get; }

    string ChannelType { get; }

    IAsyncEnumerable<MessageFromWorker> ReadAsync();

    ValueTask SendAsync(InvocationContext context);
}
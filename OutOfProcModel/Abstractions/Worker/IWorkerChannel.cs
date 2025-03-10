using OutOfProcModel.Abstractions.Core;

namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerChannel
{
    ChannelState State { get; }

    string ChannelType { get; }

    void Start();

    ValueTask SendAsync(string message);
}
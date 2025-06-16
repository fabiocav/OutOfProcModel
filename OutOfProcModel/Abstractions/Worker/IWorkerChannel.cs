namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerChannel : IWorkerChannelWriter
{
    IAsyncEnumerable<MessageFromWorker> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Signals that the worker is exiting and will not be using this channel again. Different IWorkerChannels may
    /// handle this call differently.
    /// In the case of specializable channels, an implementation can skip disconnection internally to allow for channel re-use.
    /// </summary>
    /// <returns>An awaitable Task.</returns>
    Task DisconnectAsync();
}

// Some services only need to be able to write. We only want a single reader
// in the JobHost, so this allows us to use the same channel but restrict it for some
// services.
// TODO: There's a subltety here. If the JobHost needs to write to the worker, it cannot use a channel
//       that is tied to a specific IWorker as that IWorker could get disposed, which would disconnect this
//       channel. We need to ensure that the JobHost can always write to a worker.
public interface IWorkerChannelWriter
{
    bool TryWrite(MessageToWorker message);
}
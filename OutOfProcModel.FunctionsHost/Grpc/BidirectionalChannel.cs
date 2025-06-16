using OutOfProcModel.Abstractions.Worker;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OutOfProcModel.FunctionsHost.Grpc;

// a class to hold the endpoints of our bidirectional channels and only expose the necessary
// interfaces for the host and worker to communicate with each other
public class BidirectionalChannel : IWorkerChannel
{
    // for messages going from Worker -> Host
    private readonly Channel<MessageFromWorker> _hostMessageChannel = Channel.CreateUnbounded<MessageFromWorker>();

    // for messages going from Host -> Worker
    private readonly Channel<MessageToWorker> _workerMessageChannel = Channel.CreateUnbounded<MessageToWorker>();

    private TaskCompletionSource? _specializationTcs;

    public ChannelReader<MessageToWorker> WorkerMessageReader => _workerMessageChannel.Reader;

    public ChannelWriter<MessageFromWorker> HostMessageWriter => _hostMessageChannel.Writer;

    public ChannelReader<MessageFromWorker> HostMessageReader => _hostMessageChannel.Reader;

    public ChannelWriter<MessageToWorker> WorkerMessageWriter => _workerMessageChannel.Writer;

    // Only the IWorker should call the IWorkerChannel implementations.
    Task IWorkerChannel.DisconnectAsync()
    {
        if (_specializationTcs is not null && !_specializationTcs.Task.IsCompleted)
        {
            // The one-time placeholder disconnect call occurred.We can now re-use this as a normal channel going forward.
            // The next time DisconnectAsync is called, this channel will disconnect normally.
            _specializationTcs.TrySetResult();
            return Task.CompletedTask;
        }

        _hostMessageChannel.Writer.TryComplete();
        _workerMessageChannel.Writer.TryComplete();
        return Task.WhenAll(_hostMessageChannel.Reader.Completion, _workerMessageChannel.Reader.Completion);
    }

    async IAsyncEnumerable<MessageFromWorker> IWorkerChannel.ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _hostMessageChannel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (!cancellationToken.IsCancellationRequested && _hostMessageChannel.Reader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    bool IWorkerChannelWriter.TryWrite(MessageToWorker message)
    {
        return _workerMessageChannel.Writer.TryWrite(message);
    }

    internal void MarkForSpecialization()
    {
        _specializationTcs = new TaskCompletionSource();
    }

    internal Task SpecializationCompletion => _specializationTcs?.Task ?? throw new InvalidOperationException("Specialization has not been requested for this channel.");
}

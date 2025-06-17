using OutOfProcModel.Abstractions.Worker;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorkerChannel : IWorkerChannel, IAsyncDisposable
{
    private readonly BidirectionalChannel _channel = new();

    public ChannelReader<MessageToWorker> WorkerMessageReader => _channel.WorkerMessageReader;

    public ChannelWriter<MessageFromWorker> HostMessageWriter => _channel.HostMessageWriter;

    public async IAsyncEnumerable<MessageFromWorker> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _channel.HostMessageReader.WaitToReadAsync(cancellationToken))
        {
            while (!cancellationToken.IsCancellationRequested && _channel.HostMessageReader.TryRead(out var message))
            {
                yield return message;
            }
        }
    }

    public bool TryWrite(MessageToWorker message)
    {
        return _channel.WorkerMessageWriter.TryWrite(message);
    }

    public async ValueTask DisposeAsync()
    {
        // this signals upstream that we are done writing messages
        _channel.HostMessageWriter.Complete();
        _channel.WorkerMessageWriter.Complete();
        await _channel.HostMessageReader.Completion;
        await _channel.WorkerMessageReader.Completion;
    }
}

// a class to hold the endpoints of our bidirectional channels
public class BidirectionalChannel
{
    // for messages going from Worker -> Host
    private readonly Channel<MessageFromWorker> _hostMessageChannel = Channel.CreateUnbounded<MessageFromWorker>();

    // for messages going from Host -> Worker
    private readonly Channel<MessageToWorker> _workerMessageChannel = Channel.CreateUnbounded<MessageToWorker>();

    public ChannelReader<MessageToWorker> WorkerMessageReader => _workerMessageChannel.Reader;

    public ChannelWriter<MessageToWorker> WorkerMessageWriter => _workerMessageChannel.Writer;

    public ChannelReader<MessageFromWorker> HostMessageReader => _hostMessageChannel.Reader;

    public ChannelWriter<MessageFromWorker> HostMessageWriter => _hostMessageChannel.Writer;
}

public class ChannelRouter(BidirectionalChannel sourceChannel) : IWorkerChannelFactory, IWorkerChannelWriterProvider
{
    private readonly BidirectionalChannel _sourceChannel = sourceChannel ?? throw new ArgumentNullException(nameof(sourceChannel));
    private readonly Dictionary<string, GrpcWorkerChannel> _appMap = new(StringComparer.OrdinalIgnoreCase);

    private Task? _routingTask;

    public void Start()
    {
        _routingTask = StartRoutingAsync();
    }

    private async Task StartRoutingAsync()
    {
        while (await _sourceChannel.HostMessageReader.WaitToReadAsync())
        {
            while (_sourceChannel.HostMessageReader.TryRead(out var message))
            {
                // route to appropriate iworker
                if (_appMap.TryGetValue(message.ApplicationId, out var channel))
                {
                    if (!channel.HostMessageWriter.TryWrite(message))
                    {
                        // handle failure to write, e.g., channel is full or closed
                    }
                }
                else
                {
                    // no worker found for this applicationId, handle accordingly
                }
            }
        }
    }

    public IWorkerChannel CreateWorkerChannel(string applicationId)
    {
        var channel = CreateChannel(applicationId);

        return new DisposableChannel(channel, () =>
        {
            // remove the channel from the map when disposed
            _appMap.Remove(applicationId);
        });
    }

    private GrpcWorkerChannel CreateChannel(string applicationId)
    {
        var channel = new GrpcWorkerChannel();

        _appMap[applicationId] = channel;

        // forward messages back to the source channel
        _ = Task.Run(async () =>
        {
            while (await channel.WorkerMessageReader.WaitToReadAsync())
            {
                while (channel.WorkerMessageReader.TryRead(out var message))
                {
                    // route to worker
                    _sourceChannel.WorkerMessageWriter.TryWrite(message);
                }
            }
        });

        return channel;
    }

    public IWorkerChannelWriter GetWriter(string applicationId)
    {
        if (!_appMap.TryGetValue(applicationId, out var channel))
        {
            channel = CreateChannel(applicationId);
        }
        // TODO: I don't like this...
        return channel;
    }

    private class DisposableChannel(IWorkerChannel source, Action onDispose) : IWorkerChannel, IAsyncDisposable
    {
        private readonly Action _onDispose = onDispose;
        private readonly IWorkerChannel _source = source;

        public IAsyncEnumerable<MessageFromWorker> ReadAsync(CancellationToken cancellationToken) => _source.ReadAsync(cancellationToken);

        public bool TryWrite(MessageToWorker message) => _source.TryWrite(message);

        public ValueTask DisposeAsync()
        {
            _onDispose();

            if (_source is IAsyncDisposable asyncDisposable)
            {
                return asyncDisposable.DisposeAsync();
            }

            return ValueTask.CompletedTask;
        }
    }
}

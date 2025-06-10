using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;
using System.Threading.Channels;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorkerChannel : IWorkerChannel, IMessageHandler
{
    private readonly Channel<MessageFromWorker> _incoming = Channel.CreateUnbounded<MessageFromWorker>();
    private readonly ChannelWriter<GrpcToWorker> _writeToWorker;

    private readonly ILogger<GrpcWorkerChannel> _logger;

    public GrpcWorkerChannel(MessageHandlerPipeline handlerPipeline, ChannelWriter<GrpcToWorker> writeToWorker, ILogger<GrpcWorkerChannel> logger)
    {
        handlerPipeline.AddHandler(this);
        _writeToWorker = writeToWorker ?? throw new ArgumentNullException(nameof(writeToWorker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ChannelState State { get; private set; } = ChannelState.Created;

    public string ChannelType { get; } = "grpc";

    public async ValueTask SendAsync(InvocationContext context)
    {
        await _writeToWorker.WriteAsync(new GrpcToWorker
        {
            MessageType = FunctionsGrpcMessage.InvocationRequest,
            Id = context.InvocationId,
            Properties =
            {
                [FunctionsGrpcMessage.FunctionInvocationId] = context.InvocationId,
                ["Data"] = context.Data,
            },
        });
    }

    public async IAsyncEnumerable<MessageFromWorker> ReadAsync()
    {
        while (await _incoming.Reader.WaitToReadAsync())
        {
            while (_incoming.Reader.TryRead(out MessageFromWorker? item))
            {
                yield return item;
            }
        }
    }

    public ValueTask<bool> HandleMessage(MessageFromWorker message)
    {
        if (message.MessageType == FunctionsGrpcMessage.InvocationResponse)
        {
            _incoming.Writer.TryWrite(message);
            return ValueTask.FromResult(true);
        }

        return ValueTask.FromResult(false);
    }
}

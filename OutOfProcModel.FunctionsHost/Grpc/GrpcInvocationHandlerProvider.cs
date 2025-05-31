using System.Threading.Channels;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcInvocationHandlerProvider(ILoggerFactory loggerFactory) : IInvocationHandlerProvider
{
    private readonly ILogger<GrpcInvocationHandler> _logger = loggerFactory.CreateLogger<GrpcInvocationHandler>();

    internal const string WorkerIdKey = "WorkerId";
    internal const string WorkerChannelWriterKey = "WorkerChannelWriter";
    internal const string WorkerChannelReaderKey = "WorkerChannelReader";

    public ValueTask<IInvocationHandler> Create(HandlerCreationContext context)
    {
        if (!context.Properties.TryGetValue(WorkerIdKey, out object? workerIdObj)
            || workerIdObj is not string workerId)
        {
            workerId = Guid.NewGuid().ToString();
        }

        if (!context.Properties.TryGetValue(WorkerChannelWriterKey, out object? writerObj)
            || writerObj is not ChannelWriter<FunctionsGrpcMessage> writer)
        {
            throw new InvalidOperationException("No channel writer provided. Cannot create handler.");
        }

        if (!context.Properties.TryGetValue(WorkerChannelReaderKey, out object? readerObj)
            || readerObj is not ChannelReader<FunctionsGrpcMessage> reader)
        {
            throw new InvalidOperationException("No channel reader provided. Cannot create handler.");
        }

        return new ValueTask<IInvocationHandler>(new GrpcInvocationHandler(context.ApplicationId, workerId, context.ApplicationVersion, writer, reader, _logger));
    }
}
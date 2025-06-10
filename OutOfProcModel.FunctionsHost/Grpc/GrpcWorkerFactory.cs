using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorkerFactory(IWorkerChannelFactory channelFactory, ILoggerFactory loggerFactory) : IWorkerFactory, IDisposable
{
    private readonly ILogger<GrpcWorker> _logger = loggerFactory.CreateLogger<GrpcWorker>();
    private readonly IWorkerChannelFactory _channelFactory = channelFactory;

    public ValueTask<IWorker> Create(WorkerCreationContext context)
    {
        var channel = _channelFactory.CreateWorkerChannel();
        var worker = new GrpcWorker(context.ApplicationId, context.WorkerId, context.ApplicationVersion, channel, _logger);

        return new ValueTask<IWorker>(worker);
    }

    public void Dispose()
    {
        (_channelFactory as IDisposable)?.Dispose();
    }
}
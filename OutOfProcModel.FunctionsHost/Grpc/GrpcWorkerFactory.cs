using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorkerFactory(IWorkerChannelFactory workerChannelFactory) : IWorkerFactory, IDisposable
{
    private readonly IWorkerChannelFactory _workerChannelFactory = workerChannelFactory ?? throw new ArgumentNullException(nameof(workerChannelFactory));

    public ValueTask<IWorker> Create(WorkerCreationContext context)
    {
        var worker = new GrpcWorker(context.Definition, _workerChannelFactory);

        return new ValueTask<IWorker>(worker);
    }

    public void Dispose()
    {
    }
}
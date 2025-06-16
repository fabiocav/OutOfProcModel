using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.FunctionsHost.Grpc;

internal class GrpcWorkerFactory : IWorkerFactory, IDisposable
{
    public ValueTask<IWorker> Create(WorkerCreationContext context)
    {
        var channel = (context.Properties["Channel"] as IWorkerChannel)!;
        var worker = new GrpcWorker(context.Definition, channel);

        return new ValueTask<IWorker>(worker);
    }

    public void Dispose()
    {
    }
}
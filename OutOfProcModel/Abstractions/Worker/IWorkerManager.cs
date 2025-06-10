namespace OutOfProcModel.Abstractions.Worker;

// JobHost-scoped. 
// Retrieved by WebHost-level Grpc server when a new connection is made.
public interface IWorkerManager
{
    ValueTask CreateWorkerAsync(WorkerCreationContext worker);

    bool RemoveWorker(IWorker worker);

    IReadOnlyCollection<IWorker> GetWorkers();
}
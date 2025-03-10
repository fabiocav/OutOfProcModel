namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerFactory
{
    ValueTask<IWorker> Create(WorkerCreationContext context);
}
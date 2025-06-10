namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerResolver
{
    IWorker? ResolveWorker(string context);
}
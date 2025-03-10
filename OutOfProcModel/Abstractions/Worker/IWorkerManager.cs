namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerManager
{
    void AddWorker(IWorker worker);

    bool RemoveWorker(IWorker worker);

    IReadOnlyCollection<IWorker> GetWorkers(string applicationId);
}
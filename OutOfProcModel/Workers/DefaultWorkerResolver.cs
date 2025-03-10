using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

public class DefaultWorkerResolver(IWorkerManager workerManager) : IWorkerResolver
{
    private int _lastWorkerIndex = -1;
    private readonly Lock _lock = new();

    public IWorker? ResolveWorker(string context)
    {
        var workers = workerManager.GetWorkers(string.Empty)
            .Where(w => w.Status == WorkerStatus.Running)
            .ToList();

        if (workers.Count == 0)
        {
            return null;
        }

        lock (_lock)
        {
            _lastWorkerIndex = (_lastWorkerIndex + 1) % workers.Count;
            return workers.ElementAt(_lastWorkerIndex);
        }
    }
}
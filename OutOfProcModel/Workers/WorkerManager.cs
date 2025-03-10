using System.Collections.Concurrent;
using System.Collections.Immutable;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

public class WorkerManager : IWorkerManager
{
    // Dictionary mapping applicationId to a list of workers:
    private readonly ConcurrentDictionary<string, IList<IWorker>> _workers = new();

    public void AddWorker(IWorker worker)
    {
        var appWorkers = _workers.GetOrAdd(worker.ApplicationId, new List<IWorker>());
        appWorkers.Add(worker);
    }

    public bool RemoveWorker(IWorker worker)
    {
        var appWorkers = _workers.GetOrAdd(worker.ApplicationId, new List<IWorker>());
        return appWorkers.Remove(worker);
    }

    public IReadOnlyCollection<IWorker> GetWorkers(string applicationId)
    {
        if (_workers.TryGetValue(applicationId, out var workers))
        {
            return workers.AsReadOnly();
        }

        return ImmutableArray<IWorker>.Empty;
    }
}
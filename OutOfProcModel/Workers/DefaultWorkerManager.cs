using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

internal class DefaultWorkerManager : IWorkerManager, IDisposable
{
    // Dictionary mapping applicationId to a list of workers:
    private readonly IList<IWorker> _workers = [];

    private IWorkerFactory _workerFactory;
    private readonly IWorkerChannelFactory _workerChannelFactory;

    public DefaultWorkerManager(IWorkerFactory workerFactory, IWorkerChannelFactory workerChannelFactory)
    {
        _workerFactory = workerFactory;
        _workerChannelFactory = workerChannelFactory;
    }

    public async ValueTask CreateWorkerAsync(WorkerCreationContext workerCreationContext)
    {
        // this is JobHost-scoped, so ensure that we own lifetime of workers fully
        var worker = await _workerFactory.Create(workerCreationContext);
        _workers.Add(worker);
    }

    public bool RemoveWorker(IWorker worker)
    {
        return _workers.Remove(worker);
    }

    public IReadOnlyCollection<IWorker> GetWorkers()
    {
        return _workers.AsReadOnly();
    }

    public void Dispose()
    {
        foreach (var worker in _workers)
        {
            // Dispose of the worker if it implements IDisposable
            if (worker is IDisposable disposableWorker)
            {
                disposableWorker.Dispose();
            }
        }
    }
}
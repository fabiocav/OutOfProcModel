using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

public class DefaultWorkerResolver(IInvocationHandlerManager workerManager) : IHandlerResolver
{
    private int _lastWorkerIndex = -1;
    private readonly Lock _lock = new();

    public IInvocationHandler? ResolveHandler(string context)
    {
        var workers = workerManager.GetHandlers(context)
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
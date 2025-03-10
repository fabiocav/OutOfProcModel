using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

public class WorkerEventProcessor(IWorkerResolver workerResolver) : IEventProcessor
{
    private readonly IWorkerResolver _workerResolver = workerResolver;

    public ValueTask<T> ProcessEvent<T>(string context)
    {
        var worker = _workerResolver.ResolveWorker(context);

        if (worker == null)
        {
            throw new InvalidOperationException("No worker available");
        }

        return worker.ProcessEvent<T>(context);
    }
}
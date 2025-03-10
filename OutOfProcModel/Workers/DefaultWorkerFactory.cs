using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

public class DefaultWorkerFactory : IWorkerFactory
{
    public ValueTask<IWorker> Create(WorkerCreationContext context)
    {
        if (!context.Properties.TryGetValue("workerid", out string workerId))
        {
            workerId = Guid.NewGuid().ToString();
        }

        return new ValueTask<IWorker>(new Worker(context.Channel, context.ApplicationId, workerId));
    }
}
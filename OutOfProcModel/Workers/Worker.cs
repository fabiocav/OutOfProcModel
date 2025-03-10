using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Workers;

public class Worker(IWorkerChannel channel, string applicationId, string id, string version = "") : IWorker
{
    public string Id { get; } = id;

    public string ApplicationId { get; } = applicationId;

    public string ApplicationVersion { get; } = version;

    public IWorkerChannel Channel { get; } = channel;

    public IEnumerable<string> Capabilities { get; } = new List<string>();

    public async ValueTask<T> ProcessEvent<T>(string context)
    {
        await Channel.SendAsync(context);

        return default(T);
    }

    public WorkerStatus Status { get; } = WorkerStatus.Running;
}
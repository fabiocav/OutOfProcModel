namespace OutOfProcModel.Abstractions.Worker;

public interface IWorker
{
    public string Id { get; }

    string ApplicationId { get; }

    string ApplicationVersion { get; }

    IWorkerChannel Channel { get; }

    IEnumerable<string> Capabilities { get; }

    ValueTask<T> ProcessEvent<T>(string context);

    WorkerStatus Status { get; }
}

public enum WorkerStatus
{
    Created,
    Initializing,
    Running,
    Draining,
    Stopped
}
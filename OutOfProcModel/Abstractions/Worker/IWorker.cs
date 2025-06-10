using OutOfProcModel.Abstractions.Core;

namespace OutOfProcModel.Abstractions.Worker;

public interface IWorker
{
    public string WorkerId { get; }

    string ApplicationId { get; }

    string ApplicationVersion { get; }

    // implementation detail?
    IEnumerable<string> Capabilities { get; }

    WorkerStatus Status { get; }

    // would messages from grpc call this also?
    ValueTask<InvocationResult> ProcessEvent(InvocationContext context);

    // Returns when all in-flight invocations have completed (or timeout is hit)
    Task DrainAsync(TimeSpan timeout);
}

public enum WorkerStatus
{
    None = 0,
    Created = 1,
    Initializing = 2,
    Running = 3,
    Draining = 4,
    Stopped = 5
}
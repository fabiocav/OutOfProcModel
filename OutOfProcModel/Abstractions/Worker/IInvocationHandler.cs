using OutOfProcModel.Abstractions.Core;

namespace OutOfProcModel.Abstractions.Worker;

public interface IInvocationHandler
{
    public string WorkerId { get; }

    string ApplicationId { get; }

    string ApplicationVersion { get; }

    // implementation detail?
    // IWorkerChannel Channel { get; }

    // implementation detail?
    IEnumerable<string> Capabilities { get; }

    // would messages from grpc call this also?
    ValueTask<InvocationResult> ProcessEvent(InvocationContext context);
}

public enum WorkerStatus
{
    None = 0,
    Created = 1,
    Initializing = 2,
    RunningAsPlaceholder = 3,
    Running = 4,
    Draining = 5,
    Stopped = 6
}
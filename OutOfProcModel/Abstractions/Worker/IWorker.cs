using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Mock;

namespace OutOfProcModel.Abstractions.Worker;

public interface IWorkerState
{
    WorkerDefinition Definition { get; }

    WorkerStatus Status { get; }
}

public interface IWorker : IWorkerState
{
    // would messages from grpc call this also?
    ValueTask<InvocationResult> ProcessEvent(InvocationContext context);

    // Returns when all in-flight invocations have completed (or timeout is hit)
    Task DrainAsync(TimeSpan timeout);
}

public enum WorkerStatus
{
    Created = 0,
    Initializing = 1,
    Initialized = 2,
    Running = 3,
    Draining = 4,
    Drained = 5,
    Stopping = 6,
    Stopped = 7
}
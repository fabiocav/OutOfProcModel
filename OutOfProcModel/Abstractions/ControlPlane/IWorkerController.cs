using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Abstractions.ControlPlane;

public interface IWorkerController
{
    Task UpdateWorkerStateAsync(string applicationId, string workerId, WorkerStatus status);
}

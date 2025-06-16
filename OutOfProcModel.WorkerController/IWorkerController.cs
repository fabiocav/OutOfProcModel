using OutOfProcModel.WorkerController;

namespace OutOfProcModel.Abstractions.ControlPlane;

internal interface IWorkerController
{
    Task UpdateWorkerStateAsync(string applicationId, string workerId, ControllerWorkerStatus status);
}

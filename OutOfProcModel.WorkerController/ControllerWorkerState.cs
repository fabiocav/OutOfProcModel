
using OutOfProcModel.Abstractions.ControlPlane;

namespace OutOfProcModel.WorkerController;

// Very similar to the host's internal worker state, but this is for the controller as it
// has different Statuses and doesn't care about things like Capabilities.
internal class ControllerWorkerState(ControllerWorkerDefinition definition)
{
    public ControllerWorkerDefinition Definition { get; } = definition;

    public ControllerWorkerStatus Status { get; set; } = ControllerWorkerStatus.Created;
}

internal record ControllerWorkerDefinition(ApplicationContext ApplicationContext, string WorkerId, RuntimeEnvironment RuntimeEnvironment)
{
    public ControllerWorkerDefinition Specialize(string applicationId, string applicationVersion)
    {
        var newRuntimeEnvironment = RuntimeEnvironment with
        {
            IsPlaceholder = false
        };
        return new ControllerWorkerDefinition(new ApplicationContext(applicationId, applicationVersion), WorkerId, newRuntimeEnvironment);
    }

}

internal enum ControllerWorkerStatus
{
    Created,
    Initializing,
    Initialized,
    RunningAsPlaceholder,
    Specializing,
    Running,
    Draining,
    Drained,
    Stopping,
    Stopped
}

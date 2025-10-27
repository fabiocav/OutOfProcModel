using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Abstractions.Mock;

public record WorkerDefinition(
    string WorkerId,
    string ApplicationId,
    string ApplicationVersion,
    IEnumerable<string> Capabilities,
    RuntimeEnvironment RuntimeEnvironment)
{
    public WorkerDefinition Specialize(string applicationId, string applicationVersion, IEnumerable<string> capabilities)
    {
        if (!RuntimeEnvironment.IsPlaceholder)
        {
            throw new InvalidOperationException("Cannot specialize a non-placeholder worker definition.");
        }

        // Clone the current definition with only updates to relevant propserties
        var newRuntimeEnvironment = RuntimeEnvironment with
        {
            IsPlaceholder = false,
        };

        return new WorkerDefinition(
            WorkerId: WorkerId,
            ApplicationId: applicationId,
            ApplicationVersion: applicationVersion,
            Capabilities: capabilities,
            RuntimeEnvironment: newRuntimeEnvironment);
    }
}

public class WorkerState(WorkerDefinition initialDefinition)
{
    public WorkerDefinition Definition { get; } = initialDefinition;

    public WorkerStatus Status { get; set; } = WorkerStatus.Created;

    public WorkerState Specialize(string applicationId, string applicationVersion, IEnumerable<string> capabilities)
    {
        return new WorkerState(Definition.Specialize(applicationId, applicationVersion, capabilities))
        {
            Status = Status
        };
    }
}
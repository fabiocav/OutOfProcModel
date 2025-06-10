using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Abstractions.ControlPlane;

public class WorkerState
{
    private WorkerStatus _status = WorkerStatus.None;

    public WorkerState(string applicationId, string applicationVersion, string workerId, RuntimeEnvironment runtimeEnvironment, IEnumerable<string> capabilities)
    {
        WorkerId = workerId;
        ApplicationId = applicationId;
        ApplicationVersion = applicationVersion;

        var now = DateTimeOffset.UtcNow;
        Created = now;
        LastHeartbeat = now;
        StateLastUpdated = now;

        RuntimeEnvironment = runtimeEnvironment;
        Capabilities = capabilities;
    }

    public string WorkerId { get; }

    public string ApplicationId { get; }

    public string ApplicationVersion { get; }

    public IEnumerable<string> Capabilities { get; }

    public RuntimeEnvironment RuntimeEnvironment { get; }

    public WorkerStatus Status
    {
        get
        {
            return _status;
        }
        set
        {
            _status = value;
            var now = DateTimeOffset.UtcNow;
            StateLastUpdated = now;
            LastHeartbeat = now;
        }
    }

    public DateTimeOffset StateLastUpdated { get; private set; }

    public DateTimeOffset Created { get; private set; }

    public DateTimeOffset LastHeartbeat { get; private set; }

    public WorkerState Specialize(string applicationId, string applicationVersion, IEnumerable<string> capabilities)
    {
        // Clone the current state with only relevant properties changed
        var newRuntimeEnvironment = new RuntimeEnvironment(RuntimeEnvironment.Runtime, RuntimeEnvironment.Version, RuntimeEnvironment.Architecture, isPlaceholder: false);
        var newState = new WorkerState(applicationId, applicationVersion, WorkerId, newRuntimeEnvironment, capabilities)
        {
            Status = Status,
            StateLastUpdated = StateLastUpdated,
            Created = Created,
            LastHeartbeat = LastHeartbeat
        };
        return newState;
    }
}

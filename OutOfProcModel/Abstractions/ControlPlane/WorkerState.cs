using OutOfProcModel.Abstractions.Worker;

namespace OutOfProcModel.Abstractions.ControlPlane;

public class WorkerState
{
    private WorkerStatus _status = WorkerStatus.None;

    public WorkerState(string applicationId, string applicationVersion, string workerId, RuntimeEnvironment runtimeEnvironment)
    {
        WorkerId = workerId;
        ApplicationId = applicationId;
        ApplicationVersion = applicationVersion;

        var now = DateTimeOffset.UtcNow;
        Created = now;
        LastHeartbeat = now;
        StateLastUpdated = now;

        RuntimeEnvironment = runtimeEnvironment;
    }

    public string WorkerId { get; }

    public string ApplicationId { get; set; }

    public string ApplicationVersion { get; set; }

    public string[] Capabilities { get; set; } = [];

    public RuntimeEnvironment RuntimeEnvironment { get; set; }

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

    public DateTimeOffset LastHeartbeat { get; set; }
}

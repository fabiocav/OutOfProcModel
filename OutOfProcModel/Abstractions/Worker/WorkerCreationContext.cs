namespace OutOfProcModel.Abstractions.Worker;

public class WorkerCreationContext(IWorkerChannel channel, string applicationId)
{
    public string ApplicationId { get; set; } = applicationId;

    public IWorkerChannel Channel { get; set; } = channel;

    public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
}
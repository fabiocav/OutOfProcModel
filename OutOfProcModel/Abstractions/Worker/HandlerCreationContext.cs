namespace OutOfProcModel.Abstractions.Worker;

public class HandlerCreationContext(string applicationId, string applicationVersion, IEnumerable<string> capabilities)
{
    public string ApplicationId { get; set; } = applicationId;

    public string ApplicationVersion { get; set; } = applicationVersion;

    public IEnumerable<string> Capabilities { get; set; } = capabilities;

    public IDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
}
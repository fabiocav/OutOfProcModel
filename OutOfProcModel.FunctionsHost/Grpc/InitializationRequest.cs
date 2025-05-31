using System.Runtime.Serialization;

namespace OutOfProcModel.FunctionsHost.Grpc;

[DataContract]
public class InitializationRequest
{
    [DataMember]
    public string WorkerId { get; set; } = string.Empty;
    public string ApplicationId { get; set; } = string.Empty;
    public string ApplicationVersion { get; set; } = string.Empty;
    public IEnumerable<string> Capabilities { get; set; } = Enumerable.Empty<string>();
    public IDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
}

using System.Runtime.Serialization;

namespace OutOfProcModel.FunctionsHost.Grpc;

[DataContract]
public class FunctionsGrpcMessage
{
    public const string StartStream = "StartStream";
    public const string MetadataRequest = "MetadataRequest";
    public const string MetadataResponse = "MetadataResponse";
    public const string InvocationRequest = "InvocationRequest";
    public const string InvocationResponse = "InvocationResponse";
    public const string EnvironmentReloadRequest = "EnvironmentReloadRequest";
    public const string EnvironmentReloadResponse = "EnvironmentReloadResponse";
    public const string ShutdownMessage = "ShutdownMessage";

    public const string FunctionInvocationId = "FunctionInvocationId";

    [DataMember(Order = 1)]
    public string MessageType { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string Id { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

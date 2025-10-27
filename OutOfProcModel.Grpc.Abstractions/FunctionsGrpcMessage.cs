using ProtoBuf;

namespace OutOfProcModel.Grpc.Abstractions;

[ProtoContract]
[ProtoInclude(100, typeof(GrpcToWorker))]
[ProtoInclude(101, typeof(GrpcFromWorker))]
public abstract class FunctionsGrpcMessage
{
    public const string StartStream = "StartStream";
    public const string HostDescription = "HostDescription";
    public const string MetadataRequest = "MetadataRequest";
    public const string MetadataResponse = "MetadataResponse";
    public const string InvocationRequest = "InvocationRequest";
    public const string InvocationResponse = "InvocationResponse";
    public const string EnvironmentReloadRequest = "EnvironmentReloadRequest";
    public const string EnvironmentReloadResponse = "EnvironmentReloadResponse";
    public const string ShutdownMessage = "ShutdownMessage";

    public const string FunctionInvocationId = "FunctionInvocationId";

    [ProtoMember(1)]
    public string MessageType { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string Id { get; set; } = string.Empty;

    [ProtoMember(3)]
    public IDictionary<string, string> Properties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

[ProtoContract]
public class GrpcToWorker : FunctionsGrpcMessage { }

[ProtoContract]
public class GrpcFromWorker : FunctionsGrpcMessage { }
using ProtoBuf;
using System.Threading.Channels;

namespace OutOfProcModel.FunctionsHost.Grpc;

[ProtoContract]
[ProtoInclude(100, typeof(GrpcToWorker))]
[ProtoInclude(101, typeof(GrpcFromWorker))]
public abstract class FunctionsGrpcMessage
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

    [ProtoMember(1)]
    public string MessageType { get; set; } = string.Empty;

    [ProtoMember(2)]
    public string Id { get; set; } = string.Empty;

    [ProtoMember(3)]
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

[ProtoContract]
public class GrpcToWorker : FunctionsGrpcMessage { }

[ProtoContract]
public class GrpcFromWorker : FunctionsGrpcMessage { }

internal class WorkerGrpcStream
{
    public Channel<GrpcToWorker> Outgoing { get; set; } = Channel.CreateUnbounded<GrpcToWorker>();

    public Channel<GrpcFromWorker> Incoming { get; set; } = Channel.CreateUnbounded<GrpcFromWorker>();

}
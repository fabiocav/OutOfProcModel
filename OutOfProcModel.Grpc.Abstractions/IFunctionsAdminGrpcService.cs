using OutOfProcModel.Abstractions.ControlPlane;
using ProtoBuf.Grpc.Configuration;
using System.Runtime.Serialization;

namespace OutOfProcModel.Grpc.Abstractions;

[Service]
public interface IFunctionsAdminGrpcService
{
    Task<InvokeResult> InvokeAsync(InvokeContext context);

    Task<string> GetStateAsync();

    Task SpecializeApplicationAsync(SpecializationContext context);
}

[DataContract]
public class InvokeContext
{
    [DataMember(Order = 1)]
    public string TriggerId { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string ApplicationId { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string Data { get; set; } = string.Empty;
}

[DataContract]
public class InvokeResult
{
    [DataMember(Order = 1)]
    public string InvocationId { get; set; } = string.Empty;

    [DataMember(Order = 2)]
    public string Result { get; set; } = string.Empty;

    [DataMember(Order = 3)]
    public string WorkerId { get; set; } = string.Empty;
}

public record SpecializationContext(string ApplicationId, string ApplicationVersion, RuntimeEnvironment RuntimeEnvironment);

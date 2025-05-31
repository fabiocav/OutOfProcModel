using System.Runtime.Serialization;
using System.ServiceModel;
using OutOfProcModel.Abstractions.ControlPlane;
using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Mock;

namespace OutOfProcModel.FunctionsHost.Grpc;

[ServiceContract]
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

[DataContract]
public class SpecializationContext
{
    [DataMember(Order = 1)]
    public ApplicationContext ApplicationContext { get; set; }

    [DataMember(Order = 2)]
    public RuntimeEnvironment RuntimeEnvironment { get; set; }
}

internal class FunctionsAdminGrpcService(FunctionsHostGrpcService functionsHost, IJobHostManager jobHostManager) : IFunctionsAdminGrpcService
{
    private readonly IJobHostManager _jobHostManager = jobHostManager ?? throw new ArgumentNullException(nameof(jobHostManager));
    private readonly FunctionsHostGrpcService _functionsHost = functionsHost ?? throw new ArgumentNullException(nameof(functionsHost));

    public Task<string> GetStateAsync()
    {
        return Task.FromResult(((JobHostManager)_jobHostManager).GetState());
    }

    public async Task<InvokeResult> InvokeAsync(InvokeContext context)
    {
        var jobHost = (await _jobHostManager.GetJobHostAsync(context.ApplicationId))!;
        var processor = jobHost.Services.GetRequiredService<IEventProcessor>();
        var result = await processor.ProcessEvent(new EventContext(context.ApplicationId, new InvocationContext($"i_{Guid.NewGuid().ToString()[..8]}", "random_data")));

        return new InvokeResult { InvocationId = result.InvocationResult.InvocationId, Result = result.InvocationResult.Result, WorkerId = result.WorkerId };
    }

    public Task SpecializeApplicationAsync(SpecializationContext context)
    {
        return _functionsHost.SpecializeWorkerAsync(context.ApplicationContext, context.RuntimeEnvironment);
    }
}

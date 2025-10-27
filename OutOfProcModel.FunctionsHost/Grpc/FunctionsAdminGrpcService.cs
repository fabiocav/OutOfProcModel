using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Grpc.Abstractions;
using OutOfProcModel.Mock;

namespace OutOfProcModel.FunctionsHost.Grpc;


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
        await _jobHostManager.TryGetJobHostAsync(context.ApplicationId, out var jobHost);

        if (jobHost == null)
        {
            return new InvokeResult { InvocationId = string.Empty }; // signal it failed to find the job host
        }

        var processor = jobHost.Services.GetRequiredService<IEventProcessor>();
        var result = await processor.ProcessEvent(new EventContext(context.ApplicationId, new InvocationContext($"i_{Guid.NewGuid().ToString()[..8]}", "random_data")));

        return new InvokeResult { InvocationId = result.InvocationResult.InvocationId, Result = result.InvocationResult.Result, WorkerId = result.WorkerId };
    }

    public Task SpecializeApplicationAsync(SpecializationContext context)
    {
        return _functionsHost.SpecializeWorkerAsync(context.ApplicationId, context.ApplicationVersion, context.RuntimeEnvironment);
    }
}

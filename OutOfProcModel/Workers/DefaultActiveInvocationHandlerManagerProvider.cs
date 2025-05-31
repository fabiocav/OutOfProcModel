using Microsoft.Extensions.DependencyInjection;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Mock;

namespace OutOfProcModel.Workers;

public class DefaultActiveInvocationHandlerManagerProvider(IJobHostManager manager) : IActiveInvocationHandlerManagerProvider
{
    private readonly IJobHostManager _manager = manager;

    // Just one for now, but we'll need to pull this out of the active JobHost.
    public async Task<IInvocationHandlerManager> GetActiveManagerAsync(string applicationId)
    {
        var jobHost = await _manager.GetJobHostAsync(applicationId) ??
            throw new InvalidOperationException($"No job host found for application ID '{applicationId}'.");

        var handlerManager = jobHost.Services.GetRequiredService<IInvocationHandlerManager>()!;
        return handlerManager;
    }
}

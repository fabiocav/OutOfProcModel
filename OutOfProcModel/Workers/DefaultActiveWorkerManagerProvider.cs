using Microsoft.Extensions.DependencyInjection;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Mock;

namespace OutOfProcModel.Workers;

public class DefaultActiveWorkerManagerProvider(IJobHostManager manager) : IActiveWorkerManagerProvider
{
    private readonly IJobHostManager _manager = manager;

    // Just one for now, but we'll need to pull this out of the active JobHost.
    public async Task<IWorkerManager> GetActiveManagerAsync(string applicationId)
    {
        if (!await _manager.TryGetJobHostAsync(applicationId, out var jobHost) || jobHost is null)
        {
            throw new InvalidOperationException($"No job host found for application ID '{applicationId}'.");
        }

        var handlerManager = jobHost.Services.GetRequiredService<IWorkerManager>()!;
        return handlerManager;
    }
}

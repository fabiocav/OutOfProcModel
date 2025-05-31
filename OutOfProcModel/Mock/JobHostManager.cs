using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.Workers;

namespace OutOfProcModel.Mock;

// mocking out to manage child containers
public class JobHostManager(IServiceCollection rootServices, IServiceProvider rootServiceProvider, ILogger<JobHostManager> logger) : IJobHostManager
{
    private readonly IServiceProvider _rootServiceProvider = rootServiceProvider ?? throw new ArgumentNullException(nameof(rootServiceProvider));
    private readonly IServiceCollection _rootServices = rootServices ?? throw new ArgumentNullException(nameof(rootServices));
    private readonly ILogger<JobHostManager> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ConcurrentDictionary<string, IHost> _jobHosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IHost> GetOrAddJobHostAsync(string applicationId, Func<JobHostStartContext> contextFactory, Action<IServiceCollection> configureServices)
    {
        var host = _jobHosts.GetOrAdd(applicationId, _ => BuildJobHost(contextFactory(), configureServices));
        await host.StartAsync();
        return host;
    }

    private IHost BuildJobHost(JobHostStartContext context, Action<IServiceCollection> configureServices)
    {
        var builder = new HostBuilder()
           .UseServiceProviderFactory(new JobHostScopedServiceProviderFactory(_rootServiceProvider, _rootServices))
           .ConfigureServices(services =>
           {
               // TODO: inject a MetadataProvider using the static details from context
               services.AddSingleton<IEventProcessor, WorkerEventProcessor>();
               services.AddSingleton<IHandlerResolver, DefaultWorkerResolver>();
               services.AddSingleton<IInvocationHandlerManager, DefaultInvocationHandlerManager>();
               configureServices(services);
           });

        return builder.Build();
    }

    public Task<IHost?> GetJobHostAsync(string applicationId)
    {
        return Task.FromResult(_jobHosts.TryGetValue(applicationId, out var host) ? host : null);
    }

    public async Task StopJobHostAsync(string applicationId)
    {
        if (_jobHosts.TryRemove(applicationId, out var host))
        {
            await host.StopAsync();
            host.Dispose();
            _logger.LogInformation("Stopped JobHost for application {ApplicationId}", applicationId);
        }
    }

    class Handler
    {
        public string Id { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }

    class AppState
    {
        public List<Handler> Handlers { get; } = [];
    }

    public string GetState()
    {
        Dictionary<string, AppState> appStates = [];

        foreach ((string applicationId, IHost jobHost) in _jobHosts)
        {
            var appState = new AppState();
            appStates[applicationId] = appState;

            var handlerManager = jobHost.Services.GetRequiredService<IInvocationHandlerManager>();
            var handlers = handlerManager.GetHandlers(applicationId);
            foreach (var handler in handlers)
            {
                appState.Handlers.Add(new Handler { Id = handler.WorkerId });
            }
        }

        return JsonSerializer.Serialize(appStates, new JsonSerializerOptions { WriteIndented = true });
    }
}

public interface IJobHostManager
{
    // gets or starts a new JobHost for this specific applicationId
    Task<IHost> GetOrAddJobHostAsync(string applicationId, Func<JobHostStartContext> contextFactory, Action<IServiceCollection> configureServices);

    Task<IHost?> GetJobHostAsync(string applicationId);

    // stops the JobHost and all kills all connected workers
    Task StopJobHostAsync(string applicationId);
}

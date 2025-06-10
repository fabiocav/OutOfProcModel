using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Trace;
using OutOfProcModel.Abstractions.Core;
using OutOfProcModel.Abstractions.Worker;
using OutOfProcModel.FunctionsHost.Grpc;
using OutOfProcModel.Workers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace OutOfProcModel.Mock;

// mocking out to manage child containers
public class JobHostManager(IServiceCollection rootServices, IServiceProvider rootServiceProvider, ILogger<JobHostManager> logger) : IJobHostManager
{
    private readonly IServiceProvider _rootServiceProvider = rootServiceProvider ?? throw new ArgumentNullException(nameof(rootServiceProvider));
    private readonly IServiceCollection _rootServices = rootServices ?? throw new ArgumentNullException(nameof(rootServices));
    private readonly ILogger<JobHostManager> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    private readonly ConcurrentDictionary<string, JobHost> _jobHosts = new(StringComparer.OrdinalIgnoreCase);

    public async Task<JobHost> GetOrAddJobHostAsync(string applicationId, Func<JobHostStartContext> contextFactory, Action<IServiceCollection> configureServices)
    {
        var jobHost = _jobHosts.GetOrAdd(applicationId, _ => new JobHost(BuildJobHost(contextFactory(), configureServices)));
        await jobHost.StartAsync();
        return jobHost;
    }

    private IHost BuildJobHost(JobHostStartContext context, Action<IServiceCollection> configureServices)
    {
        var builder = new HostBuilder()
           .UseServiceProviderFactory(new JobHostScopedServiceProviderFactory(_rootServiceProvider, _rootServices))
           .ConfigureServices(services =>
           {
               // TODO: inject a MetadataProvider using the static details from context
               services.AddSingleton<IEventProcessor, WorkerEventProcessor>();
               services.AddSingleton<IWorkerResolver, DefaultWorkerResolver>();
               services.AddSingleton<IWorkerManager, DefaultWorkerManager>();
               configureServices(services);
           });

        return builder.Build();
    }

    public Task<JobHost?> GetJobHostAsync(string applicationId)
    {
        return Task.FromResult(_jobHosts.TryGetValue(applicationId, out var host) ? host : null);
    }

    public Task RemoveJobHostAsync(string applicationId)
    {
        _jobHosts.Remove(applicationId, out _);
        return Task.CompletedTask;
    }

    public async Task AssignWorkerAsync(WorkerCreationContext context)
    {
        var jobHost = await GetJobHostAsync(context.ApplicationId);
        if (jobHost != null)
        {
            await jobHost.Services.GetRequiredService<IWorkerManager>().CreateWorkerAsync(context);
        }
    }

    public async Task HandleMessageAsync(MessageFromWorker message)
    {
        var jobHost = await GetJobHostAsync(message.ApplicationId);
        if (jobHost != null)
        {
            await jobHost.Services.GetRequiredService<MessageHandlerPipeline>().HandleMessage(message);
        }
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

    class Worker
    {
        public string Id { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }

    class AppState
    {
        public List<Worker> Workers { get; } = [];
    }

    public string GetState()
    {
        Dictionary<string, AppState> appStates = [];

        foreach ((string applicationId, IHost jobHost) in _jobHosts)
        {
            var appState = new AppState();
            appStates[applicationId] = appState;

            var workerManager = jobHost.Services.GetRequiredService<IWorkerManager>();
            var workers = workerManager.GetWorkers();
            foreach (var worker in workers)
            {
                appState.Workers.Add(new Worker { Id = worker.WorkerId });
            }
        }

        return JsonSerializer.Serialize(appStates, new JsonSerializerOptions { WriteIndented = true });
    }
}

public interface IJobHostManager
{
    // gets or starts a new JobHost for this specific applicationId
    Task<JobHost> GetOrAddJobHostAsync(string applicationId, Func<JobHostStartContext> contextFactory, Action<IServiceCollection> configureServices);

    Task<JobHost?> GetJobHostAsync(string applicationId);

    Task RemoveJobHostAsync(string applicationId);

    // Adds a worker to the appropriate JobHost for the context
    Task AssignWorkerAsync(WorkerCreationContext context);

    // Sends a message to the appropriate JobHost
    Task HandleMessageAsync(MessageFromWorker message);
}

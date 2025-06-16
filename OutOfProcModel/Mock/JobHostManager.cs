using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
               services.AddOptions<JobHostOptions>()
                        .Configure(options =>
                        {
                            options.ApplicationId = context.ApplicationId;
                            options.ApplicationVersion = context.ApplicationVersion;
                        });

               services.AddSingleton<MessageHandlerPipeline>();
               services.TryAddSingleton<IEventProcessor, WorkerEventProcessor>();
               services.TryAddSingleton<IWorkerResolver, DefaultWorkerResolver>();
               services.TryAddSingleton<IWorkerManager, DefaultWorkerManager>();
               services.AddOptions<FunctionsMetadata>()
                     .Configure<IFunctionMetadataFactory>((metadata, factory) =>
                     {
                     });

               services.AddHostedService<ListenerService>();
               services.AddSingleton<IWorkerResolver, DefaultWorkerResolver>();
               configureServices(services);
           });

        return builder.Build();
    }

    public Task<JobHost> GetJobHostAsync(string applicationId)
    {
        return Task.FromResult(_jobHosts[applicationId]);
    }

    public async Task RemoveJobHostAsync(string applicationId)
    {
        _jobHosts.Remove(applicationId, out var jobHost);
        await jobHost!.StopAsync();
        jobHost.Dispose();
    }

    public async Task AssignWorkerAsync(WorkerCreationContext context)
    {
        var jobHost = await GetJobHostAsync(context.Definition.ApplicationId);
        if (jobHost != null)
        {
            await jobHost.WorkerManager.CreateWorkerAsync(context);
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

    class AppState(string appId, string appVersion)
    {
        public string ApplicationId { get; set; } = appId;

        public string ApplicationVersion { get; set; } = appVersion;

        public List<Worker> Workers { get; } = [];
    }

    public string GetState()
    {
        List<AppState> jobHosts = [];

        foreach ((string applicationId, JobHost jobHost) in _jobHosts)
        {
            var opt = jobHost.Services.GetRequiredService<IOptions<JobHostOptions>>().Value;

            var appState = new AppState(opt.ApplicationId, opt.ApplicationVersion);
            jobHosts.Add(appState);

            var workerManager = jobHost.Services.GetRequiredService<IWorkerManager>();
            var workers = workerManager.GetWorkers();
            foreach (var worker in workers)
            {
                appState.Workers.Add(new Worker { Id = worker.Definition.WorkerId, Status = worker.Status.ToString() });
            }
        }

        return JsonSerializer.Serialize(new { JobHosts = jobHosts }, new JsonSerializerOptions { WriteIndented = true });
    }
}

public interface IJobHostManager
{
    // gets or starts a new JobHost for this specific applicationId
    Task<JobHost> GetOrAddJobHostAsync(string applicationId, Func<JobHostStartContext> contextFactory, Action<IServiceCollection> configureServices);

    Task<JobHost> GetJobHostAsync(string applicationId);

    Task RemoveJobHostAsync(string applicationId);

    // Sends a message to the appropriate JobHost
    Task HandleMessageAsync(MessageFromWorker message);
}

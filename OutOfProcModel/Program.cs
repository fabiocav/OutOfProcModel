// See https://aka.ms/new-console-template for more information

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

Console.WriteLine("Hello, World!");


// Build DI container:
var builder = new HostApplicationBuilder();

builder.Services.AddSingleton<IWorkerManager, WorkerManager>();
builder.Services.AddSingleton<IWorkerResolver, DefaultWorkerResolver>();
builder.Services.AddSingleton<IEventProcessor, WorkerEventProcessor>();

var host = builder.Build();

var workerManager = host.Services.GetService<IWorkerManager>();

// Add 1000 workers to the worker manager:
for (int i = 0; i < 1000; i++)
{
    workerManager.AddWorker(new Worker(string.Empty, $"Worker:{i}"));
}

var eventProcessor = host.Services.GetService<IEventProcessor>();

for (int i = 0; i < 1000; i++)
{

    var result = await eventProcessor.ProcessEvent<object>(i.ToString());
}

////
// 



public interface IWorker
{
    public string Id { get; }

    string ApplicationId { get; }

    ValueTask<T> ProcessEvent<T>(string context);

    // need a worker status (include draining)
}

public class Worker(string applicationId, string id) : IWorker
{
    public string Id { get; } = id;
    public string ApplicationId { get; } = applicationId;

    public ValueTask<T> ProcessEvent<T>(string context)
    {
        Console.WriteLine($"{context} handled by {Id}");

        return ValueTask.FromResult(default(T));
    }
}

public interface IWorkerManager
{
    void AddWorker(IWorker worker);

    bool RemoveWorker(IWorker worker);

    IReadOnlyCollection<IWorker> GetWorkers(string applicationId);
}

public class WorkerManager : IWorkerManager
{
    // Dictionary mapping applicationId to a list of workers:
    private readonly ConcurrentDictionary<string, IList<IWorker>> _workers = new();

    public void AddWorker(IWorker worker)
    {
        var appWorkers = _workers.GetOrAdd(worker.ApplicationId, new List<IWorker>());
        appWorkers.Add(worker);
    }

    public bool RemoveWorker(IWorker worker)
    {
        var appWorkers = _workers.GetOrAdd(worker.ApplicationId, new List<IWorker>());
        return appWorkers.Remove(worker);
    }

    public IReadOnlyCollection<IWorker> GetWorkers(string applicationId)
    {
        if (_workers.TryGetValue(applicationId, out var workers))
        {
            return workers.AsReadOnly();
        }

        return ImmutableArray<IWorker>.Empty;
    }
}

public interface IEventProcessor
{
    ValueTask<T> ProcessEvent<T>(string context);
}

// This could have different implementations depending on the load balancing algorithm used. 
public interface IWorkerResolver
{
    IWorker? ResolveWorker(string context);
}

public class DefaultWorkerResolver : IWorkerResolver
{
    private readonly IWorkerManager _workerManager;
    private readonly IReadOnlyCollection<IWorker> _workers;
    private int _lastWorkerIndex = -1;
    private readonly Lock _lock = new();

    public DefaultWorkerResolver(IWorkerManager workerManager)
    {
        _workerManager = workerManager;

        _workers = _workerManager.GetWorkers(string.Empty);
    }

    public IWorker? ResolveWorker(string context)
    {
        if (_workers.Count == 0)
        {
            return null;
        }

        lock (_lock)
        {
            _lastWorkerIndex = (_lastWorkerIndex + 1) % _workers.Count;
            return _workers.ElementAt(_lastWorkerIndex);
        }
    }
}

public class WorkerEventProcessor(IWorkerResolver workerResolver) : IEventProcessor
{
    private readonly IWorkerResolver _workerResolver = workerResolver;

    public ValueTask<T> ProcessEvent<T>(string context)
    {
        var worker = _workerResolver.ResolveWorker(context);

        if (worker == null)
        {
            throw new InvalidOperationException("No worker available");
        }

        return worker.ProcessEvent<T>(context);
    }
}


// Invocation flow:
// WebJobs -> Invoker -> EventProcessorManager.ProcessEvent(context)

// EventProcessorManager flow (default implementation):
//          ProcessEvent -> EventProcessorResolver (Worker based default) -> Processor.ProcessEvent(context);

// Default implementation of the worker based event processor:
//  -> ProcessEvent -> Resolver.ResolveWorker -> 

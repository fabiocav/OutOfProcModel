# Scale Controller Design

This document describes the changes required to the Scale Controller to support the new decoupled Runtime/Worker architecture. It focuses on placeholder setup, specialization, and scale-out scenarios, comparing the current model with the proposed new model.

## Table of Contents

1. [Current Model Overview](#current-model-overview)
2. [New Model Overview](#new-model-overview)
3. [Placeholder Setup](#placeholder-setup)
4. [Specialization Flow](#specialization-flow)
5. [Scale-Out](#scale-out)
6. [Resource Efficiency](#resource-efficiency)
7. [Tracking and State Management](#tracking-and-state-management)
8. [New gRPC Messages](#new-grpc-messages)
9. [Cold Start Considerations](#cold-start-considerations)
10. [Open Questions](#open-questions)

---

## Current Model Overview

### Architecture: Coupled Runtime + Worker

In the current Azure Functions architecture, the Runtime and Worker scale as a **single unit**:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Current Model: One Container = One Runtime + One Worker                │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌─────────────────────────────────────────┐                           │
│  │  Container A (Python)                   │                           │
│  │  ┌─────────────┐    ┌─────────────┐    │                           │
│  │  │   Runtime   │◄──►│   Worker    │    │                           │
│  │  │             │gRPC│  (Python)   │    │                           │
│  │  │  - Host     │    │  - Process  │    │                           │
│  │  │  - WebJobs  │    │  - Runtime  │    │                           │
│  │  │  - Triggers │    │  - Libs     │    │                           │
│  │  └─────────────┘    └─────────────┘    │                           │
│  └─────────────────────────────────────────┘                           │
│                                                                         │
│  ┌─────────────────────────────────────────┐                           │
│  │  Container B (Node.js)                  │                           │
│  │  ┌─────────────┐    ┌─────────────┐    │                           │
│  │  │   Runtime   │◄──►│   Worker    │    │                           │
│  │  │             │gRPC│  (Node.js)  │    │                           │
│  │  └─────────────┘    └─────────────┘    │                           │
│  └─────────────────────────────────────────┘                           │
│                                                                         │
│  ┌─────────────────────────────────────────┐                           │
│  │  Container C (.NET Isolated)            │                           │
│  │  ┌─────────────┐    ┌─────────────┐    │                           │
│  │  │   Runtime   │◄──►│   Worker    │    │                           │
│  │  │             │gRPC│  (.NET)     │    │                           │
│  │  └─────────────┘    └─────────────┘    │                           │
│  └─────────────────────────────────────────┘                           │
│                                                                         │
│  Each container is independent.                                         │
│  Each has its own Runtime overhead.                                     │
│  Language-specific placeholder pools.                                   │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Scale Controller Responsibilities (Current)

1. **Maintain placeholder pools** per language/version combination
2. **Select placeholder** when customer request arrives
3. **Specialize container** with customer payload and environment variables
4. **Monitor event sources** and trigger scale-out
5. **Track container assignments** (which container serves which customer)

### Current Placeholder Pool Strategy

The Scale Controller maintains separate pools for each language/version:

| Language | Version | Pool Size | Purpose |
|----------|---------|-----------|---------|
| Python | 3.9 | 10 | Ready for Python 3.9 apps |
| Python | 3.10 | 10 | Ready for Python 3.10 apps |
| Python | 3.11 | 15 | Ready for Python 3.11 apps (most popular) |
| Node.js | 18 | 12 | Ready for Node 18 apps |
| Node.js | 20 | 15 | Ready for Node 20 apps |
| .NET | 6.0 | 8 | Ready for .NET 6 isolated apps |
| .NET | 8.0 | 12 | Ready for .NET 8 isolated apps |
| Java | 11 | 5 | Ready for Java 11 apps |
| Java | 17 | 8 | Ready for Java 17 apps |
| PowerShell | 7.2 | 3 | Ready for PowerShell apps |
| PowerShell | 7.4 | 5 | Ready for PowerShell 7.4 apps |

**Total: ~103 placeholder containers** (each with full Runtime + Worker overhead)

---

## New Model Overview

### Architecture: Decoupled Runtime + Workers

In the new model, the Runtime and Workers are **separate containers** that can be composed independently:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  New Model: One Runtime + Many Workers                                  │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  Runtime Container                                               │   │
│  │  ┌───────────────────────────────────────────────────────────┐  │   │
│  │  │                      Runtime                               │  │   │
│  │  │  - gRPC Server (port 50051)                               │  │   │
│  │  │  - JobHost Manager (multiple JobHosts)                    │  │   │
│  │  │  - Worker Registry                                        │  │   │
│  │  │  - Message Router                                         │  │   │
│  │  └───────────────────────────────────────────────────────────┘  │   │
│  └──────────────────────────┬──────────────────────────────────────┘   │
│                             │                                           │
│                             │ gRPC (network)                            │
│        ┌────────────────────┼────────────────────┐                     │
│        │                    │                    │                     │
│        ▼                    ▼                    ▼                     │
│  ┌───────────┐        ┌───────────┐        ┌───────────┐              │
│  │ Worker A  │        │ Worker B  │        │ Worker C  │              │
│  │ (Python)  │        │ (Node.js) │        │ (.NET)    │              │
│  │           │        │           │        │           │              │
│  │ Sidecar   │        │ Sidecar   │        │ Sidecar   │              │
│  └───────────┘        └───────────┘        └───────────┘              │
│                                                                         │
│  Single Runtime serves multiple Workers.                                │
│  Workers can be different languages.                                    │
│  Runtime overhead shared across all Workers.                            │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Key Differences

| Aspect | Current Model | New Model |
|--------|---------------|-----------|
| Runtime:Worker ratio | 1:1 | 1:N |
| Placeholder pools | Per language/version | Mixed pool per Runtime |
| Specialization target | Entire container | Worker only (+ Runtime payload) |
| Scale unit | Container (Runtime + Worker) | Worker container only |
| Runtime overhead | Duplicated per container | Shared across Workers |

---

## Placeholder Setup

### Current Model: Single-Step Container Start

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Current Placeholder Setup                                              │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Scale Controller                                                       │
│        │                                                                │
│        │  For each language/version combo:                              │
│        │                                                                │
│        ▼                                                                │
│  ┌─────────────────────────────────────────────┐                       │
│  │  Start Container                            │                       │
│  │  - Image: python-3.11-runtime-worker        │                       │
│  │  - Contains: Runtime + Worker (coupled)     │                       │
│  │  - Warmup: Runtime warms up Worker          │                       │
│  │  - Status: Placeholder ready                │                       │
│  └─────────────────────────────────────────────┘                       │
│                                                                         │
│  Simple: One container start = one placeholder ready                    │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### New Model: Multi-Step Orchestration

The Scale Controller now needs to coordinate a multi-step process:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  New Placeholder Setup                                                  │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Scale Controller                                                       │
│        │                                                                │
│        │  Step 1: Start Runtime                                         │
│        │  ─────────────────────────                                     │
│        ▼                                                                │
│  ┌─────────────────────────────────────────────┐                       │
│  │  Start Runtime Container                    │                       │
│  │  - Image: functions-runtime:4.0             │                       │
│  │  - Assign port: 50051                       │                       │
│  │  - Configure: MAX_WORKERS=20                │                       │
│  │  - Status: Runtime ready, awaiting workers  │                       │
│  └─────────────────────────────────────────────┘                       │
│        │                                                                │
│        │  Step 2: Start Placeholder Workers                             │
│        │  ────────────────────────────────                              │
│        │  (can be parallel)                                             │
│        │                                                                │
│        ├──────────────────┬──────────────────┬─────────────────┐       │
│        ▼                  ▼                  ▼                 ▼       │
│  ┌───────────┐      ┌───────────┐      ┌───────────┐    ┌───────────┐ │
│  │ Worker    │      │ Worker    │      │ Worker    │    │ Worker    │ │
│  │ Python    │      │ Python    │      │ Node.js   │    │ .NET      │ │
│  │ 3.11      │      │ 3.11      │      │ 20        │    │ 8.0       │ │
│  │           │      │           │      │           │    │           │ │
│  │ ENV:      │      │ ENV:      │      │ ENV:      │    │ ENV:      │ │
│  │ RUNTIME=  │      │ RUNTIME=  │      │ RUNTIME=  │    │ RUNTIME=  │ │
│  │ runtime:  │      │ runtime:  │      │ runtime:  │    │ runtime:  │ │
│  │ 50051     │      │ 50051     │      │ 50051     │    │ 50051     │ │
│  └─────┬─────┘      └─────┬─────┘      └─────┬─────┘    └─────┬─────┘ │
│        │                  │                  │                │       │
│        └──────────────────┴─────────┬────────┴────────────────┘       │
│                                     │                                  │
│                                     ▼                                  │
│                          ┌─────────────────────┐                      │
│                          │  Runtime Container  │                      │
│                          │                     │                      │
│                          │  Workers connected: │                      │
│                          │  - 2x Python 3.11   │                      │
│                          │  - 1x Node.js 20    │                      │
│                          │  - 1x .NET 8.0      │                      │
│                          │                     │                      │
│                          │  Placeholder        │                      │
│                          │  JobHost active     │                      │
│                          └─────────────────────┘                      │
│                                                                         │
│  Step 3: Warmup                                                         │
│  ─────────────────                                                      │
│  Runtime routes warmup invocations to each Worker.                      │
│  All Workers warmed and ready for specialization.                       │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Scale Controller Placeholder Setup Algorithm

```csharp
public class PlaceholderSetupOrchestrator
{
    public async Task SetupPlaceholderPoolAsync(PlaceholderPoolConfig config)
    {
        // Step 1: Determine how many Runtimes to start
        var runtimeCount = CalculateRuntimeCount(config);
        
        _logger.LogInformation(
            "Setting up placeholder pool: {RuntimeCount} Runtimes, " +
            "{WorkerCount} Workers across {LanguageCount} languages",
            runtimeCount,
            config.TotalWorkers,
            config.Languages.Count);
        
        // Step 2: Start Runtimes with assigned ports
        var runtimes = new List<RuntimeInstance>();
        for (int i = 0; i < runtimeCount; i++)
        {
            var port = AllocatePort(); // e.g., 50051, 50052, ...
            
            var runtime = await StartRuntimeContainerAsync(new RuntimeConfig
            {
                Port = port,
                MaxWorkers = config.MaxWorkersPerRuntime,
                PlaceholderMode = true
            });
            
            runtimes.Add(runtime);
        }
        
        // Step 3: Distribute Workers across Runtimes
        var workerTasks = new List<Task<WorkerInstance>>();
        
        foreach (var workerSpec in config.WorkerSpecs)
        {
            // Select Runtime with capacity (round-robin or load-based)
            var targetRuntime = SelectRuntimeForWorker(runtimes, workerSpec);
            
            workerTasks.Add(StartPlaceholderWorkerAsync(
                workerSpec,
                targetRuntime));
        }
        
        // Step 4: Wait for all Workers to connect and warm up
        var workers = await Task.WhenAll(workerTasks);
        
        // Step 5: Verify warmup
        await VerifyAllWorkersWarmedAsync(runtimes, workers);
        
        _logger.LogInformation(
            "Placeholder pool ready: {RuntimeCount} Runtimes, " +
            "{WorkerCount} Workers warmed",
            runtimes.Count,
            workers.Length);
    }
    
    private async Task<WorkerInstance> StartPlaceholderWorkerAsync(
        WorkerSpec spec,
        RuntimeInstance runtime)
    {
        var worker = await _containerOrchestrator.StartContainerAsync(new ContainerConfig
        {
            Image = GetWorkerImage(spec.Language, spec.LanguageVersion),
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["RUNTIME_ENDPOINT"] = $"{runtime.Endpoint}:{runtime.Port}",
                ["FUNCTIONS_WORKER_RUNTIME"] = spec.Language,
                ["LANGUAGE_VERSION"] = spec.LanguageVersion,
                ["IS_PLACEHOLDER"] = "true",
                ["APPLICATION_ID"] = $"_Placeholder_{spec.Language}",
                ["METADATA_VERSION"] = "1.0.0",
                ["CODE_VERSION"] = "1.0.0",
                ["WORKER_AUTH_TOKEN"] = GeneratePlaceholderToken(runtime, spec)
            }
        });
        
        // Wait for Worker to connect to Runtime
        await WaitForWorkerConnectionAsync(runtime, worker);
        
        return worker;
    }
}
```

### Placeholder Workers: The "Special" Treatment

Placeholder Workers are treated specially by the Runtime:

1. **Same Application Identity**: All placeholder workers for a given Runtime share a logical application identity (`_Placeholder_{language}`)

2. **Single Placeholder JobHost**: Runtime creates one JobHost for all placeholder workers, regardless of language

3. **Warmup Distribution**: Runtime routes warmup invocations to each worker to ensure all are exercised

```csharp
// In Runtime: Placeholder Worker handling
public class PlaceholderJobHost : IJobHost
{
    private readonly List<WorkerState> _placeholderWorkers = new();
    private int _warmupIndex = 0;
    
    public void RegisterPlaceholderWorker(WorkerState worker)
    {
        _placeholderWorkers.Add(worker);
        
        _logger.LogInformation(
            "Placeholder worker registered: {WorkerId} ({Language} {Version})",
            worker.WorkerId,
            worker.Language,
            worker.LanguageVersion);
    }
    
    public async Task WarmupAllWorkersAsync()
    {
        // Send warmup invocations to each worker
        foreach (var worker in _placeholderWorkers)
        {
            await SendWarmupInvocationAsync(worker);
        }
    }
    
    public WorkerState GetNextWorkerForWarmup()
    {
        // Round-robin warmup distribution
        var worker = _placeholderWorkers[_warmupIndex];
        _warmupIndex = (_warmupIndex + 1) % _placeholderWorkers.Count;
        return worker;
    }
}
```

### How Many Placeholder Workers Per Runtime?

**Hypothesis**: A single Runtime can reasonably support **15-25 placeholder workers** based on:

| Factor | Consideration | Impact |
|--------|---------------|--------|
| **Memory** | Each worker connection: ~10-20 MB in Runtime | 25 workers ≈ 250-500 MB |
| **gRPC connections** | HTTP/2 multiplexing | Low overhead per connection |
| **Message routing** | Channel-based, O(1) lookup | Minimal CPU per message |
| **JobHost overhead** | Single placeholder JobHost | Shared across all workers |
| **Warmup traffic** | Periodic, not continuous | Low sustained load |

**Recommendation**: Start with **20 workers per Runtime**, monitor, and adjust.

```csharp
public class PlaceholderPoolConfig
{
    // Hypothesis: 20 placeholder workers per Runtime is sustainable
    public int MaxWorkersPerRuntime { get; set; } = 20;
    
    // Distribution across languages
    public Dictionary<string, int> WorkersPerLanguage { get; set; } = new()
    {
        ["python-3.11"] = 4,
        ["python-3.10"] = 2,
        ["node-20"] = 4,
        ["node-18"] = 2,
        ["dotnet-8.0"] = 3,
        ["dotnet-6.0"] = 2,
        ["java-17"] = 2,
        ["powershell-7.4"] = 1
    };
    // Total: 20 workers per Runtime
}
```

---

## Specialization Flow

### Current Model: Container-Level Specialization

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Current Specialization Flow                                            │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  1. Scale Controller selects placeholder container                      │
│                                                                         │
│  2. Injects customer payload + environment variables                    │
│     ┌─────────────────────────────────────────┐                        │
│     │  Container (Python 3.11)                │                        │
│     │  + Payload mounted                      │                        │
│     │  + ENV vars injected                    │                        │
│     └─────────────────────────────────────────┘                        │
│                                                                         │
│  3. Runtime detects environment change                                  │
│     - Reads new app settings                                           │
│     - Loads function metadata                                          │
│     - Specializes                                                      │
│                                                                         │
│  4. Runtime tells Worker to reload environment                         │
│     Runtime ──► FunctionEnvironmentReloadRequest ──► Worker            │
│                                                                         │
│  5. Worker reloads, loads customer code                                │
│                                                                         │
│  6. Container is now specialized and serving customer                  │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### New Model: Coordinated Multi-Container Specialization

In the new model, specialization involves coordinating between Runtime and Worker containers:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  New Specialization Flow                                                │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Scale Controller                                                       │
│        │                                                                │
│        │  Step 1: Select placeholder Worker                             │
│        │  ──────────────────────────────────                            │
│        │  - Find worker matching required language/version              │
│        │  - Mark as "specializing" (not available for other requests)   │
│        │                                                                │
│        ├─────────────────────────────────────────┐                     │
│        │                                         │                     │
│        │  Step 2a: Worker Specialization         │  Step 2b: Runtime   │
│        │  (parallel)                             │  Payload Mount      │
│        ▼                                         ▼                     │
│  ┌───────────────────────┐             ┌─────────────────────────┐    │
│  │ Worker Container      │             │ Runtime Container       │    │
│  │                       │             │                         │    │
│  │ Inject:               │             │ Mount:                  │    │
│  │ - Customer payload    │             │ - extensions folder     │    │
│  │ - ENV vars (app       │             │ - host.json             │    │
│  │   settings)           │             │ - function.json files   │    │
│  │ - New auth token      │             │                         │    │
│  │                       │             │ (NO env vars needed -   │    │
│  │                       │             │  Worker sends those)    │    │
│  └───────────┬───────────┘             └────────────┬────────────┘    │
│              │                                      │                  │
│              │  Step 3: Sidecar detects ENV change                     │
│              │  ─────────────────────────────────                      │
│              ▼                                                         │
│  ┌───────────────────────────────────────────────────────────────┐    │
│  │  Worker Sidecar                                                │    │
│  │                                                                │    │
│  │  1. Detects new environment variables                         │    │
│  │  2. Updates internal context (ApplicationId, Version, etc.)   │    │
│  │  3. Sends FunctionEnvironmentReloadRequest to Worker          │    │
│  │  4. Sends WorkerSpecialized message to Runtime                │    │
│  └───────────────────────────────────────────────────────────────┘    │
│              │                                      │                  │
│              │                                      │                  │
│              ▼                                      ▼                  │
│  ┌───────────────────────┐             ┌─────────────────────────┐    │
│  │ Worker Process        │             │ Runtime                 │    │
│  │                       │             │                         │    │
│  │ - Reloads environment │             │ Receives:               │    │
│  │ - Loads customer code │             │ WorkerSpecialized {     │    │
│  │ - Initializes         │             │   ApplicationId,        │    │
│  │   dependencies        │             │   MetadataVersion,      │    │
│  │                       │             │   CodeVersion,          │    │
│  │                       │             │   AppSettings,          │    │
│  │                       │             │   ConnectionStrings     │    │
│  │                       │             │ }                       │    │
│  └───────────────────────┘             └────────────┬────────────┘    │
│                                                     │                  │
│                                        Step 4: Runtime creates         │
│                                        customer JobHost                │
│                                        ─────────────────────           │
│                                                     ▼                  │
│                                        ┌─────────────────────────┐    │
│                                        │ Runtime                 │    │
│                                        │                         │    │
│                                        │ 1. Removes Worker from  │    │
│                                        │    Placeholder JobHost  │    │
│                                        │                         │    │
│                                        │ 2. Creates new JobHost  │    │
│                                        │    for customer app     │    │
│                                        │                         │    │
│                                        │ 3. Associates Worker    │    │
│                                        │    with new JobHost     │    │
│                                        │                         │    │
│                                        │ 4. Reuses existing      │    │
│                                        │    gRPC connection/     │    │
│                                        │    channel              │    │
│                                        │                         │    │
│                                        │ 5. Begins normal        │    │
│                                        │    function execution   │    │
│                                        └─────────────────────────┘    │
│                                                                         │
│  Step 5: Worker and Runtime now assigned to customer                   │
│  ─────────────────────────────────────────────────────                 │
│  - Runtime:Worker pair is exclusively assigned to this customer        │
│  - Not eligible for use by other customers                             │
│  - Scale Controller tracks this assignment                             │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Scale Controller Specialization Implementation

```csharp
public class SpecializationOrchestrator
{
    public async Task<SpecializationResult> SpecializeWorkerAsync(
        CustomerApp app,
        WorkerInstance placeholderWorker)
    {
        var correlationId = Guid.NewGuid().ToString();
        
        _logger.LogInformation(
            "Beginning specialization for {AppId} on worker {WorkerId} " +
            "(Runtime: {RuntimeId}). CorrelationId: {CorrelationId}",
            app.ApplicationId,
            placeholderWorker.WorkerId,
            placeholderWorker.RuntimeId,
            correlationId);
        
        // Step 1: Mark worker as specializing (lock it)
        await _stateManager.SetWorkerStatusAsync(
            placeholderWorker.WorkerId,
            WorkerStatus.Specializing);
        
        try
        {
            // Step 2: Parallel operations
            var workerTask = SpecializeWorkerContainerAsync(
                placeholderWorker,
                app,
                correlationId);
            
            var runtimeTask = MountRuntimePayloadAsync(
                placeholderWorker.RuntimeInstance,
                app,
                correlationId);
            
            await Task.WhenAll(workerTask, runtimeTask);
            
            // Step 3: Wait for confirmation from Runtime
            // (Runtime sends acknowledgment when it receives WorkerSpecialized
            // and creates the customer JobHost)
            var confirmation = await WaitForSpecializationConfirmationAsync(
                placeholderWorker.RuntimeInstance,
                app.ApplicationId,
                timeout: TimeSpan.FromSeconds(30));
            
            if (!confirmation.Success)
            {
                throw new SpecializationException(
                    $"Runtime did not confirm specialization: {confirmation.Error}");
            }
            
            // Step 4: Update tracking
            await _stateManager.RecordSpecializationAsync(new SpecializationRecord
            {
                ApplicationId = app.ApplicationId,
                MetadataVersion = app.MetadataVersion,
                CodeVersion = app.CodeVersion,
                WorkerId = placeholderWorker.WorkerId,
                RuntimeId = placeholderWorker.RuntimeId,
                SpecializedAt = DateTimeOffset.UtcNow,
                CorrelationId = correlationId
            });
            
            _logger.LogInformation(
                "Specialization complete for {AppId} on worker {WorkerId}. " +
                "CorrelationId: {CorrelationId}",
                app.ApplicationId,
                placeholderWorker.WorkerId,
                correlationId);
            
            return SpecializationResult.Success(placeholderWorker);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Specialization failed for {AppId} on worker {WorkerId}. " +
                "CorrelationId: {CorrelationId}",
                app.ApplicationId,
                placeholderWorker.WorkerId,
                correlationId);
            
            // Rollback: Return worker to placeholder pool if possible
            await AttemptRollbackAsync(placeholderWorker, correlationId);
            
            throw;
        }
    }
    
    private async Task SpecializeWorkerContainerAsync(
        WorkerInstance worker,
        CustomerApp app,
        string correlationId)
    {
        // Inject customer payload and environment variables into Worker container
        await _containerOrchestrator.UpdateContainerAsync(worker.ContainerId, new ContainerUpdate
        {
            // Mount customer code
            VolumeMounts = new[]
            {
                new VolumeMount
                {
                    Source = app.CodeBundlePath,
                    Target = "/home/site/wwwroot",
                    ReadOnly = true
                }
            },
            
            // Inject environment variables
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["APPLICATION_ID"] = app.ApplicationId,
                ["METADATA_VERSION"] = app.MetadataVersion,
                ["CODE_VERSION"] = app.CodeVersion,
                ["IS_PLACEHOLDER"] = "false",
                ["WORKER_AUTH_TOKEN"] = GenerateCustomerToken(app, worker),
                // Customer app settings
                ..app.AppSettings,
                // Connection strings
                ..app.ConnectionStrings.ToDictionary(
                    cs => $"ConnectionStrings__{cs.Key}",
                    cs => cs.Value)
            }
        });
    }
    
    private async Task MountRuntimePayloadAsync(
        RuntimeInstance runtime,
        CustomerApp app,
        string correlationId)
    {
        // Mount customer payload to Runtime container
        // (extensions, host.json, function.json files)
        // NOTE: Environment variables are NOT injected here
        // Worker Sidecar will send them via WorkerSpecialized message
        
        await _containerOrchestrator.UpdateContainerAsync(runtime.ContainerId, new ContainerUpdate
        {
            VolumeMounts = new[]
            {
                new VolumeMount
                {
                    Source = app.CodeBundlePath,
                    Target = $"/apps/{app.ApplicationId}",
                    ReadOnly = true
                }
            }
        });
    }
}
```

### Why No Environment Variables for Runtime?

In the current model, Runtime reads environment variables to understand the application context. In the new model:

| Current Model | New Model |
|---------------|-----------|
| Runtime reads `AzureWebJobsStorage` from ENV | Worker Sidecar sends connection string in `WorkerSpecialized` |
| Runtime reads app settings from ENV | Worker Sidecar sends app settings in `WorkerSpecialized` |
| Runtime and Worker share same ENV | Runtime and Worker have separate ENVs |

**Benefits of Worker Sidecar sending settings**:
1. Runtime doesn't need container restart to see new settings
2. Cleaner separation - Runtime is truly language-agnostic
3. Settings can be validated by Sidecar before sending
4. Easier to support multiple applications on same Runtime

---

## Scale-Out

Scale-out in the new model behaves differently depending on whether this is the **first worker** (0→1) or **additional workers** (1→N) for a customer application.

### Current Model: Scale-Out is Always the Same

In the current model, every scale-out operation is identical - select a placeholder container and specialize it:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Current Model: All Scale-Out is Identical                              │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  0→1 Scale-Out:                   1→N Scale-Out:                        │
│  ┌───────────────┐               ┌───────────────┐                     │
│  │ Placeholder   │               │ Container A   │ (already serving)   │
│  │ Container     │               │ Runtime+Worker│                     │
│  │ Runtime+Worker│               └───────────────┘                     │
│  └───────┬───────┘               ┌───────────────┐                     │
│          │                       │ Placeholder   │                     │
│          │ Specialize            │ Container     │                     │
│          ▼                       │ Runtime+Worker│                     │
│  ┌───────────────┐               └───────┬───────┘                     │
│  │ Customer      │                       │                             │
│  │ Container     │                       │ Specialize (same process)   │
│  │ Runtime+Worker│                       ▼                             │
│  └───────────────┘               ┌───────────────┐                     │
│                                  │ Customer      │                     │
│                                  │ Container     │                     │
│                                  │ Runtime+Worker│                     │
│                                  └───────────────┘                     │
│                                                                         │
│  Key: Worker is ALREADY connected to its Runtime (same container)       │
│       gRPC connection is ready-to-go                                    │
│       Both 0→1 and 1→N have the same latency characteristics           │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### New Model: 0→1 vs 1→N Are Different

In the new model, 0→1 and 1→N scale-out are fundamentally different operations:

| Aspect | 0→1 (First Worker) | 1→N (Additional Workers) |
|--------|-------------------|--------------------------|
| **Target Runtime** | Placeholder Runtime (worker is already connected) | Customer's existing Runtime |
| **Worker source** | Placeholder on same Runtime | Placeholder on *different* Runtime |
| **gRPC connection** | Already established | Must disconnect and reconnect |
| **Specialization** | Specialize in place | Reassign then specialize |
| **User impact** | Cold start (user waiting) | Hidden (existing workers serve traffic) |

---

### 0→1 Scale-Out: First Worker for Customer

When a customer application needs its first worker, we specialize a placeholder worker **in place** on its current Runtime:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  0→1 Scale-Out: Specialize In Place                                     │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Initial State:                                                         │
│  ┌───────────────────────────────────────────────────────────────┐     │
│  │ Runtime A (Placeholder)                                        │     │
│  │ ┌─────────────────────────────────────────────────────────┐   │     │
│  │ │ Placeholder JobHost                                      │   │     │
│  │ │ ├─► Worker 1 (Python) ◄── This one will be specialized  │   │     │
│  │ │ ├─► Worker 2 (Node.js)                                   │   │     │
│  │ │ └─► Worker 3 (.NET)                                      │   │     │
│  │ └─────────────────────────────────────────────────────────┘   │     │
│  └───────────────────────────────────────────────────────────────┘     │
│                                                                         │
│  Scale Controller: "Customer X needs a Python worker"                   │
│                                                                         │
│  Step 1: Select Worker 1 (already connected to Runtime A)               │
│  Step 2: Mount customer payload to Runtime A                            │
│  Step 3: Specialize Worker 1 (inject ENV, trigger reload)              │
│  Step 4: Worker Sidecar sends WorkerSpecialized to Runtime A           │
│  Step 5: Runtime A creates new JobHost for Customer X                  │
│  Step 6: Worker 1 removed from Placeholder JobHost, added to new one   │
│                                                                         │
│  After 0→1:                                                             │
│  ┌───────────────────────────────────────────────────────────────┐     │
│  │ Runtime A (Now serving Customer X)                             │     │
│  │ ┌─────────────────────────────────────────────────────────┐   │     │
│  │ │ Placeholder JobHost                                      │   │     │
│  │ │ ├─► Worker 2 (Node.js)                                   │   │     │
│  │ │ └─► Worker 3 (.NET)                                      │   │     │
│  │ └─────────────────────────────────────────────────────────┘   │     │
│  │ ┌─────────────────────────────────────────────────────────┐   │     │
│  │ │ Customer X JobHost                                       │   │     │
│  │ │ └─► Worker 1 (Python) ◄── Now specialized               │   │     │
│  │ └─────────────────────────────────────────────────────────┘   │     │
│  └───────────────────────────────────────────────────────────────┘     │
│                                                                         │
│  Key: Worker's gRPC connection to Runtime was ALREADY established      │
│       No reconnection overhead                                          │
│       Runtime A is now dedicated to Customer X                          │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

**0→1 Flow matches the Specialization Flow** documented earlier - this is the standard specialization process where the worker is already connected to the Runtime that will serve the customer.

---

### 1→N Scale-Out: Additional Workers via Reassignment

When a customer already has workers and needs more capacity, we must **claim workers from other Runtimes** and reassign them:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  1→N Scale-Out: Worker Reassignment                                     │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Initial State:                                                         │
│  ┌───────────────────────────────┐   ┌───────────────────────────────┐ │
│  │ Runtime A (Customer X)        │   │ Runtime B (Placeholder)       │ │
│  │ ┌───────────────────────────┐ │   │ ┌───────────────────────────┐ │ │
│  │ │ Customer X JobHost        │ │   │ │ Placeholder JobHost       │ │ │
│  │ │ └─► Worker 1 (Python)     │ │   │ │ ├─► Worker 4 (Python) ◄─┐ │ │ │
│  │ └───────────────────────────┘ │   │ │ ├─► Worker 5 (Node.js)  │ │ │ │
│  └───────────────────────────────┘   │ │ └─► Worker 6 (.NET)     │ │ │ │
│                                      │ └───────────────────────────┘ │ │
│                                      └───────────────────────────────┘ │
│                                                          │             │
│  Scale Controller: "Customer X needs another Python worker"            │
│                                        │                               │
│                                        │ Claim this worker             │
│                                        ▼                               │
│  Step 1: Claim Worker 4 from Runtime B's placeholder pool              │
│  Step 2: Disconnect Worker 4 from Runtime B (close gRPC stream)        │
│  Step 3: Update Worker 4's RUNTIME_ENDPOINT to point to Runtime A      │
│  Step 4: Worker Sidecar detects change, reconnects to Runtime A        │
│  Step 5: Runtime A receives StartStream from Worker 4 (new connection) │
│  Step 6: Specialize Worker 4 for Customer X (same as 0→1 from here)   │
│  Step 7: Replenish placeholder pool (start new Python worker)          │
│                                                                         │
│  After 1→N:                                                             │
│  ┌───────────────────────────────┐   ┌───────────────────────────────┐ │
│  │ Runtime A (Customer X)        │   │ Runtime B (Placeholder)       │ │
│  │ ┌───────────────────────────┐ │   │ ┌───────────────────────────┐ │ │
│  │ │ Customer X JobHost        │ │   │ │ Placeholder JobHost       │ │ │
│  │ │ ├─► Worker 1 (Python)     │ │   │ │ ├─► Worker 5 (Node.js)    │ │ │
│  │ │ └─► Worker 4 (Python) NEW │ │   │ │ ├─► Worker 6 (.NET)       │ │ │
│  │ └───────────────────────────┘ │   │ │ └─► Worker 7 (Python) NEW │ │ │
│  └───────────────────────────────┘   │ └───────────────────────────┘ │ │
│                                      └───────────────────────────────┘ │
│                                                                         │
│  Key: Worker had to DISCONNECT and RECONNECT to new Runtime            │
│       gRPC negotiation overhead exists but is hidden                    │
│       Worker 7 started to replenish placeholder pool                    │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Why 1→N Requires Reassignment

In the current model, every placeholder container has its own Runtime - the worker and Runtime are already paired. When specializing for scale-out, the worker just specializes with its existing Runtime.

In the new model, placeholder workers are connected to **placeholder Runtimes**, not to the customer's Runtime. When Customer X needs another worker:
- Customer X's Runtime is **Runtime A**
- Available placeholder workers are on **Runtime B, C, D, etc.**
- We can't just specialize in place - the worker must move to Customer X's Runtime

### gRPC Reconnection Overhead

The 1→N flow has additional overhead compared to 0→1:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Timing Comparison: 0→1 vs 1→N                                          │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  0→1 (No reconnection needed):                                          │
│  ┌──────────────────────────────────────────────────────────────────┐  │
│  │ Specialize │ Env Reload │ JobHost Create │ Function Load │ Ready │  │
│  │   ~50ms    │   ~50ms    │    ~100ms      │    ~200ms     │       │  │
│  └──────────────────────────────────────────────────────────────────┘  │
│  Total: ~400ms (cold start - user is waiting)                           │
│                                                                         │
│  1→N (Reconnection required):                                           │
│  ┌──────────────────────────────────────────────────────────────────────┐
│  │ Disconnect │ Connect │ StartStream │ Specialize │ Function Load │   │
│  │   ~10ms    │  ~50ms  │   ~30ms     │   ~100ms   │    ~200ms     │   │
│  └──────────────────────────────────────────────────────────────────────┘
│  Total: ~390ms (but hidden - existing workers serve traffic)            │
│                                                                         │
│  Key insight: 1→N overhead is similar but NOT user-visible              │
│  ─────────────────────────────────────────────────────────              │
│  During 1→N, Customer X's Worker 1 continues handling all requests.     │
│  Worker 4 only receives traffic AFTER it reports ready.                 │
│  No request sits waiting on Worker 4 during reconnection.               │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Worker Sidecar: Handling Reconnection

The Worker Sidecar detects the Runtime endpoint change and performs the reconnection:

```csharp
public class WorkerSidecar
{
    private string _currentRuntimeEndpoint;
    private GrpcChannel _runtimeChannel;
    private AsyncDuplexStreamingCall<StreamingMessage, StreamingMessage> _stream;
    
    // Called when environment variables change (e.g., via container update)
    public async Task OnEnvironmentChangedAsync()
    {
        var newEndpoint = Environment.GetEnvironmentVariable("RUNTIME_ENDPOINT");
        var reconnectSignal = Environment.GetEnvironmentVariable("RECONNECT_SIGNAL");
        
        // Detect if we need to reconnect to a different Runtime
        if (!string.IsNullOrEmpty(reconnectSignal) && 
            newEndpoint != _currentRuntimeEndpoint)
        {
            _logger.LogInformation(
                "Reconnection signal received. " +
                "Switching Runtime: {OldEndpoint} → {NewEndpoint}",
                _currentRuntimeEndpoint,
                newEndpoint);
            
            await ReconnectToRuntimeAsync(newEndpoint);
        }
    }
    
    private async Task ReconnectToRuntimeAsync(string newEndpoint)
    {
        // Step 1: Gracefully close existing connection
        try
        {
            await _stream.RequestStream.CompleteAsync();
            _runtimeChannel?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during graceful disconnect");
        }
        
        // Step 2: Connect to new Runtime
        _currentRuntimeEndpoint = newEndpoint;
        _runtimeChannel = GrpcChannel.ForAddress(newEndpoint);
        
        var client = new FunctionRpc.FunctionRpcClient(_runtimeChannel);
        _stream = client.EventStream();
        
        // Step 3: Send StartStream (same as initial connection)
        var startStream = CreateStartStreamMessage();
        await _stream.RequestStream.WriteAsync(startStream);
        
        _logger.LogInformation("Connected to new Runtime at {Endpoint}", newEndpoint);
        
        // Step 4: Resume bidirectional streaming
        // From Runtime's perspective, this looks like a brand new worker
        await ProcessMessagesAsync();
    }
}
```

### Runtime Perspective: Same Flow for New or Reassigned Worker

From the Runtime's perspective, a reassigned worker (1→N) looks exactly like a new worker connecting via StartStream. The Runtime uses the same code path:

```csharp
// In Runtime: Handles both new workers AND reassigned workers
public async Task HandleEventStream(
    IAsyncStreamReader<StreamingMessage> requestStream,
    IServerStreamWriter<StreamingMessage> responseStream,
    ServerCallContext context)
{
    // First message is always StartStream
    var message = await requestStream.MoveNext(context.CancellationToken);
    var startStream = message.StartStream;
    
    _logger.LogInformation(
        "Worker connected: {WorkerId} ({Language})",
        startStream.WorkerId,
        startStream.Properties["Language"]);
    
    // Register worker - same for new or reassigned
    var workerState = await RegisterWorkerAsync(startStream);
    
    // Send WorkerInitRequest, process WorkerInitResponse
    await InitializeWorkerAsync(workerState, requestStream, responseStream);
    
    // ... rest of StartStream flow
    // Eventually WorkerSpecialized arrives and we associate with JobHost
}
```

### Scale-Out Decision Algorithm

```csharp
public class ScaleOutOrchestrator
{
    public async Task<ScaleOutResult> ScaleOutAsync(
        CustomerApp app,
        int additionalWorkersNeeded)
    {
        // Determine if this is 0→1 or 1→N
        var existingWorkers = await _stateManager.GetWorkersForAppAsync(app.ApplicationId);
        
        if (existingWorkers.Count == 0)
        {
            // 0→1: First worker - use standard specialization
            return await FirstWorkerScaleOutAsync(app);
        }
        else
        {
            // 1→N: Additional workers - requires reassignment
            return await AdditionalWorkersScaleOutAsync(app, additionalWorkersNeeded);
        }
    }
    
    private async Task<ScaleOutResult> FirstWorkerScaleOutAsync(CustomerApp app)
    {
        _logger.LogInformation(
            "0→1 scale-out for {AppId}: Using standard specialization",
            app.ApplicationId);
        
        // Find placeholder worker on any placeholder Runtime
        var placeholder = await FindPlaceholderWorkerAsync(
            app.Language,
            app.LanguageVersion);
        
        if (placeholder == null)
        {
            throw new NoPlaceholderAvailableException(app.Language, app.LanguageVersion);
        }
        
        // Specialize in place - worker stays on its current Runtime
        // Runtime becomes dedicated to this customer
        var result = await _specializationOrchestrator.SpecializeWorkerAsync(
            app,
            placeholder);
        
        return new ScaleOutResult(new[] { result.Worker });
    }
    
    private async Task<ScaleOutResult> AdditionalWorkersScaleOutAsync(
        CustomerApp app,
        int count)
    {
        _logger.LogInformation(
            "1→N scale-out for {AppId}: Reassigning {Count} workers from placeholder pool",
            app.ApplicationId,
            count);
        
        // Find the Runtime already serving this customer
        var customerRuntime = await _stateManager.GetRuntimeForAppAsync(app.ApplicationId);
        
        var results = new List<WorkerInstance>();
        
        for (int i = 0; i < count; i++)
        {
            // Find placeholder worker (will be on a DIFFERENT Runtime)
            var placeholder = await FindPlaceholderWorkerAsync(
                app.Language,
                app.LanguageVersion);
            
            if (placeholder == null)
            {
                _logger.LogWarning(
                    "No more placeholder workers available for {Language}",
                    app.Language);
                break;
            }
            
            // Reassign: disconnect from placeholder Runtime, reconnect to customer Runtime
            var worker = await ReassignWorkerAsync(
                placeholder,
                customerRuntime,
                app);
            
            results.Add(worker);
            
            // Async replenishment - don't wait
            _ = ReplenishPlaceholderAsync(app.Language, app.LanguageVersion);
        }
        
        return new ScaleOutResult(results);
    }
    
    private async Task<WorkerInstance> ReassignWorkerAsync(
        WorkerInstance placeholder,
        RuntimeInstance targetRuntime,
        CustomerApp app)
    {
        var correlationId = Guid.NewGuid().ToString();
        
        _logger.LogInformation(
            "Reassigning worker {WorkerId}: Runtime {Source} → {Target}. " +
            "CorrelationId: {CorrelationId}",
            placeholder.WorkerId,
            placeholder.RuntimeId,
            targetRuntime.RuntimeId,
            correlationId);
        
        // Step 1: Update worker's target Runtime endpoint
        await _containerOrchestrator.UpdateContainerAsync(
            placeholder.ContainerId,
            new ContainerUpdate
            {
                EnvironmentVariables = new Dictionary<string, string>
                {
                    ["RUNTIME_ENDPOINT"] = $"{targetRuntime.Endpoint}:{targetRuntime.Port}",
                    ["RECONNECT_SIGNAL"] = correlationId
                }
            });
        
        // Step 2: Wait for worker to connect to new Runtime
        // (Sidecar detects RECONNECT_SIGNAL, disconnects, reconnects)
        var connected = await WaitForWorkerConnectionAsync(
            targetRuntime,
            placeholder.WorkerId,
            timeout: TimeSpan.FromSeconds(30));
        
        if (!connected)
        {
            throw new ReassignmentException(
                $"Worker {placeholder.WorkerId} failed to reconnect");
        }
        
        // Step 3: Update tracking
        placeholder.RuntimeId = targetRuntime.RuntimeId;
        
        // Step 4: Specialize (same as 0→1 from here)
        var result = await _specializationOrchestrator.SpecializeWorkerAsync(
            app,
            placeholder);
        
        return result.Worker;
    }
    
    private async Task ReplenishPlaceholderAsync(string language, string languageVersion)
    {
        // Background task to replace the placeholder we just used
        try
        {
            var targetRuntime = await FindPlaceholderRuntimeWithCapacityAsync();
            
            var worker = await StartPlaceholderWorkerAsync(
                targetRuntime,
                language,
                languageVersion);
            
            await WarmupWorkerAsync(worker);
            
            _logger.LogInformation(
                "Placeholder pool replenished: {Language} {Version}",
                language,
                languageVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to replenish placeholder: {Language} {Version}",
                language,
                languageVersion);
        }
    }
}
```

### Testing Considerations

The difference between 0→1 and 1→N should be benchmarked:

```csharp
public class ScaleOutBenchmarks
{
    [Benchmark(Description = "0→1: First worker (no reconnection)")]
    public async Task ZeroToOne_FirstWorker()
    {
        // Measure: Time from scale decision to worker handling first request
        // Worker already connected to Runtime - just specialize
    }
    
    [Benchmark(Description = "1→N: Additional worker (with reconnection)")]  
    public async Task OneToN_AdditionalWorker()
    {
        // Measure: Time from scale decision to worker handling first request
        // Includes: disconnect + reconnect + specialize
    }
    
    [Benchmark(Description = "1→N: User-perceived latency")]
    public async Task OneToN_UserPerceivedLatency()
    {
        // Measure: Does any USER REQUEST experience additional latency?
        // Expected: No - existing workers handle traffic during reassignment
    }
}
```

### Summary: 0→1 vs 1→N

| Scenario | Process | gRPC Connection | User Impact |
|----------|---------|-----------------|-------------|
| **0→1** | Specialize in place | Already connected | Cold start (waiting) |
| **1→N** | Reassign then specialize | Disconnect + reconnect | Hidden (no waiting) |

The key insight is that while 1→N has additional overhead (gRPC reconnection), this overhead is **not visible to users** because existing workers continue handling traffic while the new worker is being reassigned.

---

### Multiple Workers for Same Application

When scaling out, multiple workers can serve the same application on the same Runtime:

```csharp
// Runtime tracks multiple workers per application
public class JobHostState
{
    public ApplicationDefinition ApplicationDefinition { get; }
    public JobHost JobHost { get; }
    
    // Multiple workers can serve this application
    public List<WorkerState> Workers { get; } = new();
    
    public void AddWorker(WorkerState worker)
    {
        Workers.Add(worker);
        
        _logger.LogInformation(
            "Worker {WorkerId} added to JobHost for {AppId}. " +
            "Total workers: {WorkerCount}",
            worker.WorkerId,
            ApplicationDefinition.ApplicationId,
            Workers.Count);
    }
    
    public WorkerState SelectWorkerForInvocation()
    {
        // Load balancing across workers
        // (round-robin, least-connections, etc.)
        return _loadBalancer.SelectWorker(Workers);
    }
}
```

---

## Resource Efficiency

### Current Model: Duplicated Runtime Overhead

**Example: 100 placeholder containers**

| Language | Count | Runtime Overhead | Worker Overhead | Total |
|----------|-------|------------------|-----------------|-------|
| Python 3.11 | 15 | 15 × 200 MB = 3 GB | 15 × 100 MB = 1.5 GB | 4.5 GB |
| Python 3.10 | 10 | 10 × 200 MB = 2 GB | 10 × 100 MB = 1 GB | 3 GB |
| Node.js 20 | 15 | 15 × 200 MB = 3 GB | 15 × 80 MB = 1.2 GB | 4.2 GB |
| Node.js 18 | 12 | 12 × 200 MB = 2.4 GB | 12 × 80 MB = 0.96 GB | 3.36 GB |
| .NET 8.0 | 12 | 12 × 200 MB = 2.4 GB | 12 × 150 MB = 1.8 GB | 4.2 GB |
| .NET 6.0 | 8 | 8 × 200 MB = 1.6 GB | 8 × 150 MB = 1.2 GB | 2.8 GB |
| Java 17 | 8 | 8 × 200 MB = 1.6 GB | 8 × 200 MB = 1.6 GB | 3.2 GB |
| Java 11 | 5 | 5 × 200 MB = 1 GB | 5 × 200 MB = 1 GB | 2 GB |
| PowerShell 7.4 | 5 | 5 × 200 MB = 1 GB | 5 × 120 MB = 0.6 GB | 1.6 GB |
| **Total** | **90** | **18 GB** | **10.86 GB** | **28.86 GB** |

### New Model: Shared Runtime

**Example: Same 90 workers, 5 Runtimes (18 workers each)**

| Component | Count | Memory | Total |
|-----------|-------|--------|-------|
| Runtime | 5 | 300 MB* | 1.5 GB |
| Worker Sidecar | 90 | 20 MB | 1.8 GB |
| Workers | 90 | (varies) | 10.86 GB |
| **Total** | | | **14.16 GB** |

*Runtime memory slightly higher due to managing multiple workers

### Savings

| Metric | Current | New | Savings |
|--------|---------|-----|---------|
| Total Memory | 28.86 GB | 14.16 GB | **51% reduction** |
| Runtime Instances | 90 | 5 | **94% reduction** |
| Container Count | 90 | 95 (5 Runtime + 90 Worker) | Similar |

### Visual Comparison

```
Current Model (90 containers):
┌─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┬─────┐
│ R+W │ R+W │ R+W │ R+W │ R+W │ R+W │ R+W │ R+W │ R+W │ ... │ × 90
│ Py  │ Py  │ Py  │ Node│ Node│ .NET│ .NET│ Java│ PS  │     │
└─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┴─────┘
  Each box = Runtime (200 MB) + Worker (varies)

New Model (5 Runtimes + 90 Workers):
┌─────────────────────────────────────────────────────────────┐
│ Runtime 1 (300 MB)                                          │
│ ┌───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┬───┐
│ │Py │Py │Py │Nod│Nod│Nod│.NT│.NT│Jav│Jav│PS │Py │Nod│.NT│Jav│PS │Py │Nod│ (18 workers)
│ └───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┴───┘
└─────────────────────────────────────────────────────────────┘
  × 5 Runtimes

  Workers: Small boxes (Worker memory only, no Runtime overhead)
```

---

## Tracking and State Management

### Scale Controller State Model

The Scale Controller must track a more complex state in the new model:

```csharp
public class ScaleControllerState
{
    // Runtime tracking
    public ConcurrentDictionary<string, RuntimeState> Runtimes { get; } = new();
    
    // Worker tracking
    public ConcurrentDictionary<string, WorkerState> Workers { get; } = new();
    
    // Application assignments
    public ConcurrentDictionary<string, ApplicationAssignment> Assignments { get; } = new();
}

public class RuntimeState
{
    public string RuntimeId { get; set; }
    public string ContainerId { get; set; }
    public string Endpoint { get; set; }
    public int Port { get; set; }
    
    public int MaxWorkers { get; set; }
    public List<string> ConnectedWorkerIds { get; set; } = new();
    
    public RuntimeStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    
    // Placeholder tracking
    public int PlaceholderWorkerCount => ConnectedWorkerIds
        .Count(wid => Workers[wid].IsPlaceholder);
    
    public int SpecializedWorkerCount => ConnectedWorkerIds
        .Count(wid => !Workers[wid].IsPlaceholder);
    
    public bool HasCapacity => ConnectedWorkerIds.Count < MaxWorkers;
}

public class WorkerState
{
    public string WorkerId { get; set; }
    public string ContainerId { get; set; }
    public string RuntimeId { get; set; }
    
    public string Language { get; set; }
    public string LanguageVersion { get; set; }
    
    public bool IsPlaceholder { get; set; }
    public string? ApplicationId { get; set; }
    public string? MetadataVersion { get; set; }
    public string? CodeVersion { get; set; }
    
    public WorkerStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? SpecializedAt { get; set; }
}

public class ApplicationAssignment
{
    public string ApplicationId { get; set; }
    public string MetadataVersion { get; set; }
    public string CodeVersion { get; set; }
    
    // Multiple workers can serve one application
    public List<string> WorkerIds { get; set; } = new();
    
    // Runtime(s) hosting this application
    public List<string> RuntimeIds { get; set; } = new();
}
```

### State Transitions

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Worker State Machine                                                    │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌──────────┐    connect    ┌──────────┐    warmup     ┌──────────┐   │
│  │ Starting │ ──────────────► Connecting│ ──────────────► Placeholder│   │
│  └──────────┘               └──────────┘               └─────┬─────┘   │
│                                                              │         │
│                                                         specialize     │
│                                                              │         │
│                                                              ▼         │
│  ┌──────────┐   terminate   ┌──────────┐               ┌──────────┐   │
│  │ Abandoned│ ◄─────────────│Specializing│◄──────────────│    │       │
│  └──────────┘               └──────────┘    (failure)   │    │       │
│                                   │                      │    │       │
│                                   │ (success)            │    │       │
│                                   ▼                      │    │       │
│                             ┌──────────┐                │    │       │
│                             │ Specialized│◄──────────────┘    │       │
│                             └─────┬─────┘                     │       │
│                                   │                           │       │
│                              running                          │       │
│                                   │                           │       │
│                                   ▼                           │       │
│                             ┌──────────┐                     │       │
│                             │  Active  │◄────────────────────┘       │
│                             └──────────┘                              │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## New gRPC Messages

### WorkerSpecialized Message

New message sent from Worker Sidecar to Runtime when specialization occurs:

```protobuf
message StreamingMessage {
  // ... existing fields ...
  
  oneof content {
    // ... existing message types ...
    
    // NEW: Sent by Worker Sidecar when worker is specialized
    WorkerSpecialized worker_specialized = 100;
  }
}

message WorkerSpecialized {
  // Application identity
  string application_id = 1;
  string metadata_version = 2;
  string code_version = 3;
  
  // Customer settings (Worker Sidecar reads from ENV and sends)
  map<string, string> app_settings = 4;
  map<string, string> connection_strings = 5;
  
  // Function metadata path (where Runtime can find function.json files)
  string functions_path = 6;
  
  // Worker info
  string worker_id = 7;
  string language = 8;
  string language_version = 9;
  
  // Correlation for tracking
  string correlation_id = 10;
}
```

### Runtime Response

```protobuf
message WorkerSpecializedResponse {
  // Status
  StatusResult result = 1;
  
  // JobHost info
  string job_host_id = 2;
  
  // Correlation
  string correlation_id = 3;
}
```

### Flow Sequence

```
┌─────────────────────────────────────────────────────────────────────────┐
│  WorkerSpecialized Message Flow                                         │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Scale Controller                                                       │
│        │                                                                │
│        │  1. Injects new ENV vars into Worker container                 │
│        │                                                                │
│        ▼                                                                │
│  ┌─────────────────┐                                                   │
│  │ Worker Sidecar  │                                                   │
│  │                 │                                                   │
│  │ 2. Detects ENV  │                                                   │
│  │    change       │                                                   │
│  │                 │                                                   │
│  │ 3. Reads new    │                                                   │
│  │    settings     │                                                   │
│  └────────┬────────┘                                                   │
│           │                                                             │
│           │ 4. Sends FunctionEnvironmentReloadRequest to Worker        │
│           │                                                             │
│           │ 5. Sends WorkerSpecialized to Runtime                      │
│           │    (includes all settings from ENV)                        │
│           │                                                             │
│           ▼                                                             │
│  ┌─────────────────┐        ┌─────────────────┐                        │
│  │ Worker Process  │        │ Runtime         │                        │
│  │                 │        │                 │                        │
│  │ 6. Reloads env  │        │ 7. Receives     │                        │
│  │    Loads code   │        │    WorkerSpec-  │                        │
│  │                 │        │    ialized      │                        │
│  │                 │        │                 │                        │
│  │                 │        │ 8. Creates new  │                        │
│  │                 │        │    JobHost for  │                        │
│  │                 │        │    application  │                        │
│  │                 │        │                 │                        │
│  │ 9. Responds     │        │ 10. Associates  │                        │
│  │    with         │        │     worker with │                        │
│  │    capabilities │        │     JobHost     │                        │
│  └─────────────────┘        └────────┬────────┘                        │
│                                      │                                  │
│                                      │ 11. Sends WorkerSpecialized-    │
│                                      │     Response (success)          │
│                                      │                                  │
│                                      ▼                                  │
│                             Scale Controller tracks                     │
│                             successful specialization                   │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Cold Start Considerations

### Potential Cold Start Impact

The new multi-step specialization flow may impact cold start times:

| Step | Current Model | New Model | Delta |
|------|---------------|-----------|-------|
| Select placeholder | ~10 ms | ~10 ms | 0 |
| Mount payload | ~50 ms | ~100 ms (2 containers) | +50 ms |
| Inject ENV | ~20 ms | ~20 ms | 0 |
| Sidecar detection | N/A | ~10 ms | +10 ms |
| WorkerSpecialized msg | N/A | ~5 ms | +5 ms |
| Create JobHost | ~100 ms | ~100 ms | 0 |
| Worker reload | ~200 ms | ~200 ms | 0 |
| Function load | ~100 ms | ~100 ms | 0 |
| **Total** | **~480 ms** | **~545 ms** | **+65 ms** |

### Mitigations

1. **Parallel Operations**: Mount payloads to Runtime and Worker in parallel

2. **Pre-creation**: Runtime can pre-create JobHost shell when it knows specialization is coming

3. **Optimistic Processing**: Runtime starts processing while waiting for Worker to fully load

4. **Connection Reuse**: Existing gRPC connection means no connection establishment time

```csharp
// Optimization: Parallel specialization steps
public async Task<SpecializationResult> OptimizedSpecializeAsync(
    CustomerApp app,
    WorkerInstance worker)
{
    // Start all operations in parallel
    var workerSpecTask = SpecializeWorkerContainerAsync(worker, app);
    var runtimeMountTask = MountRuntimePayloadAsync(worker.RuntimeInstance, app);
    
    // Runtime can prepare for the new app while waiting
    var prepareTask = PrepareJobHostShellAsync(worker.RuntimeInstance, app);
    
    // Wait for all
    await Task.WhenAll(workerSpecTask, runtimeMountTask, prepareTask);
    
    // Now just need to finalize when WorkerSpecialized arrives
    // (JobHost shell already created, just needs activation)
    
    return await WaitForFinalizationAsync(worker, app);
}
```

### Testing Required

Cold start impact must be measured in realistic scenarios:

```csharp
public class ColdStartBenchmark
{
    [Benchmark]
    public async Task CurrentModel_ColdStart()
    {
        // Measure: Select placeholder → First function execution
    }
    
    [Benchmark]
    public async Task NewModel_ColdStart()
    {
        // Measure: Select placeholder → First function execution
        // Including: Multi-step specialization, WorkerSpecialized message
    }
    
    [Benchmark]
    public async Task NewModel_ColdStart_Optimized()
    {
        // Measure: With parallel operations and pre-creation
    }
}
```

---

## Open Questions

### 1. Runtime-to-Worker Ratio

**Question**: What is the optimal number of workers per Runtime?

**Considerations**:
- Memory overhead per connection
- Message routing overhead
- Failure blast radius (Runtime failure affects all workers)
- Resource efficiency

**Proposal**: Start with 20 workers per Runtime, monitor, adjust based on metrics.

### 2. Mixed vs. Dedicated Runtimes

**Question**: Should Runtimes host workers for multiple customers, or be dedicated?

**Options**:
- A) Mixed: One Runtime hosts workers for multiple customers (better efficiency)
- B) Dedicated: Once specialized, Runtime only serves one customer (better isolation)

**Proposal**: Dedicated after first specialization. Placeholder Runtimes are mixed, but once any worker is specialized, that Runtime is assigned to that customer.

### 3. Placeholder Worker Distribution

**Question**: How to distribute placeholder workers across Runtimes?

**Options**:
- A) Uniform: Equal distribution of each language across Runtimes
- B) Grouped: Group by language (Python Runtime, Node Runtime, etc.)
- C) Demand-based: More placeholders for popular languages

**Proposal**: Option C - Demand-based distribution with minimum coverage for each language.

### 4. Runtime Failure Handling

**Question**: What happens when a Runtime fails?

**Impact**: All connected workers lose their connection

**Proposal**: 
- Workers detect disconnection via Sidecar health check
- Workers terminate themselves
- Scale Controller detects loss, starts replacement workers
- Connect replacements to healthy Runtimes

### 5. Payload Mount Mechanism

**Question**: How exactly does Scale Controller mount payloads to containers?

**Options**:
- A) Volume mount update (requires container restart)
- B) Shared file system (NFS, Azure Files)
- C) Direct download by container (from blob storage)

**Needs Investigation**: Current Legion implementation details for payload delivery.

### 6. Environment Variable Injection

**Question**: How does Scale Controller inject environment variables without container restart?

**Options**:
- A) Kubernetes ConfigMap/Secret update + pod annotation trigger
- B) Sidecar polls for changes
- C) External signal triggers Sidecar to re-read ENV

**Needs Investigation**: Current mechanism and whether it needs modification.

### 7. Authentication Token Scope

**Question**: Should placeholder workers have different tokens than specialized workers?

**Proposal**: Yes
- Placeholder tokens: Limited scope, can only be used for warmup
- Specialized tokens: Full scope for customer application, includes tenant/app claims

### 8. Scale Controller High Availability

**Question**: How does Scale Controller maintain consistent state across instances?

**Considerations**:
- Multiple Scale Controller instances for HA
- Need consistent view of Runtime/Worker assignments
- Race conditions during specialization

**Proposal**: Use distributed state store (Redis, Cosmos DB) with optimistic concurrency.

---

## Summary

The new decoupled model requires significant changes to Scale Controller:

| Aspect | Change |
|--------|--------|
| Placeholder setup | Multi-step: Start Runtime, then Workers |
| Specialization | Coordinated: Worker ENV + Runtime payload + WorkerSpecialized message |
| Scale-out | Flexible: Add workers to existing Runtime or start new Runtime |
| State tracking | Complex: Runtime ↔ Worker ↔ Application relationships |
| Resource efficiency | Improved: ~50% memory reduction through shared Runtimes |

Key new concepts:
- **Worker Sidecar** acts as specialization coordinator
- **WorkerSpecialized** gRPC message carries settings to Runtime
- **Placeholder JobHost** serves all placeholder workers regardless of language
- **Runtime assignment** becomes exclusive after first specialization


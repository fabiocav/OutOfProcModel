# Compute Separation — Implementation Guide

**Target**: Internal preview by June 2026
**Branch**: `feature/compute-separation` (to be created from `dev`)
**Prototype reference**: [`feature/worker_refactor`](https://github.com/Azure/azure-functions-host/tree/feature/worker_refactor/prototype)

---

## Goal

Enable language workers to run on **separate compute** from the host (different VMs or containers), connecting inbound over gRPC. The host becomes a passive acceptor of gRPC streams rather than a process spawner. Workers and their customer payloads become the unit of scale.

The existing host-managed worker model is **completely untouched**. All new code is parallel and additive.

---

## Architecture Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Worker identity | Orchestrator-assigned `w_{8-char-guid}` | Matches prototype; host shouldn't generate IDs for processes it doesn't manage |
| Function metadata | 100% worker indexing — no `function.json` reads | Workers report their own functions; host just validates |
| Invocation dispatcher | New `ConnectedWorkerInvocationDispatcher` — not `RpcFunctionInvocationDispatcher` | Avoids inheriting process-restart logic that is meaningless for external workers |
| Code split | Extract `WorkerChannelBase` (protocol-only); both old and new channels inherit it | Clean separation; old code becomes deletable when proven |
| Auth / networking | Network-level trust for preview | Sufficient for controlled preview environment |
| Reconnection | Stateless (new `workerId` on reconnect) | No sticky sessions for preview |

---

## Out of Scope (post-preview)

- Scale controller integration
- Sidecar / wrapper process
- `WorkerConnect` consolidated protocol message (the prototype uses a new message type 50; we use the existing init sequence for preview)
- ApplicationId-based routing
- gRPC auth
- SquashFS mounting / package download

---

## Milestone Plan

| Milestone | Description | Target |
|---|---|---|
| M1 | Extract `WorkerChannelBase` from `GrpcWorkerChannel` | Late March |
| M2 | `ConnectedWorkerChannel` + new manager + dispatcher | Early April |
| M3 | Inbound connection acceptance in `FunctionRpcService` + `WorkerConnectionService` | Mid April |
| M4 | Configuration, DI wiring, trigger readiness gate | Late April |
| M5 | Hardening, observability, integration tests | May |
| Buffer | Preview prep, docs, feedback | June |

---

## New Files

```
src/WebJobs.Script.Grpc/
  Channel/
    WorkerChannelBase.cs                        (M1)
  ExternalWorkers/
    ConnectedWorkerChannel.cs                   (M2)
    ConnectedWorkerChannelFactory.cs            (M2)
    IConnectedWorkerChannelManager.cs           (M2)
    ConnectedWorkerChannelManager.cs            (M2)
    ConnectedWorkerInvocationDispatcher.cs      (M2)
    WorkerConnectionService.cs                  (M3)
    WorkerConnectedEvent.cs                     (M3)
    ExternalWorkerOptions.cs                    (M4)
    ExternalWorkerServiceCollectionExtensions.cs (M4)
```

## Modified Files

```
src/WebJobs.Script.Grpc/Channel/GrpcWorkerChannel.cs    (M1 — slim to lifecycle-only)
src/WebJobs.Script.Grpc/Server/FunctionRpcService.cs    (M3 — add inbound branch)
```

---

## M1: Extract `WorkerChannelBase`

### What moves

Everything in `GrpcWorkerChannel.cs` **except**:
- `_rpcWorkerProcess` field
- The `FileEvent` subscription in the constructor
- `IWorkerProcess WorkerProcess => _rpcWorkerProcess` property
- `StartWorkerProcessAsync()` — process spawn + wait
- `StopWorkerProcess()` — sends `WorkerTerminate` + waits for exit
- `Dispose(bool)` call on the process

### The one protocol method that touches process state

`AddAdditionalTraceContext()` at line 1723 uses `_rpcWorkerProcess.Id`. Solved via a virtual property:

```csharp
// WorkerChannelBase.cs
protected virtual int WorkerProcessId => -1;

private void AddAdditionalTraceContext(InvocationRequest invocationRequest, ScriptInvocationContext context)
{
    // ...
    attributes[ScriptConstants.LogPropertyProcessIdKey] = Convert.ToString(WorkerProcessId);
    // ...
}
```

```csharp
// GrpcWorkerChannel.cs
protected override int WorkerProcessId => _rpcWorkerProcess.Id;
```

`FunctionEnvironmentReloadResponse()` also logs `_rpcWorkerProcess.Id` (line 487) — same pattern:

```csharp
// base class version
_workerChannelLogger.LogDebug(
    "Received FunctionEnvironmentReloadResponse from WorkerProcess with Pid: '{0}'",
    WorkerProcessId);
```

### Result: `GrpcWorkerChannel.cs` after M1

The slimmed file is ~150 lines. It only contains:

```csharp
// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Script.Config;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Eventing;
using Microsoft.Azure.WebJobs.Script.Workers;
using Microsoft.Azure.WebJobs.Script.Workers.Rpc;
using Microsoft.Azure.WebJobs.Script.Workers.SharedMemoryDataTransfer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    internal sealed class GrpcWorkerChannel : WorkerChannelBase
    {
        private IWorkerProcess _rpcWorkerProcess;

        internal GrpcWorkerChannel(
            string workerId,
            IScriptEventManager eventManager,
            IScriptHostManager hostManager,
            RpcWorkerConfig workerConfig,
            IWorkerProcess rpcWorkerProcess,
            ILogger logger,
            IMetricsLogger metricsLogger,
            int attemptCount,
            IEnvironment environment,
            IOptionsMonitor<ScriptApplicationHostOptions> applicationHostOptions,
            ISharedMemoryManager sharedMemoryManager,
            IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
            IOptions<FunctionsHostingConfigOptions> hostingConfigOptions,
            IHttpProxyService httpProxyService)
            : base(workerId, eventManager, hostManager, workerConfig, logger, metricsLogger,
                   attemptCount, environment, applicationHostOptions, sharedMemoryManager,
                   workerConcurrencyOptions, hostingConfigOptions, httpProxyService)
        {
            _rpcWorkerProcess = rpcWorkerProcess;

            // File-change restart is only meaningful when the host owns the process
            _eventSubscriptions.Add(_eventManager.OfType<FileEvent>()
                .Where(msg => workerConfig.Description.Extensions.Contains(
                    System.IO.Path.GetExtension(msg.FileChangeArguments.FullPath)))
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Subscribe(msg => _eventManager.Publish(
                    new HostRestartEvent($"Worker monitored file changed: '{msg.FileChangeArguments.Name}'."))));
        }

        public override IWorkerProcess WorkerProcess => _rpcWorkerProcess;

        protected override int WorkerProcessId => _rpcWorkerProcess.Id;

        public override async Task StartWorkerProcessAsync(CancellationToken cancellationToken)
        {
            // Set up the protocol callbacks BEFORE starting the process (avoids a race condition)
            BeginInboundProcessing(_workerConfig.CountOptions.ProcessStartupTimeout);

            _workerChannelLogger.LogDebug("Initiating Worker Process start up");
            await _rpcWorkerProcess.StartProcessAsync(cancellationToken);
            _state |= RpcWorkerChannelState.Initializing;

            Task<int> exited = _rpcWorkerProcess.WaitForExitAsync(cancellationToken);
            Task winner = await Task.WhenAny(_workerInitTask.Task, exited).WaitAsync(cancellationToken);
            await winner;

            if (winner == exited)
            {
                throw new WorkerProcessExitException("Worker process exited before initializing.")
                {
                    ExitCode = await exited,
                };
            }
        }

        protected override void OnDisposing()
        {
            StopWorkerProcess();
        }

        protected override void DisposeWorkerResources()
        {
            (_rpcWorkerProcess as IDisposable)?.Dispose();
        }

        private void StopWorkerProcess()
        {
            bool capabilityEnabled = !string.IsNullOrEmpty(
                _workerCapabilities.GetCapabilityState(RpcWorkerConstants.HandlesWorkerTerminateMessage));

            if (!capabilityEnabled)
            {
                return;
            }

            int gracePeriod = WorkerConstants.WorkerTerminateGracePeriodInSeconds;
            _workerChannelLogger.LogDebug(
                "Sending WorkerTerminate message with grace period of {gracePeriod} seconds.", gracePeriod);

            SendStreamingMessage(new StreamingMessage
            {
                WorkerTerminate = new WorkerTerminate
                {
                    GracePeriod = Duration.FromTimeSpan(TimeSpan.FromSeconds(gracePeriod))
                }
            });

            WorkerProcess.WaitForProcessExitInMilliSeconds(gracePeriod * 1000);
        }
    }
}
```

### `WorkerChannelBase.cs` signature (new file, ~1700 lines)

The base class holds everything that moved out of `GrpcWorkerChannel`. Key structural points:

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc
{
    internal abstract partial class WorkerChannelBase : IRpcWorkerChannel, IDisposable
    {
        // All 46 protocol fields (everything except _rpcWorkerProcess)
        // protected so GrpcWorkerChannel can reference _eventSubscriptions, _workerConfig, etc.
        protected readonly IScriptEventManager _eventManager;
        protected readonly RpcWorkerConfig _workerConfig;
        protected readonly string _workerId;
        protected readonly ConcurrentBag<IDisposable> _inputLinks;
        protected readonly List<IDisposable> _eventSubscriptions;
        // ... (all other fields)

        protected RpcWorkerChannelState _state;
        protected TaskCompletionSource<bool> _workerInitTask;
        protected GrpcCapabilities _workerCapabilities;
        protected ILogger _workerChannelLogger;

        // Hook: overridden by GrpcWorkerChannel to return _rpcWorkerProcess.Id
        protected virtual int WorkerProcessId => -1;

        // Hook: called at the start of Dispose() before cleanup
        // GrpcWorkerChannel overrides to call StopWorkerProcess()
        protected virtual void OnDisposing() { }

        // Hook: called during Dispose(bool) between inputLinks cleanup and eventSubscriptions cleanup
        // GrpcWorkerChannel overrides to dispose _rpcWorkerProcess
        protected virtual void DisposeWorkerResources() { }

        // Called by GrpcWorkerChannel.StartWorkerProcessAsync() — sets up protocol callbacks and starts the inbound loop
        protected void BeginInboundProcessing(TimeSpan startStreamTimeout)
        {
            RegisterCallbackForNextGrpcMessage(
                MsgType.StartStream,
                startStreamTimeout,
                count: 1,
                SendWorkerInitRequest,
                HandleWorkerStartStreamError);

            _ = ProcessInbound();
        }

        // AcceptConnectionAsync replaces StartWorkerProcessAsync for ConnectedWorkerChannel
        // The channel is already connected; just start the inbound loop.
        public virtual Task AcceptConnectionAsync()
        {
            _ = ProcessInbound();
            // Worker will send StartStream; our callback already handles it
            return Task.CompletedTask;
        }

        // Abstract: each subclass decides how it starts/accepts
        public abstract Task StartWorkerProcessAsync(CancellationToken cancellationToken);

        // Virtual: subclasses override if they have a process (GrpcWorkerChannel)
        public virtual IWorkerProcess WorkerProcess => null;

        // Dispose — unchanged logic, uses hooks
        public void Dispose()
        {
            OnDisposing();
            _disposing = true;
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // ... cleanup executingInvocations, startLatencyMetric, timer, activeHostChanged ...

                    foreach (var link in _inputLinks) link?.Dispose();

                    DisposeWorkerResources(); // <-- hook for process disposal

                    foreach (var sub in _eventSubscriptions) sub?.Dispose();

                    _eventManager.RemoveGrpcChannels(_workerId);
                }
                _disposed = true;
            }
        }

        // All protocol methods remain here verbatim:
        // ProcessInbound(), DispatchMessage(), ProcessItem(), SendWorkerInitRequest(),
        // WorkerInitResponse(), SetupFunctionInvocationBuffers(), SendFunctionLoadRequests(),
        // InvokeAsync(), InvokeResponse(), GetFunctionMetadata(), Shutdown(), DrainInvocationsAsync(),
        // GetWorkerStatusAsync(), SendWorkerWarmupRequest(), SendFunctionEnvironmentReloadRequest(),
        // FunctionEnvironmentReloadResponse(), etc.
    }
}
```

---

## M2: `ConnectedWorkerChannel`

### New file: `ExternalWorkers/ConnectedWorkerChannel.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>
    /// A worker channel for a worker that connected inbound — the host did not spawn a process.
    /// Lifecycle: the gRPC stream IS the lifecycle; disconnection → WorkerErrorEvent.
    /// </summary>
    internal sealed class ConnectedWorkerChannel : WorkerChannelBase
    {
        internal ConnectedWorkerChannel(
            string workerId,
            IScriptEventManager eventManager,
            IScriptHostManager hostManager,
            RpcWorkerConfig workerConfig,
            ILogger logger,
            IMetricsLogger metricsLogger,
            IEnvironment environment,
            IOptionsMonitor<ScriptApplicationHostOptions> applicationHostOptions,
            ISharedMemoryManager sharedMemoryManager,
            IOptions<WorkerConcurrencyOptions> workerConcurrencyOptions,
            IOptions<FunctionsHostingConfigOptions> hostingConfigOptions,
            IHttpProxyService httpProxyService)
            : base(workerId, eventManager, hostManager, workerConfig, logger, metricsLogger,
                   attemptCount: 0, environment, applicationHostOptions, sharedMemoryManager,
                   workerConcurrencyOptions, hostingConfigOptions, httpProxyService)
        {
        }

        // No IWorkerProcess — gRPC stream health IS the worker health
        public override IWorkerProcess WorkerProcess => null;

        // Called by WorkerConnectionService when a new inbound connection is ready.
        // The gRPC channels are already set up by FunctionRpcService.
        public override Task StartWorkerProcessAsync(CancellationToken cancellationToken)
        {
            // Register for StartStream — the worker will send it momentarily
            BeginInboundProcessing(startStreamTimeout: TimeSpan.FromSeconds(30));
            return Task.CompletedTask;
        }

        public override async Task<WorkerStatus> GetWorkerStatusAsync()
        {
            // Stream health check — attempt a WorkerStatusRequest if capability is present;
            // otherwise return healthy (stream is open)
            return await base.GetWorkerStatusAsync();
        }

        // No process to terminate on dispose; graceful stream close happens
        // when the inbound channel is closed (FunctionRpcService detects cancellation).
        protected override void OnDisposing() { }
        protected override void DisposeWorkerResources() { }
    }
}
```

### New file: `ExternalWorkers/ConnectedWorkerChannelFactory.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    internal class ConnectedWorkerChannelFactory
    {
        private readonly IScriptEventManager _eventManager;
        private readonly IScriptHostManager _hostManager;
        private readonly IEnvironment _environment;
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _applicationHostOptions;
        private readonly ISharedMemoryManager _sharedMemoryManager;
        private readonly IOptions<WorkerConcurrencyOptions> _workerConcurrencyOptions;
        private readonly IOptions<FunctionsHostingConfigOptions> _hostingConfigOptions;
        private readonly IHttpProxyService _httpProxyService;
        private readonly ILoggerFactory _loggerFactory;
        private readonly IMetricsLogger _metricsLogger;
        private readonly IOptions<ExternalWorkerOptions> _options;

        public ConnectedWorkerChannelFactory(/* ...DI params... */) { /* store all */ }

        public ConnectedWorkerChannel Create(string workerId, RpcWorkerConfig workerConfig)
        {
            var logger = _loggerFactory.CreateLogger($"Worker.{workerId}");
            return new ConnectedWorkerChannel(
                workerId, _eventManager, _hostManager, workerConfig,
                logger, _metricsLogger, _environment, _applicationHostOptions,
                _sharedMemoryManager, _workerConcurrencyOptions,
                _hostingConfigOptions, _httpProxyService);
        }
    }
}
```

### New file: `ExternalWorkers/IConnectedWorkerChannelManager.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    public interface IConnectedWorkerChannelManager
    {
        void AddChannel(string workerId, ConnectedWorkerChannel channel);
        ConnectedWorkerChannel GetChannel(string workerId);
        IReadOnlyDictionary<string, ConnectedWorkerChannel> GetChannels();
        Task ShutdownChannelAsync(string workerId);
    }
}
```

### New file: `ExternalWorkers/ConnectedWorkerChannelManager.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    internal class ConnectedWorkerChannelManager : IConnectedWorkerChannelManager
    {
        private readonly ConcurrentDictionary<string, ConnectedWorkerChannel> _channels = new();

        public void AddChannel(string workerId, ConnectedWorkerChannel channel)
            => _channels[workerId] = channel;

        public ConnectedWorkerChannel GetChannel(string workerId)
            => _channels.TryGetValue(workerId, out var ch) ? ch : null;

        public IReadOnlyDictionary<string, ConnectedWorkerChannel> GetChannels()
            => _channels;

        public async Task ShutdownChannelAsync(string workerId)
        {
            if (_channels.TryRemove(workerId, out var channel))
            {
                await channel.DrainInvocationsAsync();
                channel.Dispose();
            }
        }
    }
}
```

### New file: `ExternalWorkers/ConnectedWorkerInvocationDispatcher.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>
    /// Invocation dispatcher for external (separately-hosted) workers.
    /// Only does routing — no process management, no restart logic.
    /// Registered as IFunctionInvocationDispatcher in external worker mode.
    /// </summary>
    internal class ConnectedWorkerInvocationDispatcher : IFunctionInvocationDispatcher
    {
        private readonly IConnectedWorkerChannelManager _channelManager;
        private readonly ILogger<ConnectedWorkerInvocationDispatcher> _logger;

        public ConnectedWorkerInvocationDispatcher(
            IConnectedWorkerChannelManager channelManager,
            ILogger<ConnectedWorkerInvocationDispatcher> logger)
        {
            _channelManager = channelManager;
            _logger = logger;
        }

        public FunctionInvocationDispatcherState State { get; private set; }
            = FunctionInvocationDispatcherState.Initialized;

        public async Task InvokeAsync(ScriptInvocationContext invocationContext,
            CancellationToken cancellationToken = default)
        {
            // Pick any ready channel (round-robin / load-balance in M5)
            var channels = _channelManager.GetChannels();
            var channel = channels.Values
                .FirstOrDefault(c => c.IsChannelReadyForInvocations());

            if (channel is null)
            {
                throw new InvalidOperationException("No connected worker channel is ready for invocations.");
            }

            await channel.InvokeAsync(invocationContext);
        }

        public async Task<IDictionary<string, WorkerStatus>> GetWorkerStatusesAsync()
        {
            var result = new Dictionary<string, WorkerStatus>();
            foreach (var (id, channel) in _channelManager.GetChannels())
            {
                result[id] = await channel.GetWorkerStatusAsync();
            }
            return result;
        }

        public Task<bool> RestartWorkerAsync(string workerId) => Task.FromResult(false); // no-op for external workers

        public Task<bool> ShutdownAsync() => Task.FromResult(true);

        public Task<bool> ShutdownWorkerAsync(string workerId) => Task.FromResult(true);

        public void Dispose() { }
    }
}
```

---

## M3: Inbound Connection Acceptance

### Modified: `FunctionRpcService.cs`

The current `EventStream` handler (line 54) rejects unknown `workerId`s silently. We add a branch:

```csharp
// Current code at line 54:
if (!string.IsNullOrEmpty(workerId) && _eventManager.TryGetGrpcChannels(workerId, out var inbound, out var outbound))
{
    // existing path — host-managed workers
}

// NEW: after the existing block (i.e., else branch for unknown workerIds):
else if (!string.IsNullOrEmpty(workerId) && _externalWorkerOptions.Value.IsEnabled)
{
    // Worker is connecting inbound. Create gRPC channels on-the-fly for this workerId.
    _eventManager.AddGrpcChannels(workerId);
    if (_eventManager.TryGetGrpcChannels(workerId, out inbound, out outbound))
    {
        // Notify the channel factory that a new inbound worker is connecting
        _workerConnectionService.NotifyInboundConnection(workerId);

        // Run outbound push in background
        _ = PushFromOutboundToGrpc(workerId, responseStream, outbound.Reader, cts.Token);

        // Inbound pull loop (same as existing path)
        do
        {
            // ... identical pull loop
        }
        while (await await MoveNextAsync(requestStream, cancelSource));
    }
}
```

**Constructor addition** (inject `IOptions<ExternalWorkerOptions>` and `WorkerConnectionService`):

```csharp
public FunctionRpcService(
    IScriptEventManager eventManager,
    ILogger<FunctionRpcService> logger,
    IOptions<ExternalWorkerOptions> externalWorkerOptions = null,      // optional — null when feature disabled
    WorkerConnectionService workerConnectionService = null)            // optional
{
    _eventManager = eventManager;
    _logger = logger;
    _externalWorkerOptions = externalWorkerOptions;
    _workerConnectionService = workerConnectionService;
}
```

> Using optional parameters keeps the existing DI registration valid when external worker support is not added. When `AddExternalWorkerSupport()` is called, the full constructor is used.

### New file: `ExternalWorkers/WorkerConnectedEvent.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>Published when a connected worker completes the WorkerInitResponse handshake.</summary>
    public class WorkerConnectedEvent : ScriptEvent
    {
        public WorkerConnectedEvent(string workerId, string runtime)
            : base(nameof(WorkerConnectedEvent), ScriptEvents.HostStarted)
        {
            WorkerId = workerId;
            Runtime = runtime;
        }

        public string WorkerId { get; }
        public string Runtime { get; }
    }
}
```

### New file: `ExternalWorkers/WorkerConnectionService.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>
    /// Hosted service that responds to inbound worker connections.
    /// Flow: FunctionRpcService detects new workerId → calls NotifyInboundConnection()
    ///       → this service creates a ConnectedWorkerChannel and waits for WorkerInitResponse
    ///       → on WorkerConnectedEvent: calls GetFunctionMetadata() + triggers host (re)init.
    /// </summary>
    internal class WorkerConnectionService : IHostedService
    {
        private readonly ConnectedWorkerChannelFactory _channelFactory;
        private readonly IConnectedWorkerChannelManager _channelManager;
        private readonly IScriptEventManager _eventManager;
        private readonly IScriptHostManager _scriptHostManager;
        private readonly IOptions<ExternalWorkerOptions> _options;
        private readonly ILogger<WorkerConnectionService> _logger;
        private IDisposable _workerConnectedSubscription;

        public WorkerConnectionService(/* ...DI params... */) { /* store all */ }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _workerConnectedSubscription = _eventManager
                .OfType<WorkerConnectedEvent>()
                .Subscribe(OnWorkerConnected);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _workerConnectedSubscription?.Dispose();
            return Task.CompletedTask;
        }

        /// <summary>Called by FunctionRpcService when an unknown workerId appears on StartStream.</summary>
        public void NotifyInboundConnection(string workerId)
        {
            // Create channel; it will process the StartStream callback itself
            // (BeginInboundProcessing is called inside StartWorkerProcessAsync)
            var workerConfig = BuildWorkerConfig(workerId);
            var channel = _channelFactory.Create(workerId, workerConfig);

            // Channel now listens for StartStream → WorkerInitRequest → WorkerInitResponse
            _ = channel.StartWorkerProcessAsync(CancellationToken.None);
        }

        private void OnWorkerConnected(WorkerConnectedEvent evt)
        {
            // Channel finished init — register it and trigger function loading
            var channel = _channelFactory.GetCreatedChannel(evt.WorkerId); // factory tracks pending channels
            if (channel is null) return;

            _channelManager.AddChannel(evt.WorkerId, channel);

            // Trigger host (re)initialization so GetFunctionMetadata() is called
            _ = _scriptHostManager.RestartHostAsync($"External worker connected: {evt.WorkerId}");
        }

        private RpcWorkerConfig BuildWorkerConfig(string workerId)
        {
            // Build a minimal RpcWorkerConfig based on ExternalWorkerOptions (allowed language, etc.)
            // Details depend on what language info is provided with the connection.
            // For preview: a single generic config is acceptable.
            var language = _options.Value.AllowedLanguages?.FirstOrDefault() ?? "unknown";
            return RpcWorkerConfigFactory.CreateMinimal(language);
        }
    }
}
```

**Note on `WorkerConnectedEvent` publishing**: The base class `WorkerChannelBase.WorkerInitResponse()` already fires init-complete logic. We add a publish:

```csharp
// In WorkerChannelBase.WorkerInitResponse() — ONLY when running as ConnectedWorkerChannel:
// Option A: override WorkerInitResponse in ConnectedWorkerChannel to also publish the event
// Option B: base class publishes if IsExternalWorker property is true (cleaner)

// Chosen approach: override in ConnectedWorkerChannel
internal override void WorkerInitResponse(GrpcEvent initEvent)
{
    base.WorkerInitResponse(initEvent);
    _eventManager.Publish(new WorkerConnectedEvent(_workerId, _runtime));
}
```

---

## M4: Configuration & DI

### New file: `ExternalWorkers/ExternalWorkerOptions.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    public class ExternalWorkerOptions
    {
        public bool IsEnabled { get; set; }
        public IList<string> AllowedLanguages { get; set; } = new List<string>();
    }
}
```

**Config key** (via `appsettings.json` or environment):
```
AzureFunctionsJobHost:Workers:ExternalWorkers:Enabled = true
AzureFunctionsJobHost:Workers:ExternalWorkers:AllowedLanguages:0 = node
```

### New file: `ExternalWorkers/ExternalWorkerServiceCollectionExtensions.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    public static class ExternalWorkerServiceCollectionExtensions
    {
        public static IServiceCollection AddExternalWorkerSupport(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            services.Configure<ExternalWorkerOptions>(
                configuration.GetSection("AzureFunctionsJobHost:Workers:ExternalWorkers"));

            services.AddSingleton<ConnectedWorkerChannelFactory>();
            services.AddSingleton<IConnectedWorkerChannelManager, ConnectedWorkerChannelManager>();
            services.AddSingleton<WorkerConnectionService>();
            services.AddHostedService(sp => sp.GetRequiredService<WorkerConnectionService>());

            // Replace the default IFunctionInvocationDispatcher with one that routes to connected channels
            services.AddSingleton<IFunctionInvocationDispatcher, ConnectedWorkerInvocationDispatcher>();

            return services;
        }
    }
}
```

**Called from** `WebScriptHostBuilderExtension.cs` (or `Startup.cs`) conditionally:

```csharp
// In WebScriptHostBuilderExtension.AddWebJobsScriptHost() or similar
var externalWorkerConfig = context.Configuration.GetSection("AzureFunctionsJobHost:Workers:ExternalWorkers");
if (externalWorkerConfig.GetValue<bool>("Enabled"))
{
    services.AddExternalWorkerSupport(context.Configuration);
}
```

### Trigger readiness gate

Before external workers connect, the host must not start trigger listeners. This is handled in the existing `ScriptHost` startup via `IFunctionInvocationDispatcher.State`. `ConnectedWorkerInvocationDispatcher` returns `Initializing` state until at least one channel is ready:

```csharp
public FunctionInvocationDispatcherState State
{
    get
    {
        bool anyReady = _channelManager.GetChannels().Values
            .Any(c => c.IsChannelReadyForInvocations());
        return anyReady
            ? FunctionInvocationDispatcherState.Initialized
            : FunctionInvocationDispatcherState.Initializing;
    }
}
```

---

## M5: Hardening Checklist

- [ ] **Disconnection handling**: when `FunctionRpcService` stream ends for an external worker, publish `WorkerErrorEvent`; `WorkerConnectionService` removes from manager + logs
- [ ] **Drain in-flight invocations** before removing channel on disconnect
- [ ] **Reconnection**: stateless — worker reconnects with a new `workerId`; `WorkerConnectionService` handles it as a fresh connection
- [ ] **Structured logging**: connect / disconnect / init / error events with `workerId`, `runtime`, state
- [ ] **Health check**: report count and state of connected external workers via `IHealthCheck`
- [ ] **Integration tests**:
  - Worker connects out-of-process → functions execute
  - Worker disconnects mid-invocation → fails gracefully
  - Worker reconnects → new invocations succeed
  - Host-managed mode regression (all existing tests pass)

---

## Prototype Notes

The [`feature/worker_refactor` prototype](https://github.com/Azure/azure-functions-host/tree/feature/worker_refactor/prototype) is a full-stack demo (Scale Controller + Worker Sidecar + Runtime Sidecar + Wrapper). For preview, **all sidecar and scale-controller components are out of scope**.

Key prototype patterns that inform our implementation:

| Prototype | Our implementation |
|---|---|
| `w_{8-char-guid}` worker ID from `DefaultWorkerController` | Same format, assigned by external orchestrator |
| `SidecarRpcService` bidirectional relay — stream already exists when worker connects | `FunctionRpcService` branch for unknown `workerId` |
| `WorkerConnect` consolidated message (type 50 in proto) | **Not used for preview** — standard init sequence is sufficient |
| `WorkerState.FunctionMetadata` collected during placeholder phase | `GetFunctionMetadata()` RPC called post `WorkerInitResponse` |
| `SpecializationOrchestrator` triggers host reload | `WorkerConnectionService.OnWorkerConnected()` calls `RestartHostAsync()` |

The `WorkerConnect`/`WorkerConnectResponse` proto messages (types 50/51) defined in the prototype consolidate the multi-message handshake into a single round-trip. That optimization is post-preview.

---

## Key Invariants

1. **Zero changes to `RpcFunctionInvocationDispatcher`** — it remains untouched for host-managed workers.
2. **Zero changes to `GrpcWorkerChannelFactory`** — process-based channel creation is unchanged.
3. **`FunctionRpcService` changes are branch-only** — existing path for known `workerId`s is unmodified.
4. **`WorkerChannelBase` is a pure refactor** — all existing tests pass without modification (GrpcWorkerChannel behavior is identical).
5. **External worker mode requires explicit opt-in** — `ExternalWorkers:Enabled = true` in config; no impact on existing deployments.
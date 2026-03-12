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
| host.json delivery | Worker sends host.json content via gRPC `StartStream`; host blocks in `ConfigureAppConfiguration` until received | Host has no access to customer payload in separated compute; gRPC server is already listening (WebHost-level) before ScriptHost is built |

---

## Out of Scope (post-preview)

See [`compute_separation_p2.md`](./compute_separation_p2.md) for detailed designs of lower-priority items.

- Scale controller integration
- Sidecar / wrapper process
- `WorkerConnect` consolidated protocol message (the prototype uses a new message type 50; we use the existing init sequence for preview)
- ApplicationId-based routing
- gRPC auth
- SquashFS mounting / package download
- Extension assembly loading for non-bundle / dotnet-isolated apps (host-initiated streaming from worker)

---

## Milestone Plan

| Milestone | Description | Target |
|---|---|---|
| M1 | Extract `WorkerChannelBase` from `GrpcWorkerChannel` | Late March |
| M2 | `ConnectedWorkerChannel` + new manager + dispatcher | Early April |
| M2b | `ConnectedWorkerFunctionMetadataProvider` — metadata without process spawn | Early April |
| M3 | Inbound connection acceptance in `FunctionRpcService` + `WorkerConnectionService` | Mid April |
| M3b | host.json delivery via gRPC — `HostJsonContentProvider` + `ExternalWorkerHostJsonConfigurationSource` | Mid April |
| M4 | Configuration, DI wiring, trigger readiness gate | Late April |
| M4b | Placeholder mode support — warmup without a worker, specialization flow | Late April |
| M5 | Hardening, observability, integration tests | May |
| Buffer | Preview prep, docs, feedback | June |

---

## New Files

```
src/WebJobs.Script.Grpc/
  Channel/
    WorkerChannelBase.cs                              (M1)
  ExternalWorkers/
    ConnectedWorkerChannel.cs                         (M2)
    ConnectedWorkerChannelFactory.cs                  (M2)
    IConnectedWorkerChannelManager.cs                 (M2)
    ConnectedWorkerChannelManager.cs                  (M2)
    ConnectedWorkerInvocationDispatcher.cs            (M2)
    ConnectedWorkerFunctionMetadataProvider.cs        (M2b)
    HostJsonContentProvider.cs                        (M3b)
    ExternalWorkerHostJsonConfigurationSource.cs      (M3b)
    WorkerConnectionService.cs                        (M3)
    WorkerConnectedEvent.cs                           (M3)
    ExternalWorkerOptions.cs                          (M4)
    ExternalWorkerServiceCollectionExtensions.cs      (M4)

azure-functions-language-worker-protobuf/
  src/proto/FunctionRpc.proto                         (M3b — add host_configuration_json to StartStream)
```

## Modified Files

```
src/WebJobs.Script.Grpc/Channel/GrpcWorkerChannel.cs           (M1 — slim to lifecycle-only)
src/WebJobs.Script.Grpc/Server/FunctionRpcService.cs           (M3 — add inbound branch; M3b — intercept host.json)
src/WebJobs.Script/ScriptHostBuilderExtensions.cs              (M3b — conditional config source swap)
src/WebJobs.Script.Grpc/Rpc/RpcInitializationService.cs       (M4b — skip placeholder worker spawn in external mode)
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

        /// <summary>
        /// Waits until at least one fully-initialized channel is available.
        /// Used by ConnectedWorkerFunctionMetadataProvider to block metadata retrieval
        /// until an external worker has connected and completed the init handshake.
        /// Signaled internally when AddChannel() is called for the first time.
        /// </summary>
        Task<IRpcWorkerChannel> WaitForChannelAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
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
        private readonly TaskCompletionSource<IRpcWorkerChannel> _firstChannelReady = new();

        public void AddChannel(string workerId, ConnectedWorkerChannel channel)
        {
            _channels[workerId] = channel;

            // Signal WaitForChannelAsync() on the first successfully-added channel
            _firstChannelReady.TrySetResult(channel);
        }

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

        public async Task<IRpcWorkerChannel> WaitForChannelAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            // If a channel is already available, return immediately
            var ready = _channels.Values.FirstOrDefault(c => c.IsChannelReadyForInvocations());
            if (ready is not null)
            {
                return ready;
            }

            // Block until AddChannel() signals the TCS, or timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                return await _firstChannelReady.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"No external worker connected within {timeout.TotalSeconds} seconds.");
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

## M2b: `ConnectedWorkerFunctionMetadataProvider`

### Problem: process spawn during metadata retrieval

The existing metadata path starts a worker process **before** the dispatcher is even initialized:

```
ScriptHost.InitializeAsync()
  → GetFunctionsMetadata()
    → FunctionMetadataManager.GetFunctionMetadata()
      → FunctionMetadataProvider.GetFunctionMetadataAsync()
        → [CanWorkerIndex() == true]
          → WorkerFunctionMetadataProvider.GetFunctionMetadataAsync()
            → _channelManager.GetChannels() — no channels yet!
              → _channelManager.InitializeChannelAsync()     ← STARTS PROCESS
```

`WorkerFunctionMetadataProvider` depends on `IWebHostRpcWorkerChannelManager`. When no channels exist, it calls `InitializeChannelAsync()` which creates a `GrpcWorkerChannel` and spawns the worker process. This happens at line ~91 of `WorkerFunctionMetadataProvider.cs`, well before `RpcFunctionInvocationDispatcher.InitializeAsync()` runs.

For external workers we cannot spawn — we must **wait** for an inbound connection.

### Solution: new `IWorkerFunctionMetadataProvider` implementation

```
                     IWorkerFunctionMetadataProvider
                        /                    \
    WorkerFunctionMetadataProvider    ConnectedWorkerFunctionMetadataProvider
    (existing — starts processes)     (new — waits for inbound connection)
           |                                    |
    IWebHostRpcWorkerChannelManager      IConnectedWorkerChannelManager
```

The orchestrator (`FunctionMetadataProvider`) and the host-indexing fallback are untouched. Only the `IWorkerFunctionMetadataProvider` implementation is swapped via DI when external workers are enabled.

### New file: `ExternalWorkers/ConnectedWorkerFunctionMetadataProvider.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>
    /// Metadata provider for external (separately-hosted) workers.
    /// Instead of starting a worker process to retrieve metadata, waits for an
    /// external worker to connect inbound and complete the init handshake, then
    /// calls GetFunctionMetadata() over the existing gRPC channel.
    ///
    /// Registered as IWorkerFunctionMetadataProvider when external workers are enabled.
    /// </summary>
    internal class ConnectedWorkerFunctionMetadataProvider : IWorkerFunctionMetadataProvider
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

        private readonly IConnectedWorkerChannelManager _channelManager;
        private readonly IOptionsMonitor<ScriptApplicationHostOptions> _scriptOptions;
        private readonly ILogger<ConnectedWorkerFunctionMetadataProvider> _logger;
        private readonly IEnvironment _environment;

        private ImmutableArray<FunctionMetadata> _functions;

        public ImmutableDictionary<string, ImmutableArray<string>> FunctionErrors { get; private set; }
            = ImmutableDictionary<string, ImmutableArray<string>>.Empty;

        public ConnectedWorkerFunctionMetadataProvider(
            IConnectedWorkerChannelManager channelManager,
            IOptionsMonitor<ScriptApplicationHostOptions> scriptOptions,
            ILogger<ConnectedWorkerFunctionMetadataProvider> logger,
            IEnvironment environment)
        {
            _channelManager = channelManager;
            _scriptOptions = scriptOptions;
            _logger = logger;
            _environment = environment;
        }

        public async Task<FunctionMetadataResult> GetFunctionMetadataAsync(
            IEnumerable<RpcWorkerConfig> workerConfigs, bool forceRefresh = false)
        {
            if (!_functions.IsDefaultOrEmpty && !forceRefresh)
            {
                return new FunctionMetadataResult(useDefaultMetadataIndexing: false, _functions);
            }

            _logger.LogInformation("Waiting for external worker to connect for metadata indexing.");

            // Block until an external worker connects and completes init.
            // This replaces the InitializeChannelAsync() call in WorkerFunctionMetadataProvider
            // that would normally spawn a process.
            IRpcWorkerChannel channel = await _channelManager.WaitForChannelAsync(DefaultTimeout);

            _logger.LogInformation(
                "External worker {workerId} connected. Requesting function metadata.",
                channel.Id);

            // Standard metadata retrieval — identical to WorkerFunctionMetadataProvider
            var rawFunctions = await channel.GetFunctionMetadata();

            if (rawFunctions.Any(x => x.UseDefaultMetadataIndexing))
            {
                _logger.LogDebug("External worker opted out of indexing; falling back to host.");
                _functions = ImmutableArray<FunctionMetadata>.Empty;
                return new FunctionMetadataResult(useDefaultMetadataIndexing: true, _functions);
            }

            // Validate metadata (same logic as WorkerFunctionMetadataProvider.ValidateMetadata)
            var validated = ValidateMetadata(rawFunctions);
            _functions = validated.ToImmutableArray();

            return new FunctionMetadataResult(useDefaultMetadataIndexing: false, _functions);
        }

        private IEnumerable<FunctionMetadata> ValidateMetadata(IEnumerable<RawFunctionMetadata> rawFunctions)
        {
            // Same validation as WorkerFunctionMetadataProvider:
            // - validate function names, bindings, retry options
            // - populate FunctionErrors for invalid entries
            // Implementation mirrors WorkerFunctionMetadataProvider.ValidateMetadata()
            // (consider extracting shared validation into a static helper)
        }
    }
}
```

### Startup sequence with this provider

```
1. Host starts → FunctionRpcService listening on gRPC
2. ScriptHost.InitializeAsync() → GetFunctionsMetadata()
3.   → FunctionMetadataProvider.GetFunctionMetadataAsync()
4.     → [CanWorkerIndex() == true]
5.       → ConnectedWorkerFunctionMetadataProvider.GetFunctionMetadataAsync()
6.         → _channelManager.WaitForChannelAsync()       ← BLOCKS here (no process spawn)
7. External worker connects inbound to gRPC
8.   → FunctionRpcService detects unknown workerId
9.   → WorkerConnectionService.NotifyInboundConnection()
10.  → ConnectedWorkerChannel created, init handshake completes
11.  → ConnectedWorkerChannelManager.AddChannel() signals TCS
12. WaitForChannelAsync() returns the channel              ← UNBLOCKS
13.  → channel.GetFunctionMetadata()                       ← standard RPC
14. Metadata flows back through FunctionMetadataManager
15. ScriptHost.InitializeFunctionDescriptorsAsync()
16. ScriptHost.GenerateFunctions() → ScriptTypeLocator.SetTypes()
17. ConnectedWorkerInvocationDispatcher ready for invocations
```

### Design notes

- **Timeout**: 2 minutes matches `ScriptTypeLocator._setWaitTimeout` — if no worker connects in time, the host fails to start with a clear error.
- **Validation sharing**: `ValidateMetadata()` logic should ideally be extracted from `WorkerFunctionMetadataProvider` into a shared static helper to avoid duplication. This is a low-risk refactor.
- **`FunctionMetadataProvider` unchanged**: The outer orchestrator that decides between worker vs host indexing is not modified.
- **`WorkerFunctionMetadataProvider` unchanged**: The existing process-spawning provider is not modified — the DI swap selects which implementation is active.

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

**Constructor addition** (inject `IOptions<ExternalWorkerOptions>`, `WorkerConnectionService`, and `HostJsonContentProvider`):

```csharp
public FunctionRpcService(
    IScriptEventManager eventManager,
    ILogger<FunctionRpcService> logger,
    IOptions<ExternalWorkerOptions> externalWorkerOptions = null,      // optional — null when feature disabled
    WorkerConnectionService workerConnectionService = null,            // optional
    HostJsonContentProvider hostJsonContentProvider = null)             // optional — for host.json delivery
{
    _eventManager = eventManager;
    _logger = logger;
    _externalWorkerOptions = externalWorkerOptions;
    _workerConnectionService = workerConnectionService;
    _hostJsonContentProvider = hostJsonContentProvider;
}
```

> Using optional parameters keeps the existing DI registration valid when external worker support is not added. When `AddExternalWorkerSupport()` is called, the full constructor is used.

In the external worker branch of `EventStream`, intercept host.json from the first `StartStream` message (see M3b for details):

```csharp
// NEW: after reading the first StartStream message for an external worker:
if (_hostJsonContentProvider is not null
    && !string.IsNullOrEmpty(currentMessage.StartStream?.HostConfigurationJson))
{
    _hostJsonContentProvider.SetContent(currentMessage.StartStream.HostConfigurationJson);
}
```

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

## M3b: host.json Delivery via gRPC

### Problem: host has no access to customer payload

In the separated compute model, the customer's application files (including `host.json`) live in the worker's container. The host cannot read them from disk. However, `host.json` drives critical host configuration: extension settings, logging, function timeouts, health monitoring, HTTP routing, etc.

Today, `HostJsonFileConfigurationSource` reads `host.json` from `ScriptPath` during `ConfigureAppConfiguration`. All downstream option bindings (`ScriptJobHostOptions`, `HostHealthMonitorOptions`, extension configs, etc.) flow from this single configuration source.

### Why blocking in `ConfigureAppConfiguration` works

The gRPC server starts at the **WebHost level** (via `RpcInitializationService`, registered as `IManagedHostedService`) **before** the ScriptHost is built:

```
WebHost starts hosted services (in registration order):
  1. RpcInitializationService.StartAsync() → gRPC server listening ✅
  2. WebJobsScriptHostService.StartAsync() → builds ScriptHost
       → ConfigureAppConfiguration runs    ← gRPC already available
       → IConfigurationProvider.Load()     ← can block here safely
```

Since `IConfigurationProvider.Load()` is synchronous and runs on the `WebJobsScriptHostService` thread, while gRPC requests are handled on Kestrel's thread pool, a blocking wait does not deadlock.

### Proto change: add `host_configuration_json` to `StartStream`

```protobuf
message StartStream {
  string worker_id = 2;

  // Raw host.json content from the worker's filesystem.
  // Sent by external workers in separated compute scenarios.
  // Ignored by the host when external worker mode is disabled.
  string host_configuration_json = 3;
}
```

`StartStream` is the natural location because:
- It is always the first message sent by a worker
- `FunctionRpcService` already reads it to extract `workerId`
- No channel infrastructure is needed — the interception happens at the relay level
- host.json content is available immediately, before any channel or handshake processing

### New file: `ExternalWorkers/HostJsonContentProvider.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>
    /// WebHost-level singleton that bridges gRPC (where the worker sends host.json)
    /// and the ScriptHost configuration pipeline (where host.json is consumed).
    ///
    /// Lifecycle:
    /// - FunctionRpcService calls SetContent() when an external worker's StartStream
    ///   includes host_configuration_json.
    /// - ExternalWorkerHostJsonConfigurationProvider calls WaitForContent() during
    ///   ConfigureAppConfiguration, blocking until content is available.
    /// - On ScriptHost restart, Reset() prepares for the next Build() cycle.
    ///   If cached content exists (worker still connected), the next Load() returns
    ///   immediately without blocking.
    /// </summary>
    internal class HostJsonContentProvider
    {
        private readonly object _lock = new();
        private TaskCompletionSource<string> _tcs = new();
        private string _cachedContent;

        /// <summary>
        /// Called by FunctionRpcService when an external worker provides host.json.
        /// </summary>
        public void SetContent(string hostJsonContent)
        {
            lock (_lock)
            {
                _cachedContent = hostJsonContent;
                _tcs.TrySetResult(hostJsonContent);
            }
        }

        /// <summary>
        /// Called on ScriptHost restart (via ActiveHostChanged event).
        /// Prepares a fresh TCS for the next ConfigureAppConfiguration cycle.
        /// If cached content exists, the next Load() completes immediately.
        /// </summary>
        /// <param name="clearCache">
        /// True when the worker has disconnected and we must wait for a new worker.
        /// False on a normal ScriptHost restart where the worker is still connected.
        /// </param>
        public void Reset(bool clearCache = false)
        {
            lock (_lock)
            {
                if (clearCache)
                {
                    _cachedContent = null;
                }

                _tcs = new TaskCompletionSource<string>();

                if (_cachedContent is not null)
                {
                    _tcs.TrySetResult(_cachedContent);
                }
            }
        }

        /// <summary>
        /// Synchronous blocking wait used by ExternalWorkerHostJsonConfigurationProvider.Load().
        /// Safe because gRPC is already listening on a separate Kestrel instance and the
        /// worker connection is handled on Kestrel's thread pool.
        /// </summary>
        public string WaitForContent(TimeSpan timeout)
        {
            if (_tcs.Task.Wait(timeout))
            {
                return _tcs.Task.Result;
            }

            throw new TimeoutException(
                $"No external worker provided host.json within {timeout.TotalSeconds} seconds. "
                + "Ensure the worker container is running and configured to connect to this host.");
        }
    }
}
```

### New file: `ExternalWorkers/ExternalWorkerHostJsonConfigurationSource.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>
    /// IConfigurationSource that replaces HostJsonFileConfigurationSource in external worker mode.
    /// Instead of reading host.json from disk, blocks until a connected worker provides the
    /// content via gRPC, then parses it identically to HostJsonFileConfigurationSource.
    /// </summary>
    internal class ExternalWorkerHostJsonConfigurationSource : IConfigurationSource
    {
        private readonly HostJsonContentProvider _contentProvider;
        private readonly ILogger _logger;

        public ExternalWorkerHostJsonConfigurationSource(
            HostJsonContentProvider contentProvider,
            ILogger logger)
        {
            _contentProvider = contentProvider;
            _logger = logger;
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new ExternalWorkerHostJsonConfigurationProvider(_contentProvider, _logger);
        }
    }

    internal class ExternalWorkerHostJsonConfigurationProvider : ConfigurationProvider
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
        private readonly HostJsonContentProvider _contentProvider;
        private readonly ILogger _logger;

        public ExternalWorkerHostJsonConfigurationProvider(
            HostJsonContentProvider contentProvider,
            ILogger logger)
        {
            _contentProvider = contentProvider;
            _logger = logger;
        }

        public override void Load()
        {
            _logger.LogInformation(
                "Waiting for external worker to provide host.json via gRPC...");

            string hostJson = _contentProvider.WaitForContent(DefaultTimeout);

            _logger.LogInformation(
                "Received host.json from external worker ({length} bytes). Applying configuration.",
                hostJson.Length);

            // Parse using the same logic as HostJsonFileConfigurationSource:
            // 1. Parse JSON
            // 2. Validate "version" == "2.0"
            // 3. Flatten into the AzureFunctionsJobHost configuration section
            // 4. Populate the Data dictionary
            //
            // Implementation should reuse or mirror HostJsonFileConfigurationSource.LoadHostConfig()
            // to ensure identical configuration key paths.
            var hostConfig = JObject.Parse(hostJson);
            ApplyHostJsonToConfiguration(hostConfig);
        }

        private void ApplyHostJsonToConfiguration(JObject hostConfig)
        {
            // Mirror HostJsonFileConfigurationSource parsing:
            // - Validates version field
            // - Flattens JSON into "AzureFunctionsJobHost:key" configuration keys
            // - Handles well-known sections: extensions, logging, http, queues, etc.
            // (Implementation detail — follows the same structure as
            //  HostJsonFileConfigurationSource.LoadHostConfigurationFile)
        }
    }
}
```

### Modified: `ScriptHostBuilderExtensions.cs`

In `ConfigureAppConfiguration`, conditionally swap the configuration source:

```csharp
.ConfigureAppConfiguration((context, configBuilder) =>
{
    if (!context.Properties.ContainsKey(ScriptConstants.SkipHostJsonConfigurationKey))
    {
        // NEW: check if external workers are enabled
        if (hostJsonContentProvider is not null)
        {
            // External worker mode: block until worker provides host.json via gRPC
            configBuilder.Add(new ExternalWorkerHostJsonConfigurationSource(
                hostJsonContentProvider, loggerFactory.CreateLogger("HostJsonConfig")));
        }
        else
        {
            // Standard mode: read host.json from disk
            HostJsonFileConfigurationOptions hostJsonConfigOptions =
                new(SystemEnvironment.Instance, applicationOptions);
            configBuilder.Add(new HostJsonFileConfigurationSource(
                hostJsonConfigOptions, loggerFactory, metricsLogger));
        }
    }
})
```

The `hostJsonContentProvider` reference is passed into the ScriptHost builder from the WebHost level — the same way `ScriptApplicationHostOptions` is passed today.

### Restart handling via `ActiveHostChanged`

`HostJsonContentProvider` is a WebHost-level singleton that outlives ScriptHost restarts. On each restart, `WebJobsScriptHostService` builds a new ScriptHost, which runs `ConfigureAppConfiguration` → `Load()` again.

Subscribe to `IScriptHostManager.ActiveHostChanged` to reset the provider:

```csharp
// In WebHost-level service registration or HostJsonContentProvider initialization:
scriptHostManager.ActiveHostChanged += (_, e) =>
{
    if (e.NewHost is null)
    {
        // ScriptHost is being torn down for restart.
        // Reset the TCS for the next Build() cycle.
        // Keep cached content — if the worker is still connected,
        // the next Load() returns immediately without blocking.
        hostJsonContentProvider.Reset();
    }
};
```

When the worker disconnects, `WorkerConnectionService` calls `Reset(clearCache: true)` so the next restart blocks until a new worker provides fresh host.json:

```csharp
// In WorkerConnectionService, on worker disconnect:
private void OnWorkerDisconnected(string workerId)
{
    _hostJsonContentProvider.Reset(clearCache: true);
    // ... channel cleanup ...
}
```

### Restart scenarios

| Scenario | HostJsonContentProvider state | Next `Load()` behavior |
|---|---|---|
| ScriptHost restarts, worker still connected | `Reset()` — cache preserved | Returns immediately (no blocking) |
| Worker disconnects, then ScriptHost restarts | `Reset(clearCache: true)` — cache cleared | Blocks until new worker connects |
| Worker reconnects with new host.json | `SetContent(newJson)` — cache updated | Next restart uses new content |
| Specialization (placeholder → app) | `ActiveHostChanged` → `Reset()` | Returns from cache or blocks |

### Full timeline

```
T0  WebHost starts
T1  RpcInitializationService.StartAsync() → gRPC listening on port N
T2  WebJobsScriptHostService.StartAsync() → starts building ScriptHost
T3    ConfigureAppConfiguration
T4      ExternalWorkerHostJsonConfigurationProvider.Load() → BLOCKS on WaitForContent()
          ↕ (meanwhile, on gRPC Kestrel thread pool)
T5      Worker connects → StartStream { worker_id: "w_abc123", host_configuration_json: "{...}" }
T6      FunctionRpcService reads StartStream → _hostJsonContentProvider.SetContent(json)
T7    Load() UNBLOCKS → parses host.json → configuration pipeline continues
T8    ScriptHost services configured with correct host.json values
T9  ScriptHost.StartAsync() → InitializeAsync()
T10   ConnectedWorkerFunctionMetadataProvider.WaitForChannelAsync() → BLOCKS
        ↕ (channel handshake continues on gRPC thread)
T11   WorkerInitRequest → WorkerInitResponse → channel ready
T12   ConnectedWorkerChannelManager.AddChannel() → signals TCS
T13 WaitForChannelAsync() UNBLOCKS → channel.GetFunctionMetadata()
T14 Metadata → InitializeFunctionDescriptors → GenerateFunctions → SetTypes
T15 Host fully initialized, triggers ready, invocations flow
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

            // host.json delivery — WebHost-level singleton shared between gRPC and ScriptHost config
            services.AddSingleton<HostJsonContentProvider>();

            services.AddSingleton<ConnectedWorkerChannelFactory>();
            services.AddSingleton<IConnectedWorkerChannelManager, ConnectedWorkerChannelManager>();
            services.AddSingleton<WorkerConnectionService>();
            services.AddHostedService(sp => sp.GetRequiredService<WorkerConnectionService>());

            // Replace the default IFunctionInvocationDispatcher with one that routes to connected channels
            services.AddSingleton<IFunctionInvocationDispatcher, ConnectedWorkerInvocationDispatcher>();

            // Replace the default IWorkerFunctionMetadataProvider so metadata retrieval
            // waits for an inbound worker connection instead of spawning a process.
            // Must be registered BEFORE the TryAddSingleton in RpcServiceCollectionExtensions to win.
            services.AddSingleton<IWorkerFunctionMetadataProvider, ConnectedWorkerFunctionMetadataProvider>();

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

## M4b: Placeholder Mode Support

### Background

In Azure Functions, the host starts in **placeholder mode** (`AZURE_WEBSITE_PLACEHOLDER_MODE=1`) before a real function app is assigned. This pre-warms the host to reduce cold-start latency. Once the real app arrives, the host **specializes** — restarts with the real configuration and functions.

Today, placeholder mode pre-starts worker processes so they are warm and ready for specialization. In the separated compute model, no worker exists during placeholder — the worker arrives after specialization as an inbound gRPC connection.

### What the warmup does today

`HostWarmupMiddleware.WarmupInvoke()` runs on `/api/WarmUp` requests during placeholder:

| Step | Method | What it does | Needs a worker? |
|---|---|---|---|
| 1 | `PreJitPrepare()` | Pre-JITs methods from `coldstart.jittrace` via `JitTraceRuntime.Prepare()` | ❌ No |
| 2 | `ReadRuntimeAssemblyFiles()` | Reads all DLLs into OS page cache to avoid disk I/O during specialization | ❌ No |
| 3 | `HostWarmupAsync()` | Optionally restarts host if `?restart=1` query param | ❌ No |
| 4 | `WorkerWarmupAsync()` | Sends `WorkerWarmupRequest` gRPC message to pre-started worker | ✅ Yes |

Additionally during placeholder:

| Component | What it does | Needs a worker? |
|---|---|---|
| `StandbyInitializationService` | Creates synthetic `WarmUp` function in temp directory + starts specialization timer | ❌ No |
| `RpcInitializationService.InitializeChannelsAsync()` | Spawns worker processes from `FUNCTIONS_WORKER_RUNTIME_PLACEHOLDER_MODE_LIST` | ✅ Yes |
| `WebJobsScriptHostService` | Builds and starts ScriptHost (loads synthetic WarmUp function, host.json from temp dir) | ❌ No |

### Design for separated compute

**Principle**: Warm up everything that doesn't require a worker. Gracefully skip (not fail) worker-dependent steps.

#### Steps that run unchanged

- **JIT warmup** (`PreJitPrepare`) — pure host-side, no change
- **Assembly page-in** (`ReadRuntimeAssemblyFiles`) — pure host-side, no change
- **`StandbyInitializationService`** — creates synthetic WarmUp function, starts specialization timer, no change
- **`WebJobsScriptHostService`** — builds and starts placeholder ScriptHost with synthetic functions, no change
- **Specialization trigger** — `PlaceholderSpecializationMiddleware` detects container readiness, no change

#### Steps that change

**1. `RpcInitializationService.InitializeChannelsAsync()` — skip worker spawn**

```csharp
internal Task InitializeChannelsAsync()
{
    if (_placeholderLanguageWorkersList is null)
    {
        throw new ArgumentNullException(nameof(_placeholderLanguageWorkersList));
    }

    // NEW: skip placeholder worker pre-start in external worker mode —
    // no process to spawn; workers connect inbound after specialization.
    if (_externalWorkerOptions?.Value?.IsEnabled == true)
    {
        _logger.LogDebug("External worker mode enabled. Skipping placeholder worker initialization.");
        return Task.CompletedTask;
    }

    if (_environment.IsPlaceholderModeEnabled())
    {
        // ... existing placeholder worker pre-start logic (unchanged)
    }
    return Task.CompletedTask;
}
```

**2. `ExternalWorkerHostJsonConfigurationSource.Load()` — placeholder-aware**

During placeholder, the config source should return default/minimal configuration without blocking (no worker to provide host.json yet). After specialization triggers a host restart, it blocks until the worker connects with the real host.json.

```csharp
public override void Load()
{
    if (_environment.IsPlaceholderModeEnabled())
    {
        // During placeholder: use minimal default config.
        // Real host.json will arrive after specialization when a worker connects.
        _logger.LogDebug("Placeholder mode: using default host.json configuration.");
        ApplyDefaultHostJson();
        return;
    }

    // Post-specialization: block until worker provides real host.json
    _logger.LogInformation("Waiting for external worker to provide host.json via gRPC...");
    string hostJson = _contentProvider.WaitForContent(DefaultTimeout);
    ApplyHostJsonToConfiguration(JObject.Parse(hostJson));
}

private void ApplyDefaultHostJson()
{
    // Minimal host.json equivalent: { "version": "2.0" }
    // This matches the synthetic host.json written by StandbyManager.CreateStandbyWarmupFunctions()
    Data[$"{ConfigurationSectionNames.JobHost}:version"] = "2.0";
}
```

**3. `WorkerWarmupAsync()` — already a graceful no-op**

`WebHostRpcWorkerChannelManager.WorkerWarmupAsync()` already handles the no-worker case:

```csharp
public async Task WorkerWarmupAsync()
{
    // ...
    IRpcWorkerChannel rpcWorkerChannel = await GetChannelAsync(_workerRuntime);
    if (rpcWorkerChannel != null)  // ← null when no channels exist → no-op
    {
        rpcWorkerChannel.SendWorkerWarmupRequest();
    }
}
```

No changes needed — when no channels are pre-started (external worker mode), this is already a no-op.

**4. `StandbyManager.SpecializeHostCoreAsync()` — `_workerManager.SpecializeAsync()` is a no-op**

There are no placeholder worker channels to specialize or reuse. The existing code handles this gracefully:
- `WebHostRpcWorkerChannelManager.SpecializeAsync()` calls `GetChannelAsync(_workerRuntime)` which returns null when no channels exist
- `_shutdownStandbyWorkerChannels()` is a no-op with no channels to shut down

No changes needed — the specialization step completes quickly and the subsequent `RestartHostAsync()` triggers the real initialization flow where the worker connects.

### Full placeholder → specialization timeline (separated compute)

```
PLACEHOLDER PHASE (no worker present)
──────────────────────────────────────
T0   WebHost starts
T1   RpcInitializationService.StartAsync()
       → gRPC server listening ✅
       → InitializeChannelsAsync() → SKIPPED (external worker mode)
T2   WebJobsScriptHostService.StartAsync() → builds placeholder ScriptHost
       → ConfigureAppConfiguration
       → ExternalWorkerHostJsonConfigurationSource.Load()
       → placeholder mode detected → returns default config (no blocking)
T3   StandbyInitializationService → creates synthetic WarmUp function + specialization timer
T4   Placeholder ScriptHost starts with synthetic WarmUp function
T5   Warmup request arrives (/api/WarmUp)
       → PreJitPrepare(coldstart.jittrace) ✅
       → ReadRuntimeAssemblyFiles() ✅
       → WorkerWarmupAsync() → no channels → no-op ✅
T6   Host is warm, waiting for specialization

SPECIALIZATION PHASE (worker connects)
──────────────────────────────────────
T7   Environment variables change (real app assigned)
T8   PlaceholderSpecializationMiddleware detects: !InStandbyMode && IsContainerReady()
T9   StandbyManager.SpecializeHostCoreAsync()
       → _configuration.Reload()
       → _hostNameProvider.Reset()
       → NotifyChange() (signals StandbyChangeTokenSource)
       → _workerManager.SpecializeAsync() → no channels → quick return
       → _scriptHostManager.RestartHostAsync("Host specialization.")
T10  Old placeholder ScriptHost torn down
T11  New ScriptHost builds
       → ConfigureAppConfiguration
       → ExternalWorkerHostJsonConfigurationSource.Load()
       → NOT in placeholder mode → BLOCKS on WaitForContent()
           ↕ (meanwhile, on gRPC Kestrel thread pool)
T12  External worker connects → StartStream { worker_id, host_configuration_json }
T13  FunctionRpcService → HostJsonContentProvider.SetContent(json)
T14  Load() UNBLOCKS → real host.json applied → configuration pipeline continues
T15  ScriptHost services configured with real host.json values
T16  ScriptHost.InitializeAsync()
       → ConnectedWorkerFunctionMetadataProvider.WaitForChannelAsync()
       → channel handshake completes → metadata retrieved
T17  Functions loaded → triggers started → host fully ready
T18  _scriptHostManager.DelayUntilHostReadyAsync() returns
T19  App serving traffic
```

### What does NOT need mocking

- **`WorkerWarmupAsync`** — already returns gracefully when no channels exist
- **`SpecializeAsync`** — already returns gracefully when no channels exist
- **Synthetic WarmUp function** — still created and loaded; the placeholder ScriptHost works identically
- **`HostWarmupMiddleware`** — all steps run; worker-dependent step is a no-op

### Design notes

- **Minimal changes**: Only two files need modification for placeholder support — `RpcInitializationService` (skip spawn) and `ExternalWorkerHostJsonConfigurationSource` (placeholder-aware `Load()`). Everything else works as-is.
- **Warmup effectiveness**: The host-side warmup (JIT + page-in) provides the majority of cold-start benefit. Worker warmup is a nice-to-have that can be deferred to when the worker actually connects (workers in their own container can warm themselves independently).
- **Specialization timer**: The background timer in `StandbyManager` still runs, detecting environment variable changes. This is the fallback mechanism when no HTTP request triggers specialization.

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
3. **Zero changes to `WorkerFunctionMetadataProvider`** — the existing process-spawning metadata provider is unchanged; `ConnectedWorkerFunctionMetadataProvider` is a parallel implementation swapped via DI.
4. **Zero changes to `FunctionMetadataProvider`** — the outer orchestrator (worker vs host indexing decision) is unchanged.
5. **Zero changes to `HostJsonFileConfigurationSource`** — the existing file-based config source is unchanged; `ExternalWorkerHostJsonConfigurationSource` is a parallel implementation swapped conditionally in `ConfigureAppConfiguration`.
6. **`FunctionRpcService` changes are branch-only** — existing path for known `workerId`s is unmodified.
7. **`WorkerChannelBase` is a pure refactor** — all existing tests pass without modification (GrpcWorkerChannel behavior is identical).
8. **External worker mode requires explicit opt-in** — `ExternalWorkers:Enabled = true` in config; no impact on existing deployments.
9. **gRPC server startup order is unchanged** — `RpcInitializationService` starts at WebHost level before ScriptHost is built; external worker mode relies on this existing ordering.
10. **Placeholder warmup runs identically** — JIT trace, assembly page-in, and synthetic WarmUp function are unchanged. Worker-dependent warmup steps are already graceful no-ops when no channels exist.
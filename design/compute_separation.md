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
| Auth / networking | Auth on outbound gRPC connection deferred to later milestone | Design accommodates future auth; not required for preview |
| Connection direction | Runtime is gRPC **client** connecting outbound; worker behavior unchanged | Networking restrictions prevent inbound connections to the runtime |
| gRPC relay | .NET AOT-compiled relay process co-located with the worker pod (see Sidecar Architecture) | Preserves existing worker gRPC protocol; runtime is agnostic to relay topology |
| Reconnection | Stateless (new `workerId` on reconnect) | No sticky sessions for preview |
| host.json delivery | Worker sends host.json content via gRPC `StartStream`; runtime blocks in `ConfigureAppConfiguration` until received | Host has no access to customer payload in separated compute |

---

## Out of Scope (post-preview)

See [`compute_separation_p2.md`](./compute_separation_p2.md) for detailed designs of lower-priority items.

- Scale controller integration
- `WorkerConnect` consolidated protocol message (the prototype uses a new message type 50; we use the existing init sequence for preview)
- ApplicationId-based routing
- Outbound gRPC authentication (design accommodates it; implementation deferred)
- SquashFS mounting / package download
- Extension assembly loading for non-bundle / dotnet-isolated apps (host-initiated streaming from worker)

---

## Milestone Plan

| Milestone | Description | Target |
|---|---|---|
| M1 | Extract `WorkerChannelBase` from `GrpcWorkerChannel` | ✅ Late March |
| M2 | `ConnectedWorkerChannel` + new manager + dispatcher | 🔄 Early April |
| M3 | E2E vertical slice: sidecar stub, outbound gRPC client, metadata provider, host.json via capabilities, Aspire harness | Mid April |
| M4 | Production APIs (`/admin/workers/assign`, `/admin/instance/specialize`), placeholder mode | Late April |
| M5 | Hardening: disconnection handling, scale-out, logging, health checks, integration tests | May |
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
    OutboundGrpcClient.cs                             (M3)
    WorkerConnectionService.cs                        (M3)
    WorkerConnectedEvent.cs                           (M3)
    ConnectedWorkerFunctionMetadataProvider.cs        (M3)
    HostJsonContentProvider.cs                        (M3)
    ExternalWorkerHostJsonConfigurationSource.cs      (M3)
    ExternalWorkerOptions.cs                          (M3)
    ExternalWorkerServiceCollectionExtensions.cs      (M3)
    ExternalWorkerController.cs                       (M4)
    WorkerAssignmentRequest.cs                        (M4)

tools/compute-separation-harness/
  AppHost/
    Program.cs                                        (M3 — Aspire orchestrator)
    AppHost.csproj
  Sidecar/
    Program.cs                                        (M3 — gRPC relay + HTTP proxy)
    Sidecar.csproj
  README.md                                           (M3)
```

## Modified Files

```
src/WebJobs.Script.Grpc/Channel/GrpcWorkerChannel.cs           (M1 — slim to lifecycle-only)
src/WebJobs.Script/ScriptHostBuilderExtensions.cs              (M3 — conditional config source swap)
src/WebJobs.Script.Grpc/Rpc/RpcInitializationService.cs       (M4 — skip placeholder worker spawn in external mode)
```

**No proto changes required.** host.json is delivered via worker capabilities (`host_configuration_json` key). Function metadata via existing `GetFunctionMetadata()` RPC.

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

## Sidecar Architecture

> **Note**: This section describes the **deployment infrastructure** that sits between the runtime and the worker. The runtime itself is agnostic to the sidecar — it only knows it is connecting outbound to a gRPC endpoint. See M3 for the runtime-side design.

### Pod topology

There are two pod types running on separate compute in a **1:M** relationship:

```
┌──────────────────────┐          ┌──────────────────────────────────────┐
│     Runtime Pod      │          │           Worker Pod (×M)            │
│                      │          │                                      │
│  ┌────────────────┐  │   gRPC   │  ┌──────────┐      ┌─────────────┐  │
│  │    Runtime     │──│─────────>│──│  Sidecar  │<─────│   Worker    │  │
│  │  (gRPC client) │  │          │  │(gRPC svr) │      │  (customer  │  │
│  │                │──│── HTTP ─>│──│(HTTP proxy)│─────>│    code)    │  │
│  └────────────────┘  │          │  └──────────┘      └─────────────┘  │
│                      │          │                                      │
└──────────────────────┘          └──────────────────────────────────────┘
```

- **Runtime pod**: runs the Functions host. Connects outbound to each worker pod.
- **Worker pod**: contains the sidecar + the worker process (customer code). The sidecar is the entry point that the runtime connects to.
- **1:M**: one runtime pod serves multiple worker pods (scale-out). Each `/admin/workers/assign` call adds a new worker pod connection.

### Why a sidecar?

Networking restrictions prevent the runtime from acting as a gRPC server that external workers connect to. The runtime must initiate **outbound** connections. However, existing workers expect to connect to a gRPC server implementing `FunctionRpc.EventStream`. The sidecar resolves this by acting as a relay within the worker pod:

The runtime sees only a gRPC endpoint. It does not know or care whether the other end is a sidecar, a direct worker, or any other relay topology.

### Sidecar responsibilities

1. **Worker-facing gRPC server** — implements `FunctionRpc.EventStream` (same proto). Worker connects exactly as it does today. Zero changes to worker behavior.
2. **Runtime-facing gRPC server** — implements `FunctionRpc.EventStream` on a separate port. Runtime connects as a gRPC client and sends host messages / receives worker messages.
3. **Message relay** — forwards `StreamingMessage`s between the two streams. Messages are forwarded as-is for preview; future milestones may add transformation (e.g., injecting auth tokens, filtering).
4. **HTTP proxy** — forwards HTTP requests from runtime to worker (for HTTP trigger forwarding). Required because of authentication requirements on the runtime→worker boundary.
5. **Future: authentication** — the sidecar is the trust boundary. Design accommodates this; implementation is deferred.

### Sidecar implementation

The sidecar is a standalone .NET AOT-compiled application deployed alongside the worker process in the same pod. It is intentionally minimal:

```csharp
// Program.cs — AOT entrypoint
var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(runtimePort, o => o.Protocols = HttpProtocols.Http2);  // runtime-facing
    k.ListenAnyIP(workerPort, o => o.Protocols = HttpProtocols.Http2);   // worker-facing
    k.ListenAnyIP(httpProxyPort);                                         // HTTP proxy
});
builder.Services.AddGrpc();
builder.Services.AddSingleton<FunctionRpcRelay>();
var app = builder.Build();
app.MapGrpcService<FunctionRpcRelay>();
app.MapForwarder("/{**path}", workerHttpEndpoint);  // YARP-based HTTP proxy
app.Run();
```

```csharp
// FunctionRpcRelay.cs — bidirectional gRPC relay
// Implements FunctionRpc.EventStream on BOTH ports.
// When a runtime connects, it creates a pending session.
// When a worker connects, it pairs with the pending session.
// Messages from one stream are forwarded to the other.
//
// The relay does NOT interpret messages. It forwards StreamingMessage
// payloads verbatim. This keeps it simple and version-independent.
```

### Why the same `FunctionRpc.EventStream` proto on both sides?

`StreamingMessage` is a `oneof` containing all message types (host→worker and worker→host). Either side can send any message type. By reusing the same RPC:

- **No new proto changes** needed for the relay
- Runtime's `OutboundGrpcClient` is a standard gRPC client calling `EventStream`
- The relay is transparent — it doesn't need to understand message semantics
- Future proto additions (new message types) work automatically

### Connection lifecycle

```
1. Sidecar starts → listening on runtime-port and worker-port
2. Worker starts → connects to sidecar:worker-port → calls EventStream
3. Sidecar holds worker stream, waiting for runtime
4. Runtime starts → connects to configured gRPC endpoint → calls EventStream
5. Sidecar pairs the two streams → begins bidirectional relay
6. Worker sends StartStream → relayed to runtime → triggers channel creation
7. Normal init handshake proceeds (WorkerInitRequest/Response) through relay
8. Invocations flow through relay transparently
```

---

## M3: E2E Vertical Slice

M3 consolidates what was previously M2b, M3, M3b, and parts of M4 into a single deliverable that produces a working E2E demo. No proto changes. No placeholder support. No production APIs. Just a `curl` that executes a function through the sidecar.

### What M3 delivers

```
$ curl http://localhost:7071/api/HttpTrigger?name=World
Hello World
```

With this topology running locally via Aspire:

```
Runtime Pod                         Worker Pod
+----------------+   gRPC    +------------+    +-------------+
|    Runtime     |---------->|  Sidecar   |<---|   Worker    |
|  (gRPC client) |   HTTP    |(gRPC relay)|    |  (Node.js)  |
|                |---------->|(HTTP proxy)|-->|             |
+----------------+           +------------+    +-------------+
```

### Startup flow (non-placeholder, config-driven)

```
WebHost starts hosted services (sequential, by registration order):
  1. RpcInitializationService.StartAsync()
       -> gRPC server up (for host-managed workers -- unchanged)

  2. WorkerConnectionService.StartAsync()
       -> reads FUNCTIONS_WORKER_EXTERNAL_GRPC_ENDPOINT
       -> OutboundGrpcClient.ConnectAsync()
       -> ConnectedWorkerChannel created, BeginInboundProcessing()
       -> Worker sends StartStream -> SendWorkerInitRequest()
       -> Worker sends WorkerInitResponse (with capabilities)
       -> Extract capabilities["host_configuration_json"]
         -> HostJsonContentProvider.SetContent()
       -> WorkerConnectedEvent -> channel registered in manager
       [blocks until handshake completes -- ensures host.json available]

  3. WebJobsScriptHostService.StartAsync()
       -> builds ScriptHost
       -> ConfigureAppConfiguration
            ExternalWorkerHostJsonConfigurationProvider.Load()
            -> content already available from step 2 -- no blocking
       -> ConfigureServices (with real host.json values)
       -> InitializeAsync
            ConnectedWorkerFunctionMetadataProvider.GetFunctionMetadataAsync()
            -> WaitForChannelAsync() returns immediately (channel ready from step 2)
            -> channel.GetFunctionMetadata() -- worker reports its functions
       -> Functions loaded, triggers started

  Host ready, serving traffic
```

### M3 sub-tasks

#### (a) Sidecar stub

Minimal .NET AOT project under `tools/compute-separation-harness/Sidecar/`. Three ports:

| Port | Protocol | Purpose |
|---|---|---|
| runtime-grpc | gRPC (HTTP/2) | Runtime-facing `EventStream` relay |
| worker-grpc | gRPC (HTTP/2) | Worker-facing `EventStream` relay |
| http-proxy | HTTP/1.1 | HTTP reverse proxy (runtime -> worker) |

The sidecar reads `host.json` from the worker pod filesystem and injects it into `WorkerInitResponse.capabilities` with the well-known key `host_configuration_json` before relaying to the runtime.

#### (b) OutboundGrpcClient

The mirror of `FunctionRpcService`: initiates an outbound gRPC stream to a remote endpoint. Bridges to `Channel<InboundGrpcEvent>` / `Channel<OutboundGrpcEvent>`. The runtime is agnostic to what is on the other end -- sidecar, direct worker, or any other topology.

#### (c) WorkerConnectionService

Manages outbound connections. In M3, connects on startup from `FUNCTIONS_WORKER_EXTERNAL_GRPC_ENDPOINT`. In M4, becomes API-driven via `/admin/workers/assign`.

Blocks in `StartAsync()` until `WorkerInitResponse` completes. Extracts `host_configuration_json` from capabilities and calls `HostJsonContentProvider.SetContent()`. This guarantees host.json is available before `WebJobsScriptHostService` builds the ScriptHost.

#### (d) host.json via capabilities

**No proto change.** The sidecar injects `host_configuration_json` into `WorkerInitResponse.capabilities`. `WorkerConnectionService` extracts it and feeds `HostJsonContentProvider`. `ExternalWorkerHostJsonConfigurationProvider.Load()` reads from the provider (content already available -- no blocking in the non-placeholder flow).

#### (e) ConnectedWorkerFunctionMetadataProvider

Replaces `WorkerFunctionMetadataProvider` via DI. Waits for a connected channel, then calls `channel.GetFunctionMetadata()`. No process spawn. No `function.json` on disk.

#### (f) ExternalWorkerOptions + DI wiring

```
FUNCTIONS_WORKER_EXTERNAL_ENABLED=true
FUNCTIONS_WORKER_EXTERNAL_GRPC_ENDPOINT=http://localhost:50051
```

`AddExternalWorkerSupport()` registers all external worker services and replaces `IFunctionInvocationDispatcher` and `IWorkerFunctionMetadataProvider`.

#### (g) Aspire harness

Orchestrates runtime + sidecar + worker with distributed tracing, structured logging, and automatic port wiring.

#### (h) ConnectedWorkerChannel publishes WorkerConnectedEvent

Override `WorkerInitResponse` to publish `WorkerConnectedEvent` after base call. This signals the handshake is complete and triggers channel registration.

### `FunctionRpcService.cs` -- UNMODIFIED

External workers connect via `OutboundGrpcClient`. `FunctionRpcService` serves only host-managed workers.



---

## M4: Production APIs + Placeholder Mode

Configuration, DI wiring, and `ExternalWorkerOptions` are delivered in M3. M4 adds the production API surface and placeholder mode support needed for the Azure deployment model.

---

## M4 APIs: Specialization + Worker Assignment

### Problem

In the existing model, `/admin/instance/assign` delivers app context and the host specializes and starts workers itself. In the separated compute model, three things happen that are decoupled from each other:

1. **App assignment** — infrastructure tells the runtime about the app (settings, name, etc.)
2. **Worker provisioning** — infrastructure starts worker pod(s) on separate compute
3. **Worker connection** — infrastructure tells the runtime where the worker(s) are (endpoint info)

Steps 1 and 2 happen concurrently. Step 3 happens once worker pods have IP addresses. The runtime must handle a **two-phase** flow: specialize with app context, then unblock when workers are assigned.

### Design: two separate API calls

#### API 1: `POST /admin/instance/specialize`

Delivers the app context (similar to today's `/admin/instance/assign`). The runtime begins specialization but **does not wait for a worker** — it pauses at the point where it needs worker interaction (metadata retrieval, host.json delivery).

**Request body:**

```json
{
  "assignmentContext": {
    "siteName": "my-function-app",
    "siteId": 12345,
    "environment": {
      "FUNCTIONS_WORKER_RUNTIME": "node",
      "AzureWebJobsStorage": "...",
      "CUSTOM_SETTING": "value"
    },
    "secrets": { ... },
    "corsSettings": { ... }
  }
}
```

This is intentionally close to the existing `HostAssignmentContext` to reuse the specialization machinery. The key difference: **no worker is started**. The host applies settings, reloads config, and restarts — but blocks in `ConnectedWorkerFunctionMetadataProvider.WaitForChannelAsync()` and `ExternalWorkerHostJsonConfigurationProvider.Load()` waiting for worker connections.

**Response:** `202 Accepted` (specialization in progress) or `409 Conflict` (already specialized).

**Implementation:** Can share code with `InstanceController.Assign()` and `StandbyManager.SpecializeHostCoreAsync()`. The only difference is that `_workerManager.SpecializeAsync()` is a no-op (no placeholder workers to specialize), and the host blocks during ScriptHost build waiting for workers.

#### API 2: `POST /admin/workers/assign`

Called by infrastructure when a worker pod is ready. Provides the gRPC endpoint to connect to. Can be called **multiple times** — once per worker pod assigned to this runtime.

**Request body:**

```json
{
  "workerId": "w_a1b2c3d4",
  "grpcEndpoint": "http://10.0.1.42:50051"
}
```

**Behavior:**
1. `WorkerConnectionService` creates an `OutboundGrpcClient` for this endpoint
2. Registers gRPC channels via `_eventManager.AddGrpcChannels(workerId)`
3. Creates `ConnectedWorkerChannel`, calls `StartWorkerProcessAsync()`
4. Worker's `StartStream` arrives → host.json delivered → `HostJsonContentProvider.SetContent()` **unblocks** the config pipeline (on first worker)
5. `WorkerInitRequest` / `WorkerInitResponse` handshake completes
6. `WorkerConnectedEvent` → channel registered in `ConnectedWorkerChannelManager`
7. `WaitForChannelAsync()` **unblocks** → metadata retrieved → functions loaded → triggers start

For **subsequent** worker assignments (scaling out):
- Steps 1-6 repeat — a new `OutboundGrpcClient` and `ConnectedWorkerChannel` are created
- No host restart needed — `ConnectedWorkerChannelManager.AddChannel()` adds to the pool
- `ConnectedWorkerInvocationDispatcher` picks up the new channel via round-robin
- `SetupChannel()` loads existing functions onto the new worker

**Response:** `202 Accepted` (connection in progress) or `400 Bad Request` (validation failure).

### Endpoint controller

```csharp
// New: ExternalWorkerController.cs (or add to existing InstanceController)
[Authorize(Policy = PolicyNames.AdminAuthLevel)]
[ApiController]
public class ExternalWorkerController : ControllerBase
{
    private readonly IWorkerConnectionService _connectionService;

    [HttpPost("admin/workers/assign")]
    public async Task<IActionResult> AssignWorker([FromBody] WorkerAssignmentRequest request)
    {
        if (request is null || string.IsNullOrEmpty(request.GrpcEndpoint))
        {
            return BadRequest("grpcEndpoint is required.");
        }

        string workerId = request.WorkerId ?? $"w_{Guid.NewGuid().ToString("N")[..8]}";
        var endpoint = new Uri(request.GrpcEndpoint);

        await _connectionService.ConnectWorkerAsync(workerId, endpoint);

        return Accepted(new { workerId });
    }
}
```

```csharp
public class WorkerAssignmentRequest
{
    /// <summary>Orchestrator-assigned worker ID. Generated if null.</summary>
    [JsonProperty("workerId")]
    public string WorkerId { get; set; }

    /// <summary>gRPC endpoint to connect to (e.g., "http://10.0.1.42:50051").</summary>
    [JsonProperty("grpcEndpoint")]
    public string GrpcEndpoint { get; set; }
}
```

### WorkerConnectionService changes

`WorkerConnectionService` evolves from a simple `IHostedService` that connects on startup to a service that manages **multiple** outbound connections on demand:

```csharp
internal class WorkerConnectionService : IHostedService, IWorkerConnectionService
{
    private readonly ConcurrentDictionary<string, OutboundGrpcClient> _clients = new();

    // Called by ExternalWorkerController
    public async Task ConnectWorkerAsync(string workerId, Uri endpoint, string language)
    {
        _logger.LogInformation("Connecting to worker {workerId} at {endpoint}", workerId, endpoint);

        // 1. Register gRPC channels
        _eventManager.AddGrpcChannels(workerId);

        // 2. Create and connect outbound gRPC client
        var client = new OutboundGrpcClient(_eventManager, _loggerFactory.CreateLogger<OutboundGrpcClient>());
        await client.ConnectAsync(workerId, endpoint, CancellationToken.None);
        _clients[workerId] = client;

        // 3. Create ConnectedWorkerChannel
        var workerConfig = BuildWorkerConfig(language);
        var channel = _channelFactory.Create(workerId, workerConfig);

        // 4. Start inbound processing — waits for StartStream
        await channel.StartWorkerProcessAsync(CancellationToken.None);
    }

    // StartAsync no longer initiates connections — waits for API calls
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _workerConnectedSubscription = _eventManager
            .OfType<WorkerConnectedEvent>()
            .Subscribe(OnWorkerConnected);
        return Task.CompletedTask;
    }

    // ... OnWorkerConnected, StopAsync, DisposeAsync unchanged
}
```

### Full flow timeline

```
Infrastructure                     Runtime                                Worker Pod
      |                              |                                        |
      |-- POST /admin/instance/      |                                        |
      |   specialize {appContext} --->|                                        |
      |                              |-- ApplyAppSettings()                   |
      |                              |-- RestartHostAsync()                   |
      |                              |-- ConfigureAppConfiguration            |
      |                              |     ExternalWorkerHostJsonConfig       |
      |                              |     .Load() → BLOCKS                   |
      |                              |                                        |
      |-- (start worker pod) --------|--------------------------------------->|
      |   (wait for IP)              |                                        |
      |                              |                                        |
      |-- POST /admin/workers/       |                                        |
      |   assign {workerId,          |                                        |
      |    grpcEndpoint} ----------->|                                        |
      |                              |-- OutboundGrpcClient.ConnectAsync() -->|
      |                              |-- ConnectedWorkerChannel               |
      |                              |   .StartWorkerProcessAsync()           |
      |                              |                                        |
      |                              |<-- StartStream {host_config_json} -----|
      |                              |-- SetContent(json) → UNBLOCKS Load()   |
      |                              |-- WorkerInitRequest ------------------>|
      |                              |<-- WorkerInitResponse -----------------|
      |                              |-- WorkerConnectedEvent                 |
      |                              |-- AddChannel(workerId)                 |
      |                              |-- WaitForChannelAsync() UNBLOCKS       |
      |                              |-- GetFunctionMetadata() -------------->|
      |                              |<-- metadata ---------------------------|
      |                              |-- Functions loaded, triggers started   |
      |                              |                                        |
      |   [SCALE OUT — new worker]   |                                        |
      |-- POST /admin/workers/       |                                   ┌────────────┐
      |   assign {workerId2,         |                                   │ Worker     │
      |    grpcEndpoint2} ---------->|                                   │ Pod 2      │
      |                              |-- OutboundGrpcClient #2 -------->│            │
      |                              |-- handshake, SetupChannel()      │            │
      |                              |-- round-robin now includes #2    └────────────┘
```

### Why a separate API instead of extending `/admin/instance/assign`?

1. **Temporal decoupling** — app context is available before workers are. Merging them would either delay specialization or require a partial-then-complete pattern.
2. **Multiple workers** — `/admin/workers/assign` is called per-worker. Mixing worker connection info into the app assignment model doesn't scale.
3. **Independent lifecycle** — workers can be added/removed without re-assigning the app. Future scale-in would use a `DELETE /admin/workers/{workerId}` endpoint.
4. **Clean separation of concerns** — the runtime's specialization flow stays close to today's; worker connectivity is a new capability layered on top.

### Design notes

- **Auth**: Both APIs use the existing `AdminAuthLevel` policy. Infrastructure authenticates the same way it does today for `/admin/instance/assign`.
- **`/admin/instance/specialize` vs `/admin/instance/assign`**: For preview, we can reuse the existing assign endpoint with a flag or config that changes behavior. A separate endpoint is cleaner long-term but not strictly necessary for preview.
- **`ExternalWorkerOptions`**: `GrpcEndpoint` is no longer set via config — it arrives dynamically via API. The options class keeps `IsEnabled` and `AllowedLanguages` but the endpoint is per-worker.
- **Error handling**: If the gRPC connection fails, the API returns `502 Bad Gateway`. The channel manager doesn't register the channel, and infrastructure can retry.
- **Graceful removal**: Future milestone adds `DELETE /admin/workers/{workerId}` for scale-in. `WorkerConnectionService.DisconnectWorkerAsync()` drains invocations, disposes the channel and client.

---

## M4 Placeholder: Placeholder Mode Support

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
       → gRPC server listening (host-managed workers) ✅
       → InitializeChannelsAsync() → SKIPPED (external worker mode)
T2   WorkerConnectionService.StartAsync()
       → external workers disabled during placeholder → no outbound connection
T3   WebJobsScriptHostService.StartAsync() → builds placeholder ScriptHost
       → ConfigureAppConfiguration
       → ExternalWorkerHostJsonConfigurationSource.Load()
       → placeholder mode detected → returns default config (no blocking)
T4   StandbyInitializationService → creates synthetic WarmUp function + specialization timer
T5   Placeholder ScriptHost starts with synthetic WarmUp function
T6   Warmup request arrives (/api/WarmUp)
       → PreJitPrepare(coldstart.jittrace) ✅
       → ReadRuntimeAssemblyFiles() ✅
       → WorkerWarmupAsync() → no channels → no-op ✅
T7   Host is warm, waiting for specialization

SPECIALIZATION PHASE (worker connects via outbound gRPC)
─────────────────────────────────────────────────────────
T8   Environment variables change (real app assigned, gRPC endpoint available)
T9   PlaceholderSpecializationMiddleware detects: !InStandbyMode && IsContainerReady()
T10  StandbyManager.SpecializeHostCoreAsync()
       → _configuration.Reload()
       → _hostNameProvider.Reset()
       → NotifyChange() (signals StandbyChangeTokenSource)
       → _workerManager.SpecializeAsync() → no channels → quick return
       → _scriptHostManager.RestartHostAsync("Host specialization.")
T11  Old placeholder ScriptHost torn down
T12  WorkerConnectionService detects specialization → initiates outbound gRPC connection
T13  New ScriptHost builds
       → ConfigureAppConfiguration
       → ExternalWorkerHostJsonConfigurationSource.Load()
       → NOT in placeholder mode → BLOCKS on WaitForContent()
           ↕ (meanwhile, outbound gRPC active)
T14  Worker sends StartStream { worker_id, host_configuration_json } → relayed to runtime
T15  ConnectedWorkerChannel intercepts host.json → HostJsonContentProvider.SetContent(json)
T16  Load() UNBLOCKS → real host.json applied → configuration pipeline continues
T17  ScriptHost services configured with real host.json values
T18  ScriptHost.InitializeAsync()
       → ConnectedWorkerFunctionMetadataProvider.WaitForChannelAsync()
       → channel handshake completes → metadata retrieved
T19  Functions loaded → triggers started → host fully ready
T20  _scriptHostManager.DelayUntilHostReadyAsync() returns
T21  App serving traffic
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
| `SidecarRpcService` bidirectional relay — stream already exists when worker connects | Outbound `OutboundGrpcClient` connects to remote endpoint; `FunctionRpcService` unchanged |
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
6. **Zero changes to `FunctionRpcService`** — external workers connect via `OutboundGrpcClient`; `FunctionRpcService` continues to serve only host-managed workers.
7. **`WorkerChannelBase` is a pure refactor** — all existing tests pass without modification (GrpcWorkerChannel behavior is identical).
8. **External worker mode requires explicit opt-in** — `ExternalWorkers:Enabled = true` in config; no impact on existing deployments.
9. **gRPC server startup order is unchanged** — `RpcInitializationService` starts at WebHost level before ScriptHost is built; `WorkerConnectionService` starts outbound connection at WebHost level in the same phase.
10. **Placeholder warmup runs identically** — JIT trace, assembly page-in, and synthetic WarmUp function are unchanged. Worker-dependent warmup steps are already graceful no-ops when no channels exist.
11. **Zero changes to worker protocol** — Workers connect to their configured endpoint using the same `FunctionRpc.EventStream` RPC. The transport topology is transparent to the worker.
12. **Runtime is transport-agnostic** — It connects outbound to a configured gRPC endpoint via `OutboundGrpcClient`. It does not know or care whether the other end is a sidecar relay, a direct worker, or any other topology.
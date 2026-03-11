# Worker Model — Object Model & Runtime Flow

This document describes the object model used in the new `Azure.Functions.WorkerModel` and traces
the end-to-end flow from the first gRPC connection through to creating a running JobHost with
workers dispatching invocations.

---

## 1. Key Types

### Record / Value Types

| Type | Location | Purpose |
|------|----------|---------|
| `ApplicationDefinition` | `JobHost/` | Identifies an app: `(ApplicationId, ApplicationVersion)` |
| `WorkerDefinition` | `Workers/` | Immutable description of a worker: `(WorkerId, ApplicationDefinition, Capabilities, WorkerStack)` |
| `WorkerStack` | `Workers/` | Runtime environment: `(Runtime, Version, Architecture, IsPlaceholder)` |
| `WorkerCreationContext` | `Workers/` | Wrapper around `WorkerDefinition` passed into factory methods |
| `WorkerState` | `Workers/` | Mutable state holder around a `WorkerDefinition` + `WorkerStatus`. Can `Specialize()` to produce a new state with updated app and capabilities. |
| `WorkerStatus` | `Workers/` | Enum: `Created → Initializing → Initialized → Running → Draining → Drained → Stopping → Stopped` |
| `StreamState` | `Grpc/` | Enum tracking the gRPC stream lifecycle: `None → Connected → Initialized → Running` (and `Specializing`, `RunningAsPlaceholder`, etc.) |

### Interfaces

| Interface | Scope | Purpose |
|-----------|-------|---------|
| `IWorker` | JobHost | Represents a worker that can invoke functions, return metadata, and drain. Extends `IWorkerState`. |
| `IWorkerState` | JobHost | Read-only view: `Definition`, `Status`, `Channel` |
| `IWorkerChannel` | JobHost | Internal side of the bidirectional channel: `HostMessageReader`, `WorkerMessageWriter` |
| `IExternalWorkerChannel` | JobHost | External (gRPC) side: `WorkerMessageReader`, `HostMessageWriter` |
| `IWorkerManager` | JobHost-scoped | Creates and removes workers. Retrieved by the gRPC layer when a new connection is made. |
| `IWorkerFactory` | JobHost-scoped | Creates `IWorker` instances from a `WorkerCreationContext` |
| `IWorkerResolver` | JobHost-scoped | Selects a worker for a given invocation (round-robin in `DefaultWorkerResolver`) |
| `IJobHostManager` | Singleton | Manages `JobHost` instances keyed by `ApplicationDefinition`. Creates/removes/looks up JobHosts. |

### Concrete Classes

| Class | Purpose |
|-------|---------|
| `FunctionsService` | gRPC service (`FunctionRpc.FunctionRpcBase`). Creates a `GrpcWorkerStream` per connection. |
| `GrpcWorkerStreamFactory` | Creates `GrpcWorkerStream` instances with injected `IJobHostManager` and options. |
| `GrpcWorkerStream` | **The central orchestrator** for a single gRPC connection. Owns the `BidirectionalChannel`, handles the message loop, creates the `WorkerDefinition`, starts the `JobHost`, and manages state transitions. |
| `GrpcWorker` | `IWorker` implementation. Owns its own `BidirectionalChannel` to the stream, processes invocations, handles function load, manages capabilities. |
| `GrpcWorkerFactory` | Creates `GrpcWorker` instances. |
| `BidirectionalChannel` | Two `System.Threading.Channels` — one for Host→Worker messages, one for Worker→Host messages. Implements both `IWorkerChannel` and `IExternalWorkerChannel`. |
| `DefaultWorkerManager` | `IWorkerManager` implementation. Holds a list of `IWorker` instances, delegates creation to `IWorkerFactory`. |
| `DefaultWorkerResolver` | Round-robin worker selection for invocations. |
| `JobHost` | Wraps an `IHost`, provides `CreateWorkerAsync()` and `StartAsync()`. Scoped per `ApplicationDefinition`. |
| `JobHostManager` | Singleton. `ConcurrentDictionary<ApplicationDefinition, JobHost>`. Uses `IScriptHostBuilderEx` to build new host instances. |
| `MessageHandlerPipeline` | Dispatches `MessageFromWorker` to registered `IMessageHandler` implementations. |

---

## 2. Object Relationships

```
FunctionsService (gRPC server, singleton)
  └── GrpcWorkerStreamFactory (singleton)
        └── creates GrpcWorkerStream (per gRPC connection)
              ├── BidirectionalChannel (stream ↔ worker message transport)
              ├── WorkerState / WorkerDefinition (built from WorkerConnect or WorkerInitResponse)
              └── IJobHostManager (singleton, shared)
                    └── JobHost (per ApplicationDefinition)
                          ├── IWorkerManager (JobHost-scoped)
                          │     └── IWorker[] (GrpcWorker instances)
                          │           └── BidirectionalChannel (worker ↔ JobHost messages)
                          ├── IWorkerResolver (picks worker for invocations)
                          ├── IWorkerFactory (GrpcWorkerFactory)
                          └── MessageHandlerPipeline
```

---

## 3. Channel Architecture

Each gRPC connection has **two levels of channels**:

1. **Stream-level channel** (`GrpcWorkerStream._channel`): Bridges the raw gRPC `EventStream`
   to typed `MessageToWorker` / `MessageFromWorker` objects. This is the transport layer.

2. **Worker-level channel** (`GrpcWorker._channel`): Bridges the `GrpcWorkerStream` to the
   `JobHost`. The stream reads from this channel and writes to gRPC; the JobHost writes to
   this channel and reads responses.

```
gRPC EventStream
    ↕ (protobuf StreamingMessage)
GrpcWorkerStream._channel (BidirectionalChannel)
    ↕ (MessageToWorker / MessageFromWorker)
    ├── reads from gRPC, dispatches to worker channel or handles directly
    └── reads from worker channel, writes to gRPC

GrpcWorker._channel (BidirectionalChannel)
    ↕ (MessageToWorker / MessageFromWorker)
    ├── HostMessageReader — GrpcWorker reads this for FunctionLoadResponse, InvocationResponse, etc.
    └── WorkerMessageWriter — GrpcWorkerStream writes FunctionLoadRequest, InvocationRequest, etc.
```

---

## 4. End-to-End Flow: First gRPC Connection → Running JobHost

### Phase 1: gRPC Connection Established

```
FunctionsNetHost (in Worker Pod)
    → connects to Runtime gRPC server (via Sidecar relay)
    → FunctionsService.EventStream() is called
    → GrpcWorkerStreamFactory.CreateStream() → new GrpcWorkerStream
    → GrpcWorkerStream.StartAsync() begins:
        - starts ReadStreamAsync() to process incoming messages
        - yields outgoing messages from _channel.WorkerMessageReader
```

### Phase 2a: Traditional Path (StartStream + WorkerInitResponse)

Used when FunctionsNetHost connects directly (not through Sidecar relay):

```
1. Worker sends StartStream
   → HandleStartStream()
   → StreamState: None → Connected
   → Sends WorkerInitRequest to worker (host version, app directory)

2. Worker sends WorkerInitResponse
   → HandleWorkerInitResponse()
   → Extracts: RuntimeName, RuntimeVersion, WorkerBitness, Capabilities
   → Reads ApplicationId/ApplicationVersion from CustomProperties
   → Creates: WorkerStack → WorkerDefinition → WorkerState
   → If placeholder: stores as _placeholderWorkerState
   → If specialized: stores as _workerState
   → StreamState: Connected → Connected (unchanged)
   → Fires: StartNewJobHostAsync() (see Phase 3)

3. Worker sends FunctionMetadataResponse
   → Forwarded to worker channel
   → StreamState: Connected → Initialized
```

### Phase 2b: WorkerConnect Path (Sidecar relay)

Used when the Worker Sidecar has already completed init and sends a single
`WorkerConnect` message containing all metadata:

```
1. Sidecar sends WorkerConnect
   → HandleWorkerConnect()
   → StreamState: None → Initialized
   → Extracts from WorkerConnect:
     - WorkerMetadata: RuntimeName, RuntimeVersion, WorkerBitness
     - WorkerCapabilities: HttpUri, etc.
     - CustomProperties: ApplicationId, ApplicationVersion
     - FunctionMetadata: all function definitions + bindings
   → Creates: WorkerStack(runtime, version, arch, isPlaceholder=false)
   → Creates: ApplicationDefinition(appId, appVersion)
   → Creates: WorkerDefinition(workerId, appDef, capabilities, stack)
   → Creates: WorkerState(workerDef)
   → Fires: StartJobHostWithMetadataAsync() (see Phase 3)
```

### Phase 3: JobHost Creation

```
StartJobHostWithMetadataAsync(workerDef, workerConnect):
│
├── 1. CreateMetadata(workerConnect.FunctionMetadata)
│      Converts RpcFunctionMetadata → FunctionMetadata
│      Validates bindings, trigger presence, function names
│      Sets FunctionId from protobuf (ensures consistency)
│
├── 2. StartNewJobHostAsync(workerDef, jobHostManager, metadata)
│      │
│      ├── a. _metadataProvider.SetMetadata(metadata)
│      │      Stores metadata for the JobHost to discover
│      │
│      ├── b. GetOrCreateJobHostAsync(jobHostManager, appDef, metadata)
│      │      JobHostManager.GetOrAddJobHostAsync():
│      │        - Checks ConcurrentDictionary<ApplicationDefinition, JobHost>
│      │        - If not found: IScriptHostBuilderEx.BuildHost() → new JobHost
│      │        - Registers IWorkerFactory (GrpcWorkerFactory) in JobHost DI
│      │        - Returns JobHost
│      │
│      ├── c. jobHost.CreateWorkerAsync(WorkerCreationContext(workerDef))
│      │      → IWorkerManager.CreateWorkerAsync()
│      │      → IWorkerFactory.Create() → new GrpcWorker(workerDef, ...)
│      │      → GrpcWorker starts its read loop (Status → Running)
│      │      → Worker added to IWorkerManager._workers list
│      │      → Returns IWorkerState (the GrpcWorker)
│      │
│      ├── d. ReadJobHostStreamAsync(_worker.Channel.WorkerMessageReader)
│      │      Bridges: JobHost writes to worker channel → GrpcWorkerStream
│      │      forwards to gRPC stream → reaches FunctionsNetHost
│      │
│      └── e. jobHost.StartAsync()
│             Starts the IHost (triggers, listeners, HTTP route registration)
│
├── 3. GrpcWorker.LoadFunctionsFromMetadata(metadata)
│      Uses the SAME FunctionMetadata objects as the JobHost
│      → FunctionId is consistent between load requests and invocations
│      → Sends FunctionLoadRequest for each function to the worker
│
└── 4. SendWorkerConnectResponse(Success)
       Signals Sidecar that Runtime is ready
       → Sidecar signals RuntimeReady → /assign returns 200
       → ScaleController can now forward HTTP traffic
```

### Phase 4: Invocation Dispatch

```
HTTP request arrives at Runtime
    → WebHost routes to function
    → WorkerModelFunctionInvoker.InvokeAsync()
    → IWorkerResolver.ResolveWorker(applicationId)
       (DefaultWorkerResolver: round-robin across Running workers)
    → IWorker.InvokeAsync(ScriptInvocationContext)
    → GrpcWorker writes InvocationRequest to its channel
    → GrpcWorkerStream reads from worker channel, writes to gRPC
    → FunctionsNetHost processes invocation
    → Sends InvocationResponse back through gRPC
    → GrpcWorkerStream dispatches to GrpcWorker via channel
    → GrpcWorker completes the invocation TaskCompletionSource
    → Result returned to WebHost
```

---

## 5. Specialization Flow

When a placeholder worker is specialized (e.g., after `/assign`):

```
1. TrySpecializeAsync(applicationId, applicationVersion, runtimeEnvironmentToMatch)
   → Validates: must be placeholder, stack must match
   → StreamState → Specializing
   → Removes current placeholder JobHost: jobHostManager.RemoveJobHostAsync()
   → Sends FunctionEnvironmentReloadRequest to worker
     (env vars, app path — worker reloads its runtime)

2. Worker responds with FunctionEnvironmentReloadResponse
   → HandleEnvironmentReloadResponseAsync()
   → StreamState: Specializing → Connected
   → Creates new WorkerState via _placeholderWorkerState.Specialize(app, capabilities)
   → Calls StartNewJobHostAsync() with new definition
   → New JobHost created for the specialized ApplicationDefinition
   → Worker is now running customer code
```

---

## 6. State Machine

The `StreamState` transitions are enforced by the `ChangeState()` method:

```
None ──StartStream──→ Connected
None ──WorkerConnect──→ Initialized

Connected ──WorkerInitResponse──→ Connected
Connected ──MetadataResponse──→ Initialized
Connected ──Specialize (placeholder)──→ Specializing
Connected ──InvocationResponse──→ Running

Initialized ──InvocationResponse (placeholder)──→ RunningAsPlaceholder
Initialized ──InvocationResponse (specialized)──→ Running

RunningAsPlaceholder ──Specialize──→ Specializing
RunningAsPlaceholder ──InvocationResponse──→ RunningAsPlaceholder

Specializing ──EnvironmentReloadResponse──→ Connected

Running ──InvocationResponse──→ Running
Running ──Specialize──→ Specializing
```

---

## 7. Key Design Decisions

### Single metadata source
`WorkerConnect.FunctionMetadata` is the **single source of truth**. The same `FunctionMetadata`
objects are used by the JobHost for route registration/invocation and by GrpcWorker for
`FunctionLoadRequest`. This ensures `FunctionId` consistency.

### JobHost per ApplicationDefinition
Each unique `(ApplicationId, ApplicationVersion)` gets its own `JobHost` with isolated DI,
listeners, and worker pool. This supports multi-tenant scenarios where one Runtime serves
multiple applications.

### Worker pool per JobHost
Each `JobHost` has its own `IWorkerManager` with a list of `IWorker` instances. Multiple
gRPC connections (from different Worker Pods) for the same application all add workers to
the same JobHost's pool. `DefaultWorkerResolver` round-robins across them.

### Channel-based message flow
All communication uses `System.Threading.Channels` for backpressure and async coordination.
No direct method calls between the gRPC layer and the JobHost — everything flows through
typed channel messages (`MessageToWorker`, `MessageFromWorker`).

### No process management
The Runtime has **zero responsibility** for worker process lifecycle. Workers are PID 1 in
their own containers. The gRPC connection is the only coupling point — if it drops, the
worker is removed from the pool. No start, stop, restart, or kill operations.

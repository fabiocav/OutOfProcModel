# Worker Sidecar gRPC Lifecycle

This document describes the full lifecycle of gRPC connections from the Worker Sidecar's perspective, including its connections to both the **Worker** (FunctionsNetHost) and the **Runtime** (WebHost).

## Connection Overview

The Sidecar maintains **two** gRPC connections:

| Connection | Direction | When Created | Protocol |
|---|---|---|---|
| **Worker → Sidecar** | Worker calls `EventStream` on Sidecar | At process start (placeholder mode) | Sidecar is the gRPC **server** |
| **Sidecar → Runtime** | Sidecar calls `EventStream` on Runtime | After `/assign` and worker reload | Sidecar is the gRPC **client** |

The Worker↔Sidecar stream is **one persistent bidi stream** that survives across all phases. The Sidecar↔Runtime stream is created only after specialization.

---

## Phase 1: Placeholder Warm-Up

**No Runtime connection exists.** The Sidecar acts as the host and drives the init handshake with the worker.

**Code:** `SidecarRpcService.HandlePlaceholderModeAsync`

```
  FunctionsNetHost (Worker)            Sidecar
  ═════════════════════════        ═══════════════
         │                               │
         │──── EventStream (gRPC bidi) ──▶│  Worker opens bidi stream
         │                               │  Sidecar stores responseStream in WorkerState
         │                               │
    ┌────────────────────────────────────────────────────────────┐
    │  Step 1                                                    │
    │    Worker ── StartStream { WorkerId } ──▶ Sidecar          │
    ├────────────────────────────────────────────────────────────┤
    │  Step 2                                                    │
    │    Worker ◀── WorkerInitRequest ──────── Sidecar           │
    │               { HostVersion: "4.0.0-prototype",            │
    │                 FunctionAppDirectory, WorkerDirectory,      │
    │                 Capabilities: [RawHttpBodyBytes,            │
    │                   RpcHttpTriggerMetadataRemoved, ...] }     │
    ├────────────────────────────────────────────────────────────┤
    │  Step 3                                                    │
    │    Worker ── WorkerInitResponse ────────▶ Sidecar          │
    │              { WorkerVersion, Capabilities, Result }       │
    ├────────────────────────────────────────────────────────────┤
    │  Step 4                                                    │
    │    Worker ◀── FunctionsMetadataRequest ─ Sidecar           │
    │               { FunctionAppDirectory }                     │
    ├────────────────────────────────────────────────────────────┤
    │  Step 5                                                    │
    │    Worker ── FunctionMetadataResponse ──▶ Sidecar          │
    │              { UseDefaultMetadataIndexing, Results[] }     │
    └────────────────────────────────────────────────────────────┘
         │                               │
         │    ~~~ IDLE (warm) ~~~         │  WorkerState.SetPlaceholderReady()
         │                               │
         │── Heartbeat ─────────────────▶│  (dropped)
         │── WorkerStatusRequest ───────▶│  → responds with WorkerStatusResponse
         │                               │
```

**After this phase:**
- Worker process is warm (JIT'd, assemblies loaded)
- No app code loaded yet
- Sidecar has the worker's response stream stored for later use
- Sidecar has captured and stored in `WorkerState`:
  - `WorkerCapabilities` (from WorkerInitResponse)
  - `WorkerMetadata` (from WorkerInitResponse)
  - `FunctionMetadata` (from FunctionMetadataResponse)
  - `UseDefaultMetadataIndexing` (from FunctionMetadataResponse)
- `WorkerState.IsPlaceholderReady == true`

---

## Phase 2: Specialization

**Triggered by:** `POST /assign` from Scale Controller → `SpecializationService.SpecializeAsync`

This phase runs on the **HTTP request thread** (not the gRPC stream thread). It coordinates with the gRPC stream via `TaskCompletionSource` signals.

```
  ScaleController         SpecializationService              Worker (same stream)
  ═══════════════         ══════════════════════              ════════════════════
         │                         │                               │
         │── POST /assign ────────▶│                               │
         │   { HostAssignmentContext,                               │
         │     RuntimeEndpoint }   │                               │
         │                         │                               │
         │                    1. MountAppPackage (stub)             │
         │                    2. WorkerState.Specialize()           │
         │                         │                               │
         │                    3. Send reload directly to worker     │
         │                         │── FunctionEnvironmentReload ─▶│
         │                         │   Request { FunctionAppDir,   │
         │                         │     EnvironmentVariables }    │
         │                         │                               │
         │                         │   (blocks on WaitForReload)   │
         │                         │                               │
```

**Meanwhile on the gRPC stream thread** (`SidecarRpcService`):

```
  Worker                  SidecarRpcService (gRPC thread)    SpecializationService
  ══════                  ═══════════════════════════════     ══════════════════════
         │                         │                               │
         │── FunctionEnvironment ─▶│                               │
         │   ReloadResponse        │                               │
         │                         │  CompleteReloadResponse()     │
         │                         │───────────────────────────────▶ (unblocks)
         │                         │                               │
         │                         │  WaitForSpecialization        │
         │                         │  CompleteAsync() ...          │
         │                         │             (blocks)    4. ConnectAsync(endpoint)
         │                         │                         5. SignalSpecializationComplete()
         │                         │◀──────────────────────────────│ (unblocks)
         │                         │                               │
```

**Key coordination:**
- `SpecializationService` sends reload to worker, then **blocks** waiting for reload response
- `SidecarRpcService` reads the response from the gRPC stream, signals the TCS
- `SpecializationService` unblocks, connects to Runtime, signals completion
- `SidecarRpcService` unblocks and transitions to Phase 3

---

## Phase 3: Transition to Relay Mode

**Code:** `SidecarRpcService` — immediately after `WaitForSpecializationCompleteAsync` returns

```
  Worker                        Sidecar                        Runtime
  ══════                    ═══════════════                    ═══════
                                   │                              │
                                   │── WorkerConnect ────────────▶│
                                   │   { WorkerId, Language,      │
                                   │     WorkerCapabilities,      │
                                   │     WorkerMetadata,          │
                                   │     FunctionMetadata[],      │
                                   │     ApplicationId,           │
                                   │     CodeVersion, ScriptRoot }│
                                   │                              │
                                   │◀── WorkerConnectResponse ───│
                                   │   { Result, HostCapabilities,│
                                   │     FunctionLoadRequests[] } │
                                   │                              │
                              HandleSpecializedModeAsync()        │
                              ├─ Task: RelayWorkerToRuntime       │
                              └─ Task: RelayRuntimeToWorker       │
                                   │                              │
```

The Sidecar sends `WorkerConnect` to the Runtime — a **single message** that replaces the old multi-step handshake (StartStream → WorkerInitRequest/Response → FunctionsMetadataRequest/Response). By this point the Sidecar already has all the data from placeholder warm-up and specialization.

The worker does **not** send its own StartStream — it was already consumed in Phase 1.

> **Current state:** The Runtime still expects the old `StartStream` message via `FunctionRpc`. The Sidecar sends `StartStream` for compatibility today, but logs the full `WorkerConnect` payload. When the Runtime is updated to handle `WorkerConnect`, this will switch over.

---

## Phase 4: Steady-State Relay

**Code:** `SidecarRpcService.HandleSpecializedModeAsync` — two parallel tasks

```
  Worker                        Sidecar                        Runtime
  ══════                    ═══════════════                    ═══════
         │                         │                              │
         │── any message ─────────▶│──────────────────────────────▶│
         │   (StartStream dropped) │  RelayWorkerToRuntimeAsync   │
         │                         │                              │
         │◀────────────────────────│◀── any message ──────────────│
         │  RelayRuntimeToWorkerAsync  (WorkerInitReq, FunctionLoad,
         │                         │    InvocationRequest, etc.)  │
         │                         │                              │
```

Messages flow through `RuntimeConnectionManager` channels:
- **Worker → Runtime:** `workerStream.ReadAllAsync` → `_runtimeConnection.SendToRuntimeAsync` → `_toRuntimeChannel` → `_runtimeStream.RequestStream.WriteAsync`
- **Runtime → Worker:** `_runtimeStream.ResponseStream.ReadAllAsync` → `_fromRuntimeChannel` → `workerStream.WriteAsync`

### Message filtering in relay mode

| Message | Behavior |
|---|---|
| `StartStream` from worker | **Dropped** (Sidecar already sent one in Phase 3) |
| All other worker messages | Relayed to Runtime as-is |
| All Runtime messages | Relayed to worker as-is |

---

## Thread Model

```
  ┌─────────────────────────────────────────────────────────────┐
  │  gRPC Stream Thread (SidecarRpcService.EventStream)         │
  │  ├─ HandlePlaceholderModeAsync (Phase 1)                    │
  │  │   └─ requestStream.MoveNext loop                         │
  │  ├─ reads FunctionEnvironmentReloadResponse (Phase 2)       │
  │  │   └─ CompleteReloadResponse → WaitForSpecializationComplete
  │  └─ HandleSpecializedModeAsync (Phase 3-4)                  │
  │      ├─ RelayWorkerToRuntimeAsync (reads worker stream)     │
  │      └─ RelayRuntimeToWorkerAsync (reads runtime channel)   │
  └─────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────┐
  │  HTTP Request Thread (POST /assign)                         │
  │  └─ SpecializationService.SpecializeAsync                   │
  │      ├─ SendToWorkerAsync (writes to stored responseStream) │
  │      ├─ WaitForReloadResponseAsync (TCS, blocks)            │
  │      ├─ RuntimeConnectionManager.ConnectAsync               │
  │      └─ SignalSpecializationComplete (TCS)                  │
  └─────────────────────────────────────────────────────────────┘

  ┌─────────────────────────────────────────────────────────────┐
  │  RuntimeConnectionManager background tasks                  │
  │  ├─ RelayToRuntimeAsync (_toRuntimeChannel → gRPC stream)  │
  │  └─ RelayFromRuntimeAsync (gRPC stream → _fromRuntimeChannel)
  └─────────────────────────────────────────────────────────────┘
```

---

## Synchronization Primitives

| Primitive | Location | Purpose |
|---|---|---|
| `_pendingReloadResponse` (TCS) | `WorkerState` | SpecializationService waits for worker to respond to reload |
| `_specializationComplete` (TCS) | `WorkerState` | SidecarRpcService waits for Runtime connection to be established |
| `_workerResponseStream` | `WorkerState` | Stored gRPC stream for SpecializationService to inject messages |
| `_toRuntimeChannel` (Channel) | `RuntimeConnectionManager` | Async queue for messages going to Runtime |
| `_fromRuntimeChannel` (Channel) | `RuntimeConnectionManager` | Async queue for messages coming from Runtime |
| `_connectionLock` (SemaphoreSlim) | `RuntimeConnectionManager` | Prevents concurrent ConnectAsync calls |

---

## Key Design Decisions

1. **Single worker stream across phases.** The original `EventStream` call from FunctionsNetHost persists through placeholder → specialization → relay. No reconnection needed.

2. **WorkerConnect replaces multi-step handshake.** Instead of 4+ messages (StartStream → WorkerInitReq/Resp → FunctionMetadataReq/Resp), the Sidecar sends a single `WorkerConnect` containing everything the Runtime needs.

3. **Reload goes to worker first, then Runtime connects.** The worker must reload env vars and app code before the Runtime starts sending it work. This avoids a race where Runtime sends invocations before the worker is ready.

4. **Two-thread coordination via TCS.** The gRPC stream thread and HTTP request thread coordinate through `TaskCompletionSource` signals — no shared mutable state, no polling.

---

## WorkerConnect Message

Replaces the old multi-step handshake with a single message. Defined in `WorkerModel.Contracts/Protos/sidecar_rpc.proto`.

### What it replaces

| Old message | Direction | Data carried | Where it goes in WorkerConnect |
|---|---|---|---|
| `StartStream` | Worker → Runtime | `worker_id` | `worker_id` |
| `WorkerInitResponse` | Worker → Host | `capabilities`, `worker_metadata` | `worker_capabilities`, `worker_metadata` |
| `FunctionMetadataResponse` | Worker → Host | `function_metadata_results[]`, `use_default_metadata_indexing` | `function_metadata[]`, `use_default_metadata_indexing` |
| *(specialization context)* | Scale Controller → Sidecar | site name, code version, script root | `application_id`, `code_version`, `script_root` |

### Proto definition

```protobuf
message WorkerConnect {
  // Worker identity
  string worker_id = 1;
  string language = 2;
  string language_version = 3;

  // Worker capabilities (from WorkerInitResponse during placeholder)
  map<string, string> worker_capabilities = 4;
  WorkerMetadata worker_metadata = 5;

  // Function metadata (from FunctionMetadataResponse during placeholder)
  repeated RpcFunctionMetadata function_metadata = 6;
  bool use_default_metadata_indexing = 7;

  // Application context (from specialization /assign)
  string application_id = 8;
  string code_version = 9;
  string script_root = 10;
}

message WorkerConnectResponse {
  StatusResult result = 1;
  map<string, string> host_capabilities = 2;
  repeated FunctionLoadRequest function_load_requests = 3;
}
```

### Where each field is populated

| Field | Source | When |
|---|---|---|
| `worker_id` | `WorkerState.Context.WorkerId` | Process start |
| `language` | `FUNCTIONS_WORKER_RUNTIME` env var | Process start |
| `language_version` | `FUNCTIONS_WORKER_RUNTIME_VERSION` env var | Process start |
| `worker_capabilities` | `WorkerInitResponse.Capabilities` | Phase 1 step 3 |
| `worker_metadata` | `WorkerInitResponse.WorkerMetadata` | Phase 1 step 3 |
| `function_metadata` | `FunctionMetadataResponse.FunctionMetadataResults` | Phase 1 step 5 |
| `use_default_metadata_indexing` | `FunctionMetadataResponse.UseDefaultMetadataIndexing` | Phase 1 step 5 |
| `application_id` | `HostAssignmentContext.SiteName` | Phase 2 (via /assign) |
| `code_version` | `HostAssignmentContext.Environment["CODE_VERSION"]` | Phase 2 (via /assign) |
| `script_root` | `HostAssignmentContext.Environment["AzureWebJobsScriptRoot"]` | Phase 2 (via /assign) |

### What changes on the Runtime side (TODO)

The Runtime currently expects the old FunctionRpc handshake. To support `WorkerConnect`:

1. Add `WorkerConnect` / `WorkerConnectResponse` to the canonical `FunctionRpc.proto` StreamingMessage oneof
2. Runtime's `WorkerChannel` / `GrpcWorkerChannel` recognizes `WorkerConnect` as the first message
3. Runtime skips the init handshake (WorkerInitRequest/Response + FunctionsMetadataRequest/Response)
4. Runtime immediately processes function metadata from the message and responds with `WorkerConnectResponse`
5. After that, normal relay traffic flows (invocations, logs, etc.)

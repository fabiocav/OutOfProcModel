# Worker Model Prototype - Implementation Plan

This document outlines the plan to build a working prototype of the new Worker Model architecture using the Azure Functions Host repo as the foundation.

---

## Overview

### Goal

Build a minimal but functional prototype demonstrating:
- Placeholder Runtime and Workers running independently (not connected)
- **Late-binding**: SC matches Runtime + Worker only at specialization time
- Specialization triggered on demand
- Sidecar-mediated gRPC communication (after connection)
- Worker process lifecycle management via Wrapper
- Basic invocation flow (HTTP trigger)

### Non-Goals (for this prototype)

- Authentication/authorization (no token validation)
- Multi-tenancy (single Function App)
- Production-ready error handling
- Real Scale Controller integration

### Components

| Component | Project | Framework | Notes |
|-----------|---------|-----------|-------|
| **Runtime** | Existing `WebJobs.Script.WebHost` | .NET 8 | Added to solution as reference |
| **Runtime Sidecar** | New `WorkerModel.RuntimeSidecar` | .NET 8 | SquashFS mounting for Runtime pod |
| **Customer App** | New `SampleApp.Functions` | .NET 8 isolated | Standard HTTP trigger |
| **Worker Sidecar** | New `WorkerModel.Sidecar` | .NET 8 | gRPC proxy for Worker pod |
| **Wrapper** | New `WorkerModel.Wrapper` | .NET 8 (AOT) | Process supervisor |
| **Scale Controller Mock** | New `WorkerModel.ScaleController` | .NET 8 | Heavily mocked |
| **Aspire AppHost** | New `WorkerModel.AppHost` | .NET 8 | Local orchestration |

---

## Project Structure

```
azure-functions-host/
├── src/
│   └── WebJobs.Script.WebHost/          # Existing - Runtime (referenced in solution)
│
├── prototype/                            # New prototype folder
│   ├── WorkerModel.RuntimeSidecar/      # Runtime sidecar - SquashFS mounting
│   ├── WorkerModel.Sidecar/             # Worker sidecar - gRPC proxy
│   ├── WorkerModel.Wrapper/             # Worker process manager (AOT)
│   ├── WorkerModel.ScaleController/     # Mock SC infrastructure
│   ├── WorkerModel.Contracts/           # Shared gRPC protos and types
│   ├── SampleApp.Functions/             # Sample customer function app
│   ├── WorkerModel.AppHost/             # Aspire orchestration
│   ├── WorkerModel.ServiceDefaults/     # Aspire service defaults
│   └── docker/                          # Dockerfiles (for container mode)
│       ├── Dockerfile.runtime
│       ├── Dockerfile.runtimesidecar
│       ├── Dockerfile.worker
│       └── Dockerfile.scalecontroller
```

---

## Phase 1: Project Setup

### 1.1 Create Solution Structure

```powershell
# Create prototype directory
mkdir azure-functions-host/prototype
cd azure-functions-host/prototype

# Create solution
dotnet new sln -n WorkerModelPrototype

# Create projects
dotnet new classlib -n WorkerModel.Contracts -f net8.0
dotnet new webapi -n WorkerModel.RuntimeSidecar -f net8.0   # Runtime pod sidecar
dotnet new grpc -n WorkerModel.Sidecar -f net8.0            # Worker pod sidecar
dotnet new console -n WorkerModel.Wrapper -f net8.0
dotnet new webapi -n WorkerModel.ScaleController -f net8.0
dotnet new func -n SampleApp.Functions --worker-runtime dotnet-isolated

# Add to solution
dotnet sln add WorkerModel.Contracts
dotnet sln add WorkerModel.RuntimeSidecar
dotnet sln add WorkerModel.Sidecar
dotnet sln add WorkerModel.Wrapper
dotnet sln add WorkerModel.ScaleController
dotnet sln add SampleApp.Functions

# Add WebHost (Runtime) and its dependencies to the solution for reference/debugging
dotnet sln add ../src/WebJobs.Script.WebHost/WebJobs.Script.WebHost.csproj
dotnet sln add ../src/WebJobs.Script/WebJobs.Script.csproj
dotnet sln add ../src/WebJobs.Script.Grpc/WebJobs.Script.Grpc.csproj
```

### 1.2 Configure AOT for Wrapper

```xml
<!-- WorkerModel.Wrapper.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StripSymbols>true</StripSymbols>
  </PropertyGroup>
</Project>
```

---

## Phase 2: Contracts & Protos

### 2.1 Define gRPC Contracts

Create shared proto files in `WorkerModel.Contracts`:

**worker_sidecar.proto** - Sidecar ↔ Runtime communication:
- Extends existing `FunctionRpc.proto`
- Adds `WorkerContext` to messages

**wrapper_control.proto** - Sidecar ↔ Wrapper communication:
- `WorkerStatusRequest/Response` - health check
- `ShutdownRequest` - graceful shutdown
- `RestartWorkerRequest` - for failure recovery (not specialization)

> **Note**: SC communication with Runtime and Worker is via **HTTP REST**, not gRPC.
> - SC → Runtime: `POST /admin/instance/assign` (existing endpoint)
> - SC → Worker Sidecar: `POST /assign` (new HTTP endpoint)
> - Runtime → SC: `POST /api/runtimes/register`
> - Worker → SC: `POST /api/workers/register`

### 2.2 Shared Types

```csharp
// WorkerModel.Contracts/ApplicationDefinition.cs
public record ApplicationDefinition(
    string ApplicationId,
    string MetadataVersion,
    string CodeVersion,
    string ScriptRoot);       // Path to function app

// WorkerModel.Contracts/WorkerContext.cs
public record WorkerContext(
    ApplicationDefinition? Application,  // null = placeholder
    string WorkerId,
    string Language,
    string LanguageVersion,
    bool IsPlaceholder);                  // True until specialized
```

---

## Phase 3: Worker Sidecar Implementation

> **Note**: This is the **Worker Sidecar** that runs in the Worker pod. There is also a **Runtime Sidecar** (Phase 3.5) that runs in the Runtime pod. The naming convention is:
> - `WorkerModel.Sidecar` = Worker Sidecar (gRPC proxy + app mounting)
> - `WorkerModel.RuntimeSidecar` = Runtime Sidecar (app mounting only)

### 3.1 Core Responsibilities

| Responsibility | Implementation |
|----------------|----------------|
| gRPC proxy | Bidirectional stream relay |
| Context injection | Add WorkerContext to StartStream |
| **`/assign` endpoint** | Receive `HostAssignmentContext` from SC, like Runtime |
| App mount | Download zip, mount via SquashFS (like production) |
| Specialization trigger | Send `FunctionEnvironmentReloadRequest` to worker via gRPC |
| Health endpoint | HTTP /health for k8s probes |

### 3.2 Key Classes

```
WorkerModel.Sidecar/
├── Program.cs
├── Controllers/
│   ├── AssignController.cs         # POST /assign (HTTP from SC)
│   └── HealthController.cs         # GET /health (HTTP for probes)
├── Services/
│   ├── GrpcProxyService.cs         # gRPC proxy to Runtime (only after /assign)
│   ├── RunFromPackageHandler.cs    # Download zip from blob
│   ├── SquashFsMounter.cs          # Mount zip as SquashFS filesystem
│   ├── SpecializationService.cs    # Coordinate specialization flow
│   └── ScaleControllerClient.cs    # HTTP client to register with SC
├── Grpc/
│   └── WorkerChannel.cs            # Send FunctionEnvironmentReloadRequest
└── appsettings.json
```

### 3.3 Placeholder → Specialized Flow

**Startup (Placeholder mode - NOT connected to Runtime):**
1. Runtime starts in placeholder mode, registers with SC via HTTP
2. Worker Sidecar starts:
   - Registers with SC as available placeholder via HTTP
   - Starts listening on `localhost:50051` for FunctionsNetHost
   - Does **NOT** connect to any Runtime (no RuntimeEndpoint yet)
3. FunctionsNetHost starts, connects to Sidecar on `localhost:50051`
4. Both Runtime and Worker are idle, waiting for SC to match them

**Specialization (SC matches Runtime + Worker):**
1. SC decides to specialize (triggered by demand, manual action, etc.)
2. SC selects an available placeholder Runtime and Worker
3. SC calls **Runtime's `POST /admin/instance/assign`** with `HostAssignmentContext`:
   - `SiteName`, `SiteId`
   - `Environment` dict (app settings)
   - `WEBSITE_RUN_FROM_PACKAGE` = blob URL to zip
4. Runtime downloads zip, mounts via SquashFS, restarts host
5. SC calls **Worker Sidecar's `POST /assign`** with:
   - Same `HostAssignmentContext`
   - **Plus `RuntimeEndpoint`** = the matched Runtime's gRPC endpoint
6. Sidecar:
   - Downloads zip, mounts via SquashFS to `/home/site/wwwroot`
   - **Now connects** to the provided Runtime endpoint
   - Sends `FunctionEnvironmentReloadRequest` to worker via gRPC:
     ```protobuf
     FunctionEnvironmentReloadRequest {
       environment_variables: { ... },
       function_app_directory: "/home/site/wwwroot"
     }
     ```
7. Worker receives `FunctionEnvironmentReloadRequest`:
   - Reloads environment variables
   - Reloads functions from mounted path
   - Sends `FunctionEnvironmentReloadResponse` back
8. Worker is now specialized and connected to its assigned Runtime
9. Runtime routes invocations to Worker

### 3.4 SquashFS App Mounting (Production Parity)

**Both Runtime and Worker mount the customer app via SquashFS**, matching production behavior:

| Component | Files Needed | Mount Point |
|-----------|-------------|-------------|
| **Runtime** | `host.json`, function metadata, extensions | `/home/site/wwwroot` |
| **Worker** | Customer DLLs, deps.json, runtime config | `/home/site/wwwroot` |

**SquashFS benefits:**
- **Read-only**: Immutable, secure
- **No extraction**: Mount directly, faster specialization
- **Deduplication**: Same zip cached once per node
- **Production parity**: Matches Flex Consumption behavior

**Implementation:**

```csharp
// WorkerModel.Sidecar/Services/SquashFsMounter.cs
public class SquashFsMounter
{
    public async Task<string> MountAsync(string zipPath, string mountPoint)
    {
        // Convert zip to squashfs (or use squashfuse directly on zip)
        // Linux: squashfuse or mount -t squashfs
        // For prototype, can use fuse-zip as simpler alternative
        
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "squashfuse",
            Arguments = $"{zipPath} {mountPoint}",
            RedirectStandardError = true
        });
        
        await process.WaitForExitAsync();
        return mountPoint;
    }
}
```

**Container requirements:**
- Install `squashfuse` or `fuse-zip` in container image
- Container needs `SYS_ADMIN` capability or `--privileged` for FUSE
- Or use `--device /dev/fuse` with `--cap-add SYS_ADMIN`

**Dockerfile addition:**
```dockerfile
# Install SquashFS tools
RUN apt-get update && apt-get install -y squashfuse fuse
```

**Proxy Flow (after specialization, steady state):**

Once the Sidecar receives `/assign` and connects to Runtime:
1. For each message from Worker:
   - Inject WorkerContext (WorkerId, Application)
   - Forward to Runtime
2. For each message from Runtime:
   - Forward to Worker unchanged

> **Note**: In placeholder mode, the Sidecar is only listening for FunctionsNetHost on `localhost:50051`. It does NOT connect to any Runtime until it receives `/assign` with the `RuntimeEndpoint`.

### 3.5 Configuration

```json
{
  "Sidecar": {
    "WorkerEndpoint": "localhost:50051",
    "ScaleControllerEndpoint": "http://scalecontroller:80"
  }
}
```

### 3.6 Communication Protocols

```
                                    ┌────────────────────────────────────────────────────┐
                                    │                   Runtime Pod                       │
                                    │                                                     │
┌──────────────────┐      HTTP      │  ┌─────────────────┐        ┌──────────────────┐   │
│ Scale Controller │───────────────▶│  │ Runtime Sidecar │        │     Runtime      │   │
│                  │   POST /mount  │  │                 │  read  │    (WebHost)     │   │
│                  │                │  │  SquashFS ──────┼────────┼▶ /home/site/     │   │
│                  │◀──────────────▶│  │  Mounter        │  files │    wwwroot       │   │
│                  │  /api/runtimes │  └─────────────────┘        │                  │   │
│                  │  /admin/assign │                              │  POST /admin/    │   │
│                  │                │                              │  instance/assign │   │
└────────┬─────────┘                └──────────────────────────────┴────────┬─────────┘   
         │                                                                  │
         │ HTTP                                                        gRPC │ FunctionRpc
         │ /api/workers/register                                            │ (after /assign)
         │ POST /assign                                                     │
         ▼                                                                  ▼
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│                                    Worker Pod                                             │
│                                                                                           │
│  ┌──────────────────┐            gRPC             ┌──────────────────┐  Wrapper manages  │
│  │  Worker Sidecar  │◀──────────────────────────▶│ FunctionsNetHost │◀─────process       │
│  │                  │      localhost:50051        │    (Worker)      │                    │
│  │  SquashFS ──────────────── read files ────────▶ /home/site/      │                    │
│  │  Mounter         │                             │    wwwroot       │                    │
│  └──────────────────┘                             └──────────────────┘                    │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

**Protocol summary:**
- **HTTP**: All SC ↔ Runtime/Worker communication
- **gRPC (FunctionRpc)**: Runtime ↔ Worker (via Worker Sidecar) for function invocations
- **SquashFS**: Both sidecars mount customer app packages for their respective pods

---

## Phase 3.5: Runtime Sidecar Implementation

The Runtime Sidecar is a lightweight service that runs alongside the Runtime (WebHost) in the same pod. Its sole responsibility is **mounting the customer's app package** so the Runtime can access the function metadata, host.json, and extensions.

### 3.5.1 Core Responsibilities

| Responsibility | Implementation |
|----------------|----------------|
| App download | Download zip from blob storage (via SAS URL) |
| SquashFS mounting | Mount zip as read-only filesystem |
| Mount point management | Expose `/home/site/wwwroot` to Runtime |
| Health endpoint | HTTP /health for k8s probes |
| Ready endpoint | HTTP /ready (true after mount complete) |

### 3.5.2 Key Classes

```
WorkerModel.RuntimeSidecar/
├── Program.cs
├── Controllers/
│   ├── MountController.cs          # POST /mount (from SC to trigger mount)
│   ├── HealthController.cs         # GET /health, GET /ready
│   └── StatusController.cs         # GET /status (mount info)
├── Services/
│   ├── PackageDownloader.cs        # Download zip from blob URL
│   ├── SquashFsMounter.cs          # Mount zip as SquashFS filesystem
│   └── MountManager.cs             # Track mounted packages, cleanup
└── appsettings.json
```

### 3.5.3 Mount Flow

**On `/mount` request from Scale Controller:**

1. SC calls `POST /mount` with:
   ```json
   {
     "applicationId": "sample-app",
     "codeVersion": "v1.0.0",
     "packageUrl": "https://storage.blob.../app.zip?sas=...",
     "mountPoint": "/home/site/wwwroot"
   }
   ```
2. Runtime Sidecar downloads the zip to local cache
3. Mounts via SquashFS:
   ```bash
   squashfuse /cache/sample-app-v1.0.0.zip /home/site/wwwroot
   ```
4. Returns 200 OK when mount is ready
5. Runtime sees files at `/home/site/wwwroot`

### 3.5.4 Why Separate from Runtime?

| Concern | Runtime | Runtime Sidecar |
|---------|---------|-----------------|
| **Filesystem access** | Reads from mount point | Manages FUSE mount |
| **Privileges** | No CAP_SYS_ADMIN needed | Needs CAP_SYS_ADMIN for FUSE |
| **Restart behavior** | Can restart without remount | Mount persists across Runtime restarts |
| **Security** | Runs customer code | Only handles packages |
| **Image updates** | WebHost changes frequently | Mounter changes rarely |

### 3.5.5 Shared Volume Setup (Kubernetes)

```yaml
# In the Runtime Pod spec
spec:
  containers:
  - name: runtime
    image: runtime:latest
    volumeMounts:
    - name: app-mount
      mountPath: /home/site/wwwroot
      
  - name: runtime-sidecar
    image: runtime-sidecar:latest
    securityContext:
      capabilities:
        add: ["SYS_ADMIN"]
      privileged: true  # Required for FUSE
    volumeMounts:
    - name: app-mount
      mountPath: /home/site/wwwroot
      mountPropagation: Bidirectional  # So Runtime sees the mount
    - name: fuse-device
      mountPath: /dev/fuse
      
  volumes:
  - name: app-mount
    emptyDir: {}
```

### 3.5.6 Communication Diagram

```
                          ┌─────────────────────────────────────────────┐
                          │              Runtime Pod                     │
                          │                                              │
┌──────────────────┐      │   ┌─────────────────┐   ┌────────────────┐  │
│ Scale Controller │──────┼──▶│ Runtime Sidecar │   │    Runtime     │  │
│                  │ HTTP │   │                 │   │   (WebHost)    │  │
│                  │      │   │  POST /mount    │   │                │  │
│                  │      │   │                 │   │ Reads from     │  │
│                  │      │   │  SquashFS ──────┼───┼─▶ /home/site/  │  │
│                  │      │   │  Mounter        │   │    wwwroot     │  │
│                  │      │   └─────────────────┘   └────────────────┘  │
└──────────────────┘      │         ▲                                   │
                          │         │ Shared emptyDir volume            │
                          │         │ with mountPropagation             │
                          └─────────┴───────────────────────────────────┘
```

### 3.5.7 Configuration

```json
{
  "RuntimeSidecar": {
    "CachePath": "/var/cache/functions",
    "DefaultMountPoint": "/home/site/wwwroot",
    "HealthCheckPath": "/health"
  }
}
```

---

## Phase 4: Wrapper Implementation

### 4.1 Core Responsibilities

| Responsibility | Implementation |
|----------------|----------------|
| Process supervision | Start/stop/restart FunctionsNetHost |
| Signal forwarding | Forward SIGTERM to worker |
| Unix socket API | Listen for restart commands |
| Exit handling | Exit if worker exits (let k8s restart) |

### 4.2 FunctionsNetHost

The worker process is **FunctionsNetHost** from the existing `Microsoft.Azure.Functions.DotNetIsolatedNativeHost` NuGet package (v1.0.13+). This is an AOT-compiled native binary that:

- Speaks gRPC to connect to the Runtime
- Loads the customer's .NET worker assembly dynamically
- Supports placeholder mode (connect without customer code)
- Supports specialization (load customer code on demand)

**Source location**: `azure-functions-dotnet-worker/host/src/FunctionsNetHost/`

**Key command-line arguments**:
```
FunctionsNetHost.exe \
  --functions-uri http://runtime:50051 \
  --functions-request-id <request-id> \
  --functions-worker-id <worker-id> \
  --functions-grpc-max-message-length 134217728
```

### 4.3 Key Classes

```
WorkerModel.Wrapper/
├── Program.cs                      # Entry point, PID 1
├── WorkerProcessManager.cs         # Start/stop FunctionsNetHost
├── RestartApiServer.cs             # Unix socket HTTP server
└── SignalHandler.cs                # SIGTERM/SIGINT handling
```

### 4.4 Startup Sequence

1. Read worker config from environment:
   - `FUNCTIONS_URI` (e.g., `http://runtime:50051`)
   - `FUNCTIONS_WORKER_ID` (e.g., `worker-001`)
   - `FUNCTIONS_GRPC_MAX_MESSAGE_LENGTH` (default: 134217728)
   - `AzureWebJobsScriptRoot` (mount point, e.g., `/home/site/wwwroot`)
2. Start FunctionsNetHost with appropriate arguments
3. Start Unix socket API server
4. Forward signals to FunctionsNetHost
5. Wait for worker exit or restart command

### 4.5 Restart API (for failures, not specialization)

The Wrapper's restart API is for **process failures**, not specialization. Specialization uses `FunctionEnvironmentReloadRequest` via gRPC without restarting the process.

```
POST /restart
{
  "reason": "Worker process crashed"
}

Response: 202 Accepted
```

On restart (failure recovery only):
1. Gracefully stops FunctionsNetHost (SIGTERM + wait)
2. Restarts FunctionsNetHost with same config

---

## Phase 5: Scale Controller Mock

### 5.1 Core Responsibilities (Mocked)

| Responsibility | Implementation |
|----------------|----------------|
| App deployment | Accept zip, store in blob |
| App metadata | Store in Cosmos DB |
| Runtime discovery | Hardcoded or config-based |
| Worker assignment | Manual via API |
| Specialization trigger | HTTP endpoint |
| Health monitoring | Basic polling |

### 5.2 Data Storage

**Blob Storage** - Customer app packages:
```
function-apps/
├── {appId}/
│   └── {codeVersion}/
│       └── app.zip          # Deployed zip file
```

**Cosmos DB** - App metadata:
```json
// Container: applications
{
  "id": "sample-app",
  "partitionKey": "sample-app",
  "displayName": "Sample Function App",
  "language": "dotnet-isolated",
  "languageVersion": "8.0",
  "metadataVersion": "1",
  "codeVersion": "v1.0.0",
  "blobPath": "function-apps/sample-app/v1.0.0/app.zip",
  "environment": {
    "MY_SETTING": "value"
  },
  "createdAt": "2026-02-05T10:00:00Z",
  "updatedAt": "2026-02-05T10:00:00Z"
}
```

```json
// Container: workers  
{
  "id": "worker-001",
  "partitionKey": "worker-001",
  "status": "specialized",  // placeholder | specializing | specialized
  "applicationId": "sample-app",      // null if placeholder
  "codeVersion": "v1.0.0",            // null if placeholder
  "assignedRuntimeId": "runtime-001", // null if placeholder (late-binding!)
  "sidecarEndpoint": "http://worker1-sidecar:8080",
  "lastHeartbeat": "2026-02-05T10:05:00Z"
}
```

```json
// Container: runtimes
{
  "id": "runtime-001",
  "partitionKey": "runtime-001",
  "status": "specialized",  // placeholder | specializing | specialized
  "applicationId": "sample-app",      // null if placeholder
  "grpcEndpoint": "http://runtime-001:50051",
  "httpEndpoint": "http://runtime-001:80",
  "lastHeartbeat": "2026-02-05T10:05:00Z"
}
```

### 5.3 Mock API Endpoints (all HTTP REST)

**Deployment:**
```
POST /api/apps
  - Create new app registration
  - Body: { appId, displayName, language, languageVersion }

POST /api/apps/{appId}/deploy
  - Deploy app code (zip upload)
  - Content-Type: multipart/form-data
  - Body: zip file + optional environment JSON
  - Stores zip in blob, updates Cosmos metadata
  - Returns: { codeVersion, blobPath }

GET /api/apps/{appId}
  - Get app metadata including current codeVersion

GET /api/apps/{appId}/download/{codeVersion}
  - Download zip (used by Sidecar during specialization)
  - Returns: zip file stream with SAS token
```

**Runtime/Worker Management:**
```
POST /api/runtimes/register
  - Runtime announces itself
  - Body: { runtimeId, endpoint, capacity }

POST /api/workers/register  
  - Worker (Sidecar) announces itself
  - Body: { workerId, sidecarEndpoint, isPlaceholder }

POST /api/workers/{workerId}/specialize
  - Trigger specialization
  - Body: { appId }
  - SC looks up app metadata, tells Sidecar to specialize

GET /api/workers/{workerId}/assignment
  - Get current worker assignment (polled by Sidecar)

GET /api/status
  - Show current Runtime/Worker/App status
```

### 5.4 Specialization Flow (SC Perspective)

**Late-binding: SC matches Runtime + Worker at specialization time:**

```
1. User deploys app via SC UI (zip uploaded to blob)
2. User triggers specialization for an app
3. SC finds available placeholder Runtime (not yet assigned)
4. SC finds available placeholder Worker (not yet connected)
5. SC calls Runtime: POST /admin/instance/assign
   {
     "siteName": "sample-app",
     "environment": {
       "AzureWebJobsScriptRoot": "/home/site/wwwroot",
       "WEBSITE_RUN_FROM_PACKAGE": "https://blob.../app.zip",
       "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
       ... other app settings
     }
   }

6. SC calls Worker Sidecar: POST /assign
   {
     "runtimeEndpoint": "http://runtime-001:50051",  // <-- NEW: which Runtime to connect to
     "hostAssignmentContext": {
       "siteName": "sample-app",
       "environment": { ... same as above ... }
     }
   }

7. Runtime: downloads zip, mounts via SquashFS, restarts host
8. Worker Sidecar: 
   - Downloads zip, mounts via SquashFS
   - **Connects to the specified Runtime endpoint**
   - Sends FunctionEnvironmentReloadRequest to worker
9. Worker reloads functions from mounted path
10. Runtime + Worker are now paired and specialized
11. Runtime routes invocations to Worker via gRPC
```

**Key benefits of late-binding:**
- **Independent scaling**: Runtimes and Workers scale independently
- **Flexible matching**: SC can match based on capacity, locality, etc.
- **No wasted connections**: Placeholders don't consume Runtime resources
- **Production parity**: Matches Flex Consumption's deferred binding model

### 5.5 Web UI (Simple)

Basic HTML page showing:
- **Apps**: List of deployed apps with deploy button (zip upload)
- **Runtimes**: Registered runtimes and their capacity
- **Workers**: Connected workers with status (placeholder/specialized)
- **Actions**: Buttons to manually trigger specialization
- **Logs**: Real-time log viewer

### 5.6 Worker Lifecycle Management

Runtimes and Workers register independently with SC (no connection between them yet):

```
┌───────────────┐                           ┌───────────────┐
│    Runtime    │                           │    Worker     │
│  (placeholder)│                           │   (Sidecar)   │
└───────┬───────┘                           └───────┬───────┘
        │                                           │
        │ POST /api/runtimes/register               │ POST /api/workers/register
        │ { runtimeId, endpoint }                   │ { workerId, sidecarEndpoint }
        │                                           │
        └───────────────────┬───────────────────────┘
                            ▼
                   ┌────────────────┐
                   │       SC       │
                   │                │
                   │  Tracks both   │
                   │  independently │
                   └────────────────┘
```

- Runtime and Worker are **NOT connected** in placeholder mode
- SC tracks available Runtimes and Workers separately in Cosmos
- At specialization time, SC **matches** one Runtime with one Worker
- SC provides Runtime endpoint to Worker during `/assign` call
- Only then does Worker connect to its assigned Runtime

---

## Phase 6: Runtime Modifications

### 6.1 No Changes to WebHost (Initial Setup)

For the initial prototype, **no modifications to existing WebHost code are required**:

| Existing Capability | Location | Why It Works |
|---------------------|----------|--------------|
| `/admin/instance/assign` | `InstanceController.cs` | Already handles `HostAssignmentContext` |
| Placeholder mode | Built-in | Runtime already supports cold start |
| SquashFS mounting | `RunFromPackageHandler` | Already supports `WEBSITE_RUN_FROM_PACKAGE` |
| gRPC channel | `FunctionRpcService.cs` | Already accepts worker connections |
| Process management | Feature flag | Disable with `FUNCTIONS_WORKER_PROCESS_COUNT=0` |

The existing Runtime already supports everything we need:
- Accepts specialization via `/admin/instance/assign`
- Downloads and mounts app packages
- Accepts gRPC connections from workers
- Can disable spawning worker processes

**Future enhancements** (not needed for initial prototype):
- WorkerContext injection for multi-tenant scenarios
- SC registration service
- Custom placeholder worker tracking

### 6.2 Feature Flags

```json
{
  "WorkerModel": {
    "DecoupledMode": true,
    "DisableProcessManagement": true,
    "ScaleControllerEndpoint": "http://scalecontroller:80"
  }
}
```

### 6.3 JobHostManager Integration

For Phase 1, we'll use a **single JobHost** (no multi-tenancy yet):
- One Function App
- Multiple workers connecting
- Standard WebJobs SDK trigger handling

---

## Phase 7: Sample Function App

### 7.1 Simple HTTP Trigger

```csharp
public class HttpTriggerFunction
{
    [Function("HelloWorld")]
    public HttpResponseData Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] 
        HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.WriteString($"Hello from worker {Environment.MachineName}!");
        return response;
    }
}
```

### 7.2 Additional Test Functions

- Timer trigger (verify trigger listeners work)
- Queue trigger (verify message processing)
- Durable function (verify orchestration)

---

## Phase 8: Aspire Orchestration

### 8.1 Why Aspire?

- **Dashboard**: Built-in observability (logs, traces, metrics)
- **Service discovery**: Automatic endpoint resolution
- **Local dev**: Run everything with F5, no Docker needed
- **Container support**: Can still publish to Docker/K8s
- **Hot reload**: Faster iteration during development

### 8.2 AppHost Project

```csharp
// WorkerModel.AppHost/Program.cs
var builder = DistributedApplication.CreateBuilder(args);

// Storage emulator (for function triggers AND app zip storage)
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator();

var blobs = storage.AddBlobs("blobs");       // App zips + function storage
var queues = storage.AddQueues("queues");    // For queue trigger testing

// Cosmos DB emulator (for app metadata)
var cosmos = builder.AddAzureCosmosDB("cosmos")
    .RunAsEmulator();

var metadataDb = cosmos.AddDatabase("workermodel");

// Scale Controller (mock) - needs blob + cosmos access
var scaleController = builder.AddProject<Projects.WorkerModel_ScaleController>("scalecontroller")
    .WithReference(blobs)        // To store/retrieve app zips
    .WithReference(metadataDb)   // To store app/worker metadata
    .WithExternalHttpEndpoints(); // Expose UI externally

// Runtime Sidecar - handles SquashFS mounting for Runtime pod
var runtimeSidecar = builder.AddProject<Projects.WorkerModel_RuntimeSidecar>("runtime-sidecar")
    .WithReference(scaleController)  // To receive mount commands
    .WithReference(blobs);           // To download app zips

// Runtime (existing WebHost) - runs independently as placeholder
var runtime = builder.AddProject<Projects.WebJobs_Script_WebHost>("runtime")
    .WithReference(storage)          // For function triggers
    .WithReference(scaleController)  // To register with SC
    .WithReference(runtimeSidecar)   // Runtime Sidecar manages its mounts
    .WithEnvironment("WorkerModel__DecoupledMode", "true")
    .WithEnvironment("WorkerModel__DisableProcessManagement", "true");

// Worker Sidecar 1 - runs independently, NO reference to runtime yet (late-binding)
var worker1 = builder.AddProject<Projects.WorkerModel_Sidecar>("worker1-sidecar")
    .WithReference(scaleController)  // To register with SC and receive /assign
    .WithReference(blobs)            // To download app zips
    .WithEnvironment("WORKER_ID", "worker-001")
    .WithEnvironment("AzureWebJobsScriptRoot", "/home/site/wwwroot");  // SquashFS mount point
    // NOTE: No .WithReference(runtime) - connection happens at specialization!

var worker2 = builder.AddProject<Projects.WorkerModel_Sidecar>("worker2-sidecar")
    .WithReference(scaleController)
    .WithReference(blobs)
    .WithEnvironment("WORKER_ID", "worker-002")
    .WithEnvironment("AzureWebJobsScriptRoot", "/home/site/wwwroot");

// For container mode (closer to production)
if (builder.Configuration["UseContainers"] == "true")
{
    // Add container-based workers instead
    builder.AddDockerfile("worker1", "../docker")
        .WithReference(runtime)
        .WithReference(blobs);
}

builder.Build().Run();
```

### 8.3 Cosmos DB Setup

The SC creates containers on startup:

```csharp
// WorkerModel.ScaleController/Services/CosmosInitializer.cs
public class CosmosInitializer : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var db = await _client.CreateDatabaseIfNotExistsAsync("workermodel");
        
        // Applications container
        await db.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("applications", "/partitionKey"));
        
        // Workers container  
        await db.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("workers", "/partitionKey"));
        
        // Runtimes container
        await db.Database.CreateContainerIfNotExistsAsync(
            new ContainerProperties("runtimes", "/partitionKey"));
    }
}
```

### 8.4 Service Defaults

```csharp
// WorkerModel.ServiceDefaults/Extensions.cs
public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
{
    builder.ConfigureOpenTelemetry();
    builder.AddDefaultHealthChecks();
    builder.Services.AddServiceDiscovery();
    
    builder.Services.ConfigureHttpClientDefaults(http =>
    {
        http.AddStandardResilienceHandler();
        http.AddServiceDiscovery();
    });
    
    return builder;
}
```

### 8.5 Running Locally

```powershell
# From prototype folder
cd azure-functions-host/prototype

# Run with Aspire (opens dashboard at https://localhost:15000)
dotnet run --project WorkerModel.AppHost

# Or with containers
dotnet run --project WorkerModel.AppHost -- --UseContainers=true
```

### 8.6 Aspire Dashboard

The dashboard provides:
- **Resources**: See all running services (including Cosmos & Storage emulators)
- **Console**: Aggregated logs from all services
- **Traces**: Distributed tracing across Runtime → Sidecar → Worker
- **Metrics**: Request rates, latencies, errors

---

## Phase 9: Docker (Alternative to Aspire)

### 9.1 Dockerfile.runtime

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Install SquashFS tools for mounting customer apps (production parity)
RUN apt-get update && apt-get install -y squashfuse fuse && rm -rf /var/lib/apt/lists/*

# Mount point for customer app (SquashFS will mount here)
RUN mkdir -p /home/site/wwwroot

COPY publish/WebHost .
ENV ASPNETCORE_URLS=http://+:80
ENV WorkerModel__DecoupledMode=true
ENV AzureWebJobsScriptRoot=/home/site/wwwroot
EXPOSE 80 50051
ENTRYPOINT ["dotnet", "Microsoft.Azure.WebJobs.Script.WebHost.dll"]
```

### 9.2 Dockerfile.worker

```dockerfile
# Multi-stage: build wrapper AOT + include FunctionsNetHost
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Build AOT wrapper
WORKDIR /src/wrapper
COPY WorkerModel.Wrapper .
RUN dotnet publish -c Release -r linux-x64 -o /app/wrapper

# Build sidecar
WORKDIR /src/sidecar
COPY WorkerModel.Sidecar .
RUN dotnet publish -c Release -o /app/sidecar

# Download FunctionsNetHost from NuGet
WORKDIR /src/nethost
RUN dotnet new console -n temp && \
    dotnet add temp package Microsoft.Azure.Functions.DotNetIsolatedNativeHost --version 1.0.13 && \
    cp -r ~/.nuget/packages/microsoft.azure.functions.dotnetisolatednativehost/1.0.13/contentFiles/any/any/workers/dotnet-isolated /app/dotnet-isolated

# Final image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Install SquashFS tools for mounting customer apps (production parity)
RUN apt-get update && apt-get install -y squashfuse fuse && rm -rf /var/lib/apt/lists/*

# Copy AOT wrapper (native binary, no .NET needed)
COPY --from=build /app/wrapper/WorkerModel.Wrapper /app/wrapper

# Copy sidecar (needs .NET runtime)
COPY --from=build /app/sidecar /app/sidecar

# Copy FunctionsNetHost (native binary from NuGet)
COPY --from=build /app/dotnet-isolated /app/dotnet-isolated

# Mount point for customer app (SquashFS will mount here)
RUN mkdir -p /home/site/wwwroot

# Environment
ENV WORKER_ID=worker-001
ENV FUNCTIONS_NETHOST_PATH=/app/dotnet-isolated/bin/FunctionsNetHost
ENV AzureWebJobsScriptRoot=/home/site/wwwroot
# NOTE: No FUNCTIONS_URI - provided by SC at specialization time

# Wrapper is entrypoint (PID 1)
ENTRYPOINT ["/app/wrapper"]
```

### 9.3 docker-compose.yml

```yaml
version: '3.8'

services:
  runtime:
    build:
      context: .
      dockerfile: docker/Dockerfile.runtime
    ports:
      - "7071:80"      # HTTP
      - "50051:50051"  # gRPC
    environment:
      - AzureWebJobsStorage=UseDevelopmentStorage=true
      - WorkerModel__DecoupledMode=true
      - WorkerModel__ScaleControllerEndpoint=http://scalecontroller:80
    # Required for SquashFS/FUSE mounting
    devices:
      - /dev/fuse
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    depends_on:
      - azurite

  worker1:
    build:
      context: .
      dockerfile: docker/Dockerfile.worker
    environment:
      - WORKER_ID=worker-001
      - SCALECONTROLLER_ENDPOINT=http://scalecontroller:80
      # NOTE: No RUNTIME_ENDPOINT - provided by SC at specialization time
    # Required for SquashFS/FUSE mounting
    devices:
      - /dev/fuse
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    depends_on:
      - scalecontroller

  worker2:
    build:
      context: .
      dockerfile: docker/Dockerfile.worker
    environment:
      - WORKER_ID=worker-002
      - SCALECONTROLLER_ENDPOINT=http://scalecontroller:80
      # NOTE: No RUNTIME_ENDPOINT - provided by SC at specialization time
    devices:
      - /dev/fuse
    cap_add:
      - SYS_ADMIN
    security_opt:
      - apparmor:unconfined
    depends_on:
      - scalecontroller

  scalecontroller:
    build:
      context: .
      dockerfile: docker/Dockerfile.scalecontroller
    ports:
      - "8080:80"
    environment:
      - COSMOS_ENDPOINT=https://cosmosdb:8081
      - BLOB_ENDPOINT=http://azurite:10000
    depends_on:
      - azurite
      - cosmosdb

  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  cosmosdb:
    image: mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:latest
    ports:
      - "8081:8081"
    environment:
      - AZURE_COSMOS_EMULATOR_PARTITION_COUNT=10
```

---

## Phase 10: Testing & Validation

### 10.1 Success Criteria

| Criteria | Validation |
|----------|------------|
| Worker connects via Sidecar | gRPC stream established |
| Function invocation works | HTTP trigger returns response |
| Multiple workers | Load balanced across workers |
| SquashFS mounting | Both Runtime and Worker mount app correctly |
| Basic specialization | SC triggers `/assign`, both mount + reload |

### 10.2 Test Scenarios

1. **Placeholder startup (independent)**
   - Start Runtime via Aspire - verifies it registers with SC
   - Start Worker via Aspire - verifies it registers with SC
   - Verify Runtime and Worker are **NOT connected** to each other
   - Verify both show as available placeholders in SC dashboard

2. **Specialization with late-binding**
   - Deploy sample app via SC UI (upload zip)
   - Trigger specialization via SC UI
   - Verify SC calls Runtime `/admin/instance/assign`
   - Verify SC calls Sidecar `/assign` with **RuntimeEndpoint included**
   - Verify both download zip and mount via SquashFS
   - Verify **Worker connects to Runtime** only after receiving /assign
   - Verify Sidecar sends `FunctionEnvironmentReloadRequest` to worker
   - Verify worker reloads functions from mounted path
   - Verify HTTP request to function succeeds

3. **Subsequent invocations**
   - Send more HTTP requests
   - Verify routing to specialized worker
   - Verify no re-specialization

4. **Multiple workers**
   - Start 3 placeholder workers
   - Specialize all to same app
   - Send multiple requests
   - Verify load distribution

5. **Worker restart**
   - Kill worker process
   - Verify Wrapper detects exit
   - Verify Wrapper restarts worker
   - Verify worker reconnects (still specialized)

---

## Implementation Order

### Week 1: Foundation
- [x] Create project structure
- [x] Define proto contracts
- [x] Implement basic Wrapper (start process, forward signals)
- [x] Implement basic Worker Sidecar (passthrough proxy)

### Week 2: Integration
- [x] Connect Worker Sidecar to existing WebHost
- [x] Add WorkerContext injection
- [x] Implement Wrapper restart API
- [x] Create Docker images

### Week 3: End-to-End
- [x] Aspire orchestration working
- [x] SC with Cosmos + Blob storage
- [x] Zip upload and download flow
- [ ] HTTP trigger invocation working
- [ ] Multiple workers routing correctly

### Week 3.5: Runtime Sidecar & WebHost Integration (NEW)
- [x] Add WebHost and dependencies to WorkerModelPrototype.sln
- [x] Create WorkerModel.RuntimeSidecar project
- [x] Implement SquashFS mount service for Runtime pod
- [x] Implement `/mount` endpoint for SC communication
- [ ] Configure shared volume between Runtime and RuntimeSidecar
- [ ] Test Runtime reading files from mounted path
- [x] Update AppHost to orchestrate RuntimeSidecar with Runtime

### Week 3.6: Worker Placeholder Warm-up (CURRENT)

Get the worker-side components (Sidecar + Wrapper + FunctionsNetHost) to fully
initialize in placeholder mode and sit idle, ready for specialization.

**Flow:**
1. Sidecar starts → listens for gRPC on its Aspire-assigned port, registers with SC
2. Wrapper starts → reads FUNCTIONS_URI from Sidecar endpoint, launches FunctionsNetHost
3. FunctionsNetHost connects to Sidecar gRPC, sends `StartStream { WorkerId }`
4. Sidecar (acting as host) sends `WorkerInitRequest` to FunctionsNetHost
5. FunctionsNetHost responds with `WorkerInitResponse` (capabilities, version)
6. Sidecar sends `FunctionsMetadataRequest` (placeholder — no app directory yet)
7. FunctionsNetHost responds with `FunctionMetadataResponse { UseDefaultMetadataIndexing = true }`
8. **Done** — Sidecar, FunctionsNetHost, and Wrapper are warm and idle

**Key insight**: In placeholder mode the Sidecar must **act as the host** and drive
the gRPC initialization handshake. FunctionsNetHost only sends `StartStream` on its
own; all subsequent requests (`WorkerInitRequest`, `FunctionsMetadataRequest`, etc.)
must come from the host side (the Sidecar).

**Tasks:**
- [ ] Fix `SidecarRpcService.HandlePlaceholderModeAsync` to drive init handshake:
      receive `StartStream` → send `WorkerInitRequest` → receive `WorkerInitResponse`
      → send `FunctionsMetadataRequest` → receive `FunctionMetadataResponse` → idle
- [ ] Add placeholder readiness tracking to `WorkerState` (started / init_sent / warm / error)
- [ ] Verify FunctionsNetHost connects successfully to Sidecar's Aspire-assigned gRPC endpoint
- [ ] Verify Wrapper sees FunctionsNetHost running and stable (not crashing)
- [ ] Verify Aspire dashboard shows all three components healthy
- [ ] Verify SC dashboard shows workers registered and runtimes registered

### Week 4: Refinement
- [x] SC Web UI for deployment/monitoring
- [ ] Config change detection
- [ ] Worker restart flow
- [ ] Error handling
- [ ] Documentation and demo

---

## Completed Work

- [x] Created project structure (Contracts, Sidecar, Wrapper, ScaleController, AppHost)
- [x] Implemented ScaleController with Cosmos + Blob storage (mock)
- [x] Implemented Worker Sidecar with gRPC proxy and /assign endpoint
- [x] Implemented Wrapper process supervisor
- [x] Configured Aspire AppHost orchestration
- [x] Added WaitFor() dependencies in AppHost
- [x] Removed hardcoded ports - Aspire handles dynamic port assignment
- [x] Added WebHost (Runtime) and all dependencies to WorkerModelPrototype.sln
- [x] Organized solution with Runtime and Worker solution folders
- [x] Created WorkerModel.RuntimeSidecar project (MountController, PackageDownloader, SquashFsMounter, MountManager)
- [x] Added RuntimeSidecar and WebHost to AppHost orchestration with proper WaitFor/WithReference chains

---

## Next Steps

1. ~~**Add WebHost to solution**~~ ✅ Done
2. ~~**Create RuntimeSidecar project**~~ ✅ Done
3. **Configure shared volume** - Set up shared mount point between RuntimeSidecar and Runtime for local dev
4. **Implement mount flow** - SC → RuntimeSidecar → mount → Runtime reads files
5. **Test end-to-end** - Deploy app, verify specialization, invoke function
6. **Run the full prototype** - Start AppHost, test complete flow

---

## Open Questions for Prototype

1. **Sidecar language**: .NET or Go?
   - Decision: .NET 8 for team familiarity and consistency with host

2. **Wrapper language**: .NET AOT or Go/Rust?
   - Decision: .NET 8 AOT to prove it works without runtime

3. **FunctionsNetHost version**: Use existing NuGet package or build from source?
   - Option A: Use `Microsoft.Azure.Functions.DotNetIsolatedNativeHost` NuGet (simplest)
   - Option B: Build from `azure-functions-dotnet-worker/host/src/FunctionsNetHost`
   - Recommendation: Option A for prototype, may need Option B for modifications

4. **Aspire vs Docker**: Which to prioritize?
   - Decision: Aspire for development, Docker for production validation

5. **Existing tests**: Run existing WebHost tests?
   - Recommendation: Feature flag to run in both modes

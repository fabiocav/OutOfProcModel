# Worker Design

This document covers the Worker-side architecture in the new decoupled model, including the Worker Sidecar pattern that enables existing workers to function without modification.

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Worker Sidecar Architecture](#worker-sidecar-architecture)
3. [Authentication](#authentication)
4. [Message Transformation](#message-transformation)
5. [Specialization Injection](#specialization-injection)
6. [Sidecar Implementation](#sidecar-implementation)
7. [Deployment Patterns](#deployment-patterns)
8. [Open Questions](#open-questions)

---

## Problem Statement

### Current Model (In-Process Host)

In the current Azure Functions architecture:
- Host and Worker run in the **same process** or on the **same machine**
- Host reads environment variables directly to understand the application context
- Host controls worker process lifecycle (start, stop, kill)
- Worker connects to a local gRPC endpoint (localhost)
- No authentication needed - same trust boundary

```
┌────────────────────────────────────────────────────────────┐
│  Same Machine / Process                                    │
├────────────────────────────────────────────────────────────┤
│  ┌──────────────┐         ┌──────────────┐                │
│  │   Host       │◄───────►│   Worker     │                │
│  │              │  gRPC   │              │                │
│  │  - Reads ENV │ (local) │  - Executes  │                │
│  │  - Knows app │         │    functions │                │
│  │  - Controls  │         │              │                │
│  │    lifecycle │         │              │                │
│  └──────────────┘         └──────────────┘                │
│         │                                                  │
│         ▼                                                  │
│  Environment Variables:                                    │
│  - FUNCTIONS_WORKER_RUNTIME=python                        │
│  - AzureWebJobsStorage=...                                │
│  - ApplicationName=my-function-app                        │
│  - etc.                                                    │
└────────────────────────────────────────────────────────────┘
```

### New Model Challenges

In the decoupled architecture:
- Runtime and Worker are in **separate containers**
- Runtime cannot read Worker's environment variables
- **Worker must send application context to Runtime**
- Network communication requires **authentication**
- Multiple workers from different tenants connect to shared Runtime

```
┌─────────────────┐         ┌─────────────────┐
│  Runtime        │         │  Worker         │
│  Container      │◄───────►│  Container      │
│                 │  gRPC   │                 │
│  - No ENV access│ (network)│  - Has ENV     │
│  - Must receive │         │  - Must send   │
│    app context  │         │    app context │
│  - Multi-tenant │         │  - Must auth   │
└─────────────────┘         └─────────────────┘
        │                           │
        │                           │
   How does Runtime          How does Worker
   know which app?           authenticate?
```

### Specific Challenges

| Challenge | Description | Impact |
|-----------|-------------|--------|
| **App Identity** | Runtime needs ApplicationId, Version, Language | Can't route messages without it |
| **Authentication** | Workers must prove identity to Runtime | Security requirement |
| **Existing Workers** | Python, Node, Java, PowerShell workers exist | Can't require major changes |
| **Multi-tenancy** | Multiple customers share Runtime | Must isolate correctly |
| **Specialization** | Context changes when placeholder becomes specialized | Dynamic injection needed |

### Data That Must Flow Worker → Runtime

```csharp
// Information Runtime needs from Worker
public class WorkerContext
{
    // Application Identity
    public string ApplicationId { get; set; }
    public string MetadataVersion { get; set; }
    public string CodeVersion { get; set; }
    
    // Worker Identity
    public string WorkerId { get; set; }
    public string Language { get; set; }        // python, node, dotnet, java, powershell
    public string LanguageVersion { get; set; } // 3.11, 20, 8.0, 17, 7.4
    
    // Capabilities
    public WorkerCapabilities Capabilities { get; set; }
    
    // Authentication
    public string AuthToken { get; set; }
    public string TenantId { get; set; }
    
    // Instance Info
    public string InstanceId { get; set; }
    public bool IsPlaceholder { get; set; }
}
```

---

## Worker Sidecar Architecture

### Solution: Transparent Proxy Sidecar

Instead of modifying each language worker, introduce a **Worker Sidecar** that:
1. Runs alongside the worker in the same pod/container group
2. Acts as a gRPC proxy between Worker and Runtime
3. Handles authentication transparently
4. Injects application context into messages
5. Allows existing workers to remain unchanged

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Worker Pod / Container Group                                           │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌──────────────┐         ┌──────────────────┐         ┌─────────────┐ │
│  │   Worker     │◄───────►│  Worker Sidecar  │◄───────►│   Runtime   │ │
│  │   (unchanged)│  gRPC   │                  │  gRPC   │   (remote)  │ │
│  │              │ (local) │  - Auth          │(network)│             │ │
│  │  Thinks it's │         │  - Transform     │         │             │ │
│  │  talking to  │         │  - Inject context│         │             │ │
│  │  local host  │         │  - Monitor       │         │             │ │
│  └──────────────┘         └──────────────────┘         └─────────────┘ │
│        │                          │                                     │
│        │                          ▼                                     │
│        │                 ┌──────────────────┐                          │
│        │                 │ Environment Vars │                          │
│        │                 │ - ApplicationId  │                          │
│        │                 │ - Version        │                          │
│        │                 │ - Language       │                          │
│        │                 │ - Auth secrets   │                          │
│        │                 └──────────────────┘                          │
│        │                                                                │
│        ▼                                                                │
│  Worker sees: localhost:50051 (sidecar)                                │
│  Sidecar connects to: runtime.internal:50051                           │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Key Benefits

| Benefit | Description |
|---------|-------------|
| **Zero Worker Changes** | Existing workers connect to localhost as before |
| **Centralized Auth** | Single component handles all authentication |
| **Context Injection** | Sidecar reads ENV and injects into messages |
| **Language Agnostic** | One sidecar works for all worker languages |
| **Upgradeable** | Sidecar can be updated independently of workers |
| **Testable** | Workers can be tested locally without sidecar |

### Message Flow

```
┌────────────────────────────────────────────────────────────────────────┐
│  Worker → Sidecar → Runtime (Upstream)                                 │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  Worker sends:                    Sidecar transforms to:               │
│  ┌─────────────────────────┐     ┌─────────────────────────────────┐  │
│  │ StreamingMessage        │     │ StreamingMessage                │  │
│  │   WorkerInitResponse    │ ──► │   WorkerInitResponse            │  │
│  │     Capabilities: {...} │     │     Capabilities: {...}         │  │
│  │                         │     │   + WorkerContext:              │  │
│  │                         │     │       ApplicationId: "app-123"  │  │
│  │                         │     │       MetadataVersion: "1.0.0"  │  │
│  │                         │     │       Language: "python"        │  │
│  │                         │     │       AuthToken: "eyJ..."       │  │
│  └─────────────────────────┘     └─────────────────────────────────┘  │
│                                                                        │
├────────────────────────────────────────────────────────────────────────┤
│  Runtime → Sidecar → Worker (Downstream)                               │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  Runtime sends:                   Sidecar forwards (mostly unchanged): │
│  ┌─────────────────────────┐     ┌─────────────────────────────────┐  │
│  │ StreamingMessage        │     │ StreamingMessage                │  │
│  │   InvocationRequest     │ ──► │   InvocationRequest             │  │
│  │     FunctionId: "fn1"   │     │     FunctionId: "fn1"           │  │
│  │     InputData: [...]    │     │     InputData: [...]            │  │
│  └─────────────────────────┘     └─────────────────────────────────┘  │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
```

---

## Authentication

### Challenge

Workers must authenticate to Runtime to:
1. Prove they are legitimate workers (not attackers)
2. Associate with the correct tenant/application
3. Establish trust for multi-tenant isolation

### Proposed: Token-Based Authentication

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Authentication Flow                                                     │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  1. Scale Controller provisions Worker with auth token                  │
│                                                                         │
│     Scale Controller ──► Worker Container                               │
│                          ENV: WORKER_AUTH_TOKEN=eyJ...                  │
│                          ENV: RUNTIME_ENDPOINT=runtime.internal:50051   │
│                                                                         │
│  2. Sidecar reads token and attaches to gRPC calls                     │
│                                                                         │
│     Sidecar reads ENV ──► Attaches as gRPC metadata                    │
│                          authorization: Bearer eyJ...                   │
│                                                                         │
│  3. Runtime validates token                                             │
│                                                                         │
│     Runtime receives ──► Validates signature                           │
│                      ──► Extracts claims (app, tenant, etc.)           │
│                      ──► Associates worker with application            │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Token Structure (JWT)

```json
{
  "header": {
    "alg": "RS256",
    "typ": "JWT"
  },
  "payload": {
    "iss": "functions-scale-controller",
    "sub": "worker-instance-abc123",
    "aud": "functions-runtime",
    "exp": 1738627200,
    "iat": 1738540800,
    
    "app_id": "my-function-app",
    "metadata_version": "1.0.0",
    "code_version": "1.0.0",
    "tenant_id": "contoso.onmicrosoft.com",
    "language": "python",
    "language_version": "3.11",
    "is_placeholder": false,
    "instance_id": "instance-xyz789"
  }
}
```

### Sidecar Auth Implementation

```csharp
public class AuthenticatingInterceptor : Interceptor
{
    private readonly string _authToken;
    
    public AuthenticatingInterceptor()
    {
        _authToken = Environment.GetEnvironmentVariable("WORKER_AUTH_TOKEN")
            ?? throw new InvalidOperationException("WORKER_AUTH_TOKEN not set");
    }
    
    public override AsyncDuplexStreamingCall<TRequest, TResponse> 
        AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        // Add auth header to all calls
        var headers = options.Headers ?? new Metadata();
        headers.Add("authorization", $"Bearer {_authToken}");
        
        return continuation(method, host, options.WithHeaders(headers));
    }
}
```

### Authentication Options

| Option | Description | Pros | Cons |
|--------|-------------|------|------|
| **JWT Token** | Scale Controller issues signed token | Stateless validation, standard | Token refresh needed |
| **mTLS** | Mutual TLS with client certs | Strong security | Cert management complexity |
| **Shared Secret** | Simple symmetric key | Easy to implement | Less secure, rotation difficult |
| **Managed Identity** | Azure AD workload identity | Cloud-native | Azure-specific |

**Recommendation**: JWT tokens for flexibility, with mTLS as optional enhancement for high-security scenarios.

---

## Message Transformation

### Upstream Transformations (Worker → Runtime)

The sidecar intercepts and enriches messages from the worker:

```csharp
public class MessageTransformer
{
    private readonly WorkerContext _context;
    
    public MessageTransformer()
    {
        // Read context from environment on startup
        _context = new WorkerContext
        {
            ApplicationId = GetRequiredEnv("APPLICATION_ID"),
            MetadataVersion = GetRequiredEnv("METADATA_VERSION"),
            CodeVersion = GetRequiredEnv("CODE_VERSION"),
            WorkerId = GetRequiredEnv("WORKER_ID"),
            Language = GetRequiredEnv("FUNCTIONS_WORKER_RUNTIME"),
            LanguageVersion = GetRequiredEnv("LANGUAGE_VERSION"),
            InstanceId = GetRequiredEnv("INSTANCE_ID"),
            IsPlaceholder = GetEnvBool("IS_PLACEHOLDER", false)
        };
    }
    
    public StreamingMessage TransformUpstream(StreamingMessage message)
    {
        // Inject context into specific message types
        switch (message.ContentCase)
        {
            case StreamingMessage.ContentOneofCase.StartStream:
                return EnrichStartStream(message);
                
            case StreamingMessage.ContentOneofCase.WorkerInitResponse:
                return EnrichWorkerInitResponse(message);
                
            case StreamingMessage.ContentOneofCase.FunctionMetadataResponse:
                return EnrichFunctionMetadataResponse(message);
                
            default:
                // Pass through unchanged
                return message;
        }
    }
    
    private StreamingMessage EnrichStartStream(StreamingMessage message)
    {
        // Add application context to StartStream
        var startStream = message.StartStream;
        
        // Inject context as properties (or dedicated field)
        startStream.Properties["ApplicationId"] = _context.ApplicationId;
        startStream.Properties["MetadataVersion"] = _context.MetadataVersion;
        startStream.Properties["CodeVersion"] = _context.CodeVersion;
        startStream.Properties["Language"] = _context.Language;
        startStream.Properties["LanguageVersion"] = _context.LanguageVersion;
        startStream.Properties["InstanceId"] = _context.InstanceId;
        startStream.Properties["IsPlaceholder"] = _context.IsPlaceholder.ToString();
        
        return message;
    }
    
    private StreamingMessage EnrichWorkerInitResponse(StreamingMessage message)
    {
        // Add language-specific info if not present
        var initResponse = message.WorkerInitResponse;
        
        // Ensure worker version is set
        if (string.IsNullOrEmpty(initResponse.WorkerVersion))
        {
            initResponse.WorkerVersion = GetWorkerVersion();
        }
        
        return message;
    }
    
    private StreamingMessage EnrichFunctionMetadataResponse(StreamingMessage message)
    {
        // Add application context to each function's metadata
        var metadataResponse = message.FunctionMetadataResponse;
        
        foreach (var function in metadataResponse.FunctionMetadataResults)
        {
            function.Properties["ApplicationId"] = _context.ApplicationId;
        }
        
        return message;
    }
}
```

### Downstream Transformations (Runtime → Worker)

Most downstream messages pass through unchanged, but some may need filtering or transformation:

```csharp
public StreamingMessage TransformDownstream(StreamingMessage message)
{
    switch (message.ContentCase)
    {
        case StreamingMessage.ContentOneofCase.FunctionEnvironmentReloadRequest:
            return HandleEnvironmentReload(message);
            
        default:
            // Pass through unchanged
            return message;
    }
}

private StreamingMessage HandleEnvironmentReload(StreamingMessage message)
{
    var reloadRequest = message.FunctionEnvironmentReloadRequest;
    
    // Update local context when specialization happens
    if (reloadRequest.EnvironmentVariables.TryGetValue("APPLICATION_ID", out var newAppId))
    {
        _context.ApplicationId = newAppId;
        _context.IsPlaceholder = false;
        
        _logger.LogInformation(
            "Sidecar context updated: ApplicationId={AppId}, IsPlaceholder={IsPlaceholder}",
            _context.ApplicationId,
            _context.IsPlaceholder);
    }
    
    // Forward to worker unchanged
    return message;
}
```

---

## Specialization Injection

### Challenge

When a placeholder worker gets specialized:
1. Environment variables change (new app context)
2. Worker receives `FunctionEnvironmentReloadRequest`
3. Sidecar must update its injected context

### Flow

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Placeholder Specialization Flow                                        │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Initial State (Placeholder):                                           │
│  ┌──────────────┐     ┌──────────────┐     ┌──────────────┐            │
│  │   Worker     │     │   Sidecar    │     │   Runtime    │            │
│  │ Placeholder  │     │ Context:     │     │              │            │
│  │              │     │  App=_Plhldr │     │              │            │
│  │              │     │  IsPlhldr=T  │     │              │            │
│  └──────────────┘     └──────────────┘     └──────────────┘            │
│                                                                         │
│  Scale Controller deploys customer app to this worker:                  │
│                                                                         │
│  1. Scale Controller updates ENV (container-level)                      │
│     APPLICATION_ID = "customer-app-123"                                 │
│     METADATA_VERSION = "2.0.0"                                          │
│     IS_PLACEHOLDER = "false"                                            │
│                                                                         │
│  2. Scale Controller notifies Runtime                                   │
│                                                                         │
│  3. Runtime sends FunctionEnvironmentReloadRequest to Sidecar           │
│                                                                         │
│  4. Sidecar receives, updates its context:                              │
│     ┌──────────────┐                                                    │
│     │   Sidecar    │                                                    │
│     │ Context:     │  ◄── Updated!                                      │
│     │  App=cust-123│                                                    │
│     │  IsPlhldr=F  │                                                    │
│     └──────────────┘                                                    │
│                                                                         │
│  5. Sidecar forwards reload request to Worker                           │
│                                                                         │
│  6. Worker reloads environment, loads customer code                     │
│                                                                         │
│  7. Subsequent messages from Worker now get new context injected        │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### Sidecar Context Update

```csharp
public class SidecarContextManager
{
    private WorkerContext _context;
    private readonly ReaderWriterLockSlim _lock = new();
    
    public void HandleSpecialization(FunctionEnvironmentReloadRequest request)
    {
        _lock.EnterWriteLock();
        try
        {
            var env = request.EnvironmentVariables;
            
            // Update context from new environment
            _context = new WorkerContext
            {
                ApplicationId = env.GetValueOrDefault("APPLICATION_ID", _context.ApplicationId),
                MetadataVersion = env.GetValueOrDefault("METADATA_VERSION", _context.MetadataVersion),
                CodeVersion = env.GetValueOrDefault("CODE_VERSION", _context.CodeVersion),
                WorkerId = _context.WorkerId, // Doesn't change
                Language = _context.Language, // Doesn't change
                LanguageVersion = _context.LanguageVersion, // Doesn't change
                InstanceId = _context.InstanceId, // Doesn't change
                IsPlaceholder = bool.Parse(env.GetValueOrDefault("IS_PLACEHOLDER", "false"))
            };
            
            _logger.LogInformation(
                "Sidecar specialized: {OldApp} → {NewApp}:{MetadataVersion}",
                _context.ApplicationId,
                _context.MetadataVersion);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
    
    public WorkerContext GetContext()
    {
        _lock.EnterReadLock();
        try
        {
            return _context;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}
```

---

## Sidecar Implementation

### Architecture

```csharp
public class WorkerSidecar : IHostedService
{
    private readonly ILogger<WorkerSidecar> _logger;
    private readonly SidecarContextManager _contextManager;
    private readonly MessageTransformer _transformer;
    private readonly AuthenticatingInterceptor _authInterceptor;
    
    private Server _localServer;        // gRPC server for Worker
    private GrpcChannel _runtimeChannel; // gRPC client to Runtime
    
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 1. Read configuration from environment
        var runtimeEndpoint = Environment.GetEnvironmentVariable("RUNTIME_ENDPOINT")
            ?? throw new InvalidOperationException("RUNTIME_ENDPOINT not set");
        
        var localPort = int.Parse(
            Environment.GetEnvironmentVariable("SIDECAR_PORT") ?? "50051");
        
        // 2. Initialize context from environment
        _contextManager.Initialize();
        
        // 3. Connect to Runtime with auth
        _runtimeChannel = GrpcChannel.ForAddress(
            runtimeEndpoint,
            new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler
                {
                    EnableMultipleHttp2Connections = true
                }
            });
        
        // 4. Start local gRPC server for Worker
        _localServer = new Server
        {
            Services = { FunctionRpc.BindService(new ProxyService(this)) },
            Ports = { new ServerPort("localhost", localPort, ServerCredentials.Insecure) }
        };
        
        _localServer.Start();
        
        _logger.LogInformation(
            "Worker Sidecar started. Local: localhost:{LocalPort}, Runtime: {RuntimeEndpoint}",
            localPort,
            runtimeEndpoint);
    }
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _localServer.ShutdownAsync();
        _runtimeChannel.Dispose();
    }
}
```

### Proxy Service Implementation

```csharp
public class ProxyService : FunctionRpc.FunctionRpcBase
{
    private readonly WorkerSidecar _sidecar;
    private readonly ILogger _logger;
    
    public override async Task EventStream(
        IAsyncStreamReader<StreamingMessage> workerStream,
        IServerStreamWriter<StreamingMessage> workerResponseStream,
        ServerCallContext context)
    {
        // Create authenticated channel to Runtime
        using var runtimeCall = _sidecar.CreateRuntimeStream();
        
        // Bidirectional proxy tasks
        var upstreamTask = ProxyUpstreamAsync(
            workerStream, 
            runtimeCall.RequestStream,
            context.CancellationToken);
        
        var downstreamTask = ProxyDownstreamAsync(
            runtimeCall.ResponseStream,
            workerResponseStream,
            context.CancellationToken);
        
        // Wait for either direction to complete
        await Task.WhenAny(upstreamTask, downstreamTask);
        
        // Cancel the other direction
        // (connection closed or error)
    }
    
    private async Task ProxyUpstreamAsync(
        IAsyncStreamReader<StreamingMessage> workerStream,
        IClientStreamWriter<StreamingMessage> runtimeStream,
        CancellationToken cancellationToken)
    {
        await foreach (var message in workerStream.ReadAllAsync(cancellationToken))
        {
            // Transform message (inject context)
            var transformed = _sidecar.TransformUpstream(message);
            
            _logger.LogTrace(
                "Upstream: {MessageType} (RequestId: {RequestId})",
                transformed.ContentCase,
                transformed.RequestId);
            
            // Forward to Runtime
            await runtimeStream.WriteAsync(transformed);
        }
        
        await runtimeStream.CompleteAsync();
    }
    
    private async Task ProxyDownstreamAsync(
        IAsyncStreamReader<StreamingMessage> runtimeStream,
        IServerStreamWriter<StreamingMessage> workerStream,
        CancellationToken cancellationToken)
    {
        await foreach (var message in runtimeStream.ReadAllAsync(cancellationToken))
        {
            // Transform message (update context on specialization)
            var transformed = _sidecar.TransformDownstream(message);
            
            _logger.LogTrace(
                "Downstream: {MessageType} (RequestId: {RequestId})",
                transformed.ContentCase,
                transformed.RequestId);
            
            // Forward to Worker
            await workerStream.WriteAsync(transformed);
        }
    }
}
```

### Health Check Integration

```csharp
public class SidecarHealthCheck : IHealthCheck
{
    private readonly WorkerSidecar _sidecar;
    
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, object>();
        
        // Check Runtime connection
        var runtimeConnected = await _sidecar.IsRuntimeConnectedAsync();
        data["runtime_connected"] = runtimeConnected;
        
        // Check Worker connection
        var workerConnected = _sidecar.IsWorkerConnected;
        data["worker_connected"] = workerConnected;
        
        // Check context
        var ctx = _sidecar.GetContext();
        data["application_id"] = ctx.ApplicationId;
        data["is_placeholder"] = ctx.IsPlaceholder;
        
        if (!runtimeConnected)
        {
            return HealthCheckResult.Unhealthy(
                "Not connected to Runtime",
                data: data);
        }
        
        if (!workerConnected)
        {
            return HealthCheckResult.Degraded(
                "Worker not connected",
                data: data);
        }
        
        return HealthCheckResult.Healthy("Sidecar healthy", data: data);
    }
}
```

---

## Deployment Patterns

### Pattern 1: Sidecar Container (Kubernetes)

```yaml
apiVersion: v1
kind: Pod
metadata:
  name: functions-worker
spec:
  containers:
  # Main worker container
  - name: worker
    image: mcr.microsoft.com/azure-functions/python:4.0
    env:
    - name: FUNCTIONS_WORKER_RUNTIME
      value: "python"
    - name: AzureFunctionsWebHost__hostId
      value: "worker-sidecar"
    # Worker connects to sidecar, not runtime directly
    - name: FUNCTIONS_GRPC_HOST
      value: "localhost"
    - name: FUNCTIONS_GRPC_PORT
      value: "50051"
    
  # Sidecar container
  - name: sidecar
    image: mcr.microsoft.com/azure-functions/worker-sidecar:1.0
    ports:
    - containerPort: 50051  # Local gRPC for worker
    env:
    - name: RUNTIME_ENDPOINT
      value: "https://runtime.functions.internal:443"
    - name: SIDECAR_PORT
      value: "50051"
    - name: WORKER_AUTH_TOKEN
      valueFrom:
        secretKeyRef:
          name: worker-auth
          key: token
    - name: APPLICATION_ID
      valueFrom:
        fieldRef:
          fieldPath: metadata.labels['app-id']
    - name: METADATA_VERSION
      value: "1.0.0"
    - name: CODE_VERSION
      value: "1.0.0"
    - name: IS_PLACEHOLDER
      value: "true"
```

### Pattern 2: Init Container + Shared Process (Azure Container Apps)

```yaml
# For platforms without true sidecars
containers:
- name: worker-with-sidecar
  image: mcr.microsoft.com/azure-functions/python-with-sidecar:4.0
  # Combined image with sidecar baked in
  env:
  - name: RUNTIME_ENDPOINT
    value: "https://runtime.functions.internal:443"
  # Sidecar starts first, then worker
```

### Pattern 3: Network Proxy (Service Mesh)

```yaml
# Using service mesh (Istio/Linkerd) for some sidecar functions
# Sidecar still needed for message transformation
apiVersion: networking.istio.io/v1beta1
kind: VirtualService
metadata:
  name: worker-to-runtime
spec:
  hosts:
  - runtime.functions.internal
  http:
  - route:
    - destination:
        host: runtime-service
        port:
          number: 443
---
# Worker Sidecar still handles auth and transformation
# but can rely on mesh for mTLS
```

---

## Open Questions

### 1. Sidecar Image Management

**Question**: How is the sidecar image versioned and deployed?

**Options**:
- A) Single sidecar image for all languages
- B) Language-specific sidecar images (python-sidecar, node-sidecar)
- C) Sidecar baked into each worker image

**Proposal**: Option A - Single sidecar image. Language-agnostic gRPC proxy doesn't need language-specific logic.

### 2. Sidecar-Worker Communication Security

**Question**: Should sidecar-to-worker communication (localhost) be encrypted?

**Options**:
- A) No encryption (same pod = trusted)
- B) Unix domain sockets (no network exposure)
- C) Local TLS for defense in depth

**Proposal**: Option B - Unix domain sockets provide security without TLS overhead.

### 3. Sidecar Failure Handling

**Question**: What happens if the sidecar crashes?

**Options**:
- A) Worker continues (can't communicate)
- B) Worker crashes (coupled lifecycle)
- C) Container orchestrator restarts sidecar

**Proposal**: Worker should detect sidecar disconnect and terminate itself (Option B behavior via health checks). Container orchestrator restarts both.

### 4. Token Refresh

**Question**: How are auth tokens refreshed for long-running workers?

**Options**:
- A) Long-lived tokens (hours/days)
- B) Sidecar refreshes tokens from metadata service
- C) Scale Controller pushes new tokens via ENV update

**Proposal**: Option B - Sidecar can refresh tokens using managed identity or metadata service, transparent to worker.

### 5. Protocol Evolution

**Question**: How to handle gRPC protocol changes between Runtime and Worker versions?

**Options**:
- A) Sidecar performs protocol translation
- B) Version negotiation in StartStream
- C) Require compatible versions

**Proposal**: Sidecar can perform basic protocol translation for backward compatibility, similar to how Azure Functions currently handles worker protocol versions.

### 6. Local Development Experience

**Question**: How do developers test locally without sidecar?

**Options**:
- A) Mock sidecar for local testing
- B) Direct connection mode (skip sidecar)
- C) Local sidecar that connects to local runtime

**Proposal**: Option B for simplicity - workers can connect directly to local runtime/emulator. Sidecar only in deployed environments.

### 7. Metrics and Tracing

**Question**: Should sidecar emit its own metrics/traces?

**Proposal**: Yes - Sidecar should emit:
- Message counts (upstream/downstream)
- Latency (added by proxy)
- Auth success/failure rates
- Connection status to Runtime

This provides visibility into the proxy layer without requiring worker changes.

### 8. Connection Multiplexing

**Question**: Should multiple workers share a sidecar?

**Options**:
- A) 1:1 sidecar per worker (simpler, isolated)
- B) 1:N sidecar per pod (fewer resources)
- C) Shared sidecar service (pool)

**Proposal**: Option A for isolation and simplicity. Resource overhead of sidecar is minimal (stateless gRPC proxy).

---

## Summary

The Worker Sidecar pattern solves the challenge of worker-to-runtime communication in the decoupled architecture:

| Challenge | Solution |
|-----------|----------|
| Workers must send app context | Sidecar injects from environment |
| Authentication required | Sidecar handles token attachment |
| Existing workers unchanged | Sidecar is transparent proxy |
| Specialization context changes | Sidecar updates on ENV reload |
| Multi-tenancy isolation | Auth tokens enforce tenant boundaries |

This approach enables the new architecture while maintaining backward compatibility with existing language workers.

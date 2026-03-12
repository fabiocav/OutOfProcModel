# Compute Separation — Post-Preview / P2 Items

**Parent plan**: [`compute_separation.md`](./compute_separation.md)

These items are lower priority and are not required for the internal preview. They represent follow-on work once the core separated compute model (M1–M5) is proven.

---

## Extension Assembly Loading (non-bundle / dotnet-isolated)

### Problem

For non-bundle dotnet-isolated apps, the host loads extension assemblies from the `.azurefunctions` folder in the deployed payload. This folder contains:

- **`extensions.json`** (~1KB) — manifest listing extension names, startup type names, hint paths, and bindings
- **Extension DLLs** (10–50MB total) — the actual assemblies referenced by `extensions.json`

In the separated compute model, these files live exclusively in the worker's container. The host cannot read them from disk.

The host needs these assemblies during `ScriptStartupTypeLocator.GetExtensionsStartupTypesAsync()`, which runs during `IHostBuilder.Build()` — **before** the full channel handshake completes. Without them, the host cannot register trigger bindings (queue, timer, HTTP, etc.).

### Current loading chain

```
ScriptStartupTypeLocator.GetExtensionsStartupTypesAsync()
  → Utility.TryResolveExtensionsMetadataPath(rootScriptPath)
    → probes: {root}/bin/extensions.json
              {root}/extensions.json
              {root}/.azurefunctions/extensions.json
  → ParseExtensionsAsync(metadataFilePath)
    → reads extensions.json → ExtensionReference[]
  → For each ExtensionReference:
    → resolves HintPath relative to extensionsMetadataPath
    → FunctionAssemblyLoadContext.Shared.LoadFromAssemblyPath(path)
    → Type.GetType(typeName) → IWebJobsStartup type
```

Key files:
- `src/WebJobs.Script/DependencyInjection/ScriptStartupTypeLocator.cs` — startup type resolution
- `src/WebJobs.Script/Utility.cs:826` — `TryResolveExtensionsMetadataPath()`
- `src/WebJobs.Script/Models/ExtensionReference.cs` — extension manifest model
- `schemas/json/extensions.json` — JSON schema

### Design: Split manifest from assemblies, host-initiated streaming

**Constraints:**
1. **Host-initiated pull** — host decides when and whether to request assemblies
2. **First worker only** — subsequent workers use the same extensions; no re-transfer
3. **Manifest is small** — `extensions.json` can travel early in `StartStream`
4. **DLLs are large** — must be streamed, not bundled into a single message

#### Phase 1: Manifest in `StartStream`

`extensions.json` is small (~1KB) and needed early (during `Build()`). It travels in `StartStream` alongside host.json:

```protobuf
message StartStream {
  string worker_id = 2;
  string host_configuration_json = 3;
  string extensions_metadata_json = 4;  // extensions.json content
}
```

`FunctionRpcService` intercepts it and stores it in `ExternalWorkerExtensionsProvider` (WebHost singleton). Available before `Build()`.

#### Phase 2: Host requests DLLs via streaming messages

New proto messages added to the `StreamingMessage` oneof:

```protobuf
// Host → Worker: request specific assemblies (sent only to first worker)
message ExtensionAssembliesRequest {
  repeated string hint_paths = 1;  // from extensions.json; host knows exactly what it needs
}

// Worker → Host: one message per assembly, streamed over the bidirectional EventStream
message ExtensionAssemblyResponse {
  string hint_path = 1;
  bytes content = 2;
  bool is_last = 3;  // signals all assemblies have been sent
}
```

The host sends `ExtensionAssembliesRequest` only when:
- `ExternalWorkerExtensionsProvider.HasExtensions` is false (first worker)
- The manifest lists assemblies with non-empty hint paths

#### Phase 3: `ScriptStartupTypeLocator` blocks until assemblies arrive

Same blocking pattern as `HostJsonContentProvider`:

```csharp
// In ScriptStartupTypeLocator, external worker mode:
if (_extensionsProvider.HasExtensions)
{
    // Already have DLLs from a previous worker — use cached path
    extensionsMetadataPath = _extensionsProvider.GetExtensionsPath();
}
else if (_extensionsProvider.ExtensionsJson is not null)
{
    // Have manifest but need DLLs — request from worker and block
    _extensionsProvider.RequestAssemblies();
    extensionsMetadataPath = _extensionsProvider.WaitForExtensionsPath(timeout);
}
```

### New file: `ExternalWorkers/ExternalWorkerExtensionsProvider.cs`

```csharp
namespace Microsoft.Azure.WebJobs.Script.Grpc.ExternalWorkers
{
    /// <summary>
    /// WebHost-level singleton that manages extension assembly transfer from external workers.
    ///
    /// Lifecycle:
    /// - FunctionRpcService calls SetManifest() when the first worker's StartStream
    ///   includes extensions_metadata_json.
    /// - ScriptStartupTypeLocator calls WaitForExtensionsPath() during host Build(),
    ///   which blocks until all assemblies are materialized to a temp directory.
    /// - Only the first worker transfers assemblies. Subsequent workers skip.
    /// - On host restart, cached extensions are reused (no re-transfer).
    /// </summary>
    internal class ExternalWorkerExtensionsProvider
    {
        private readonly object _lock = new();
        private TaskCompletionSource<string> _tcs = new();
        private string _extensionsPath;
        private string _extensionsJson;
        private int _expectedCount;
        private int _receivedCount;
        private bool _hasExtensions;

        /// <summary>
        /// Called by FunctionRpcService when StartStream includes extensions.json.
        /// </summary>
        public void SetManifest(string extensionsJson)
        {
            if (_hasExtensions)
            {
                return; // already have them from a previous worker
            }

            lock (_lock)
            {
                _extensionsJson = extensionsJson;

                var refs = JsonSerializer.Deserialize<ExtensionReferences>(extensionsJson);
                _expectedCount = refs?.Extensions?
                    .Count(e => !string.IsNullOrEmpty(e.HintPath)) ?? 0;

                _extensionsPath = Path.Combine(
                    Path.GetTempPath(), "functions",
                    "ext-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(_extensionsPath);

                File.WriteAllText(
                    Path.Combine(_extensionsPath, "extensions.json"),
                    extensionsJson);

                if (_expectedCount == 0)
                {
                    _hasExtensions = true;
                    _tcs.TrySetResult(_extensionsPath);
                }
            }
        }

        /// <summary>
        /// Called when an ExtensionAssemblyResponse is received from the worker.
        /// </summary>
        public void OnAssemblyReceived(string hintPath, byte[] content)
        {
            lock (_lock)
            {
                string targetPath = Path.Combine(_extensionsPath, hintPath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath));
                File.WriteAllBytes(targetPath, content);

                if (++_receivedCount >= _expectedCount)
                {
                    _hasExtensions = true;
                    _tcs.TrySetResult(_extensionsPath);
                }
            }
        }

        /// <summary>True if a previous worker already provided all extensions.</summary>
        public bool HasExtensions => _hasExtensions;

        /// <summary>The cached extensions.json content (available after SetManifest).</summary>
        public string ExtensionsJson => _extensionsJson;

        /// <summary>
        /// Signals that the host needs DLLs — FunctionRpcService will send
        /// ExtensionAssembliesRequest on the active gRPC stream.
        /// </summary>
        public void RequestAssemblies()
        {
            // Sets a flag / event that FunctionRpcService's relay loop checks.
            // FunctionRpcService sends ExtensionAssembliesRequest with the hint_paths
            // parsed from the manifest.
        }

        /// <summary>
        /// Blocks until all extension assemblies are materialized to disk.
        /// Called by ScriptStartupTypeLocator during host Build().
        /// </summary>
        public string WaitForExtensionsPath(TimeSpan timeout)
        {
            if (_tcs.Task.Wait(timeout))
            {
                return _tcs.Task.Result;
            }

            throw new TimeoutException(
                "Extension assemblies were not received from external worker within the timeout.");
        }

        /// <summary>
        /// Returns the cached extensions path immediately (no blocking).
        /// Only valid when HasExtensions is true.
        /// </summary>
        public string GetExtensionsPath() => _extensionsPath;

        /// <summary>
        /// Reset for host restart. Keeps cached extensions — no re-transfer
        /// if the worker is still connected.
        /// </summary>
        public void Reset(bool clearCache = false)
        {
            lock (_lock)
            {
                if (clearCache)
                {
                    _hasExtensions = false;
                    _extensionsJson = null;
                    _receivedCount = 0;

                    if (_extensionsPath is not null && Directory.Exists(_extensionsPath))
                    {
                        Directory.Delete(_extensionsPath, recursive: true);
                    }

                    _extensionsPath = null;
                }

                _tcs = new TaskCompletionSource<string>();

                if (_hasExtensions && _extensionsPath is not null)
                {
                    _tcs.TrySetResult(_extensionsPath);
                }
            }
        }
    }
}
```

### Timeline: first worker with extensions

```
T4   ConfigureAppConfiguration
       → ExternalWorkerHostJsonConfigurationSource.Load() BLOCKS
T5   Worker connects → StartStream { host_json, extensions_json }
T6   FunctionRpcService intercepts:
       → HostJsonContentProvider.SetContent(host_json)
       → ExternalWorkerExtensionsProvider.SetManifest(extensions_json)
T7   Load() UNBLOCKS → host.json applied
T8   ConfigureServices
       → ScriptStartupTypeLocator.GetExtensionsStartupTypesAsync()
       → External worker mode: parses cached extensions.json
       → Calls RequestAssemblies() → FunctionRpcService sends ExtensionAssembliesRequest
       → BLOCKS on WaitForExtensionsPath()
           ↕ (worker streams DLLs back on gRPC)
T9   Worker sends ExtensionAssemblyResponse × N
T10  ExternalWorkerExtensionsProvider writes each DLL to temp dir
T11  Last assembly → _hasExtensions = true → signals TCS
T12  WaitForExtensionsPath() UNBLOCKS → returns temp dir
T13  FunctionAssemblyLoadContext.ResetSharedContext(tempPath)
T14  Startup types loaded → extensions registered in DI
T15  ScriptHost.Build() completes
```

### Timeline: subsequent workers

```
Worker N connects → StartStream { host_json, extensions_json }
  → FunctionRpcService:
    → HostJsonContentProvider.SetContent() — updates cache
    → ExternalWorkerExtensionsProvider.SetManifest() — already has extensions, returns
  → NO ExtensionAssembliesRequest sent
  → Channel handshake proceeds normally
  → Worker available for invocations
```

### Restart / reconnect scenarios

| Scenario | Behavior |
|---|---|
| ScriptHost restarts, worker still connected | `Reset()` keeps cache → `WaitForExtensionsPath()` returns immediately |
| Worker disconnects, same worker reconnects | Same extensions.json → `HasExtensions` true → no re-transfer |
| Different worker connects with different extensions | `Reset(clearCache: true)` → re-transfer from new worker |
| Host restart after worker disconnect | Blocked until new worker connects and provides extensions |

### Implementation notes

- **Cleanup**: Temp directories should be cleaned up in `ExternalWorkerExtensionsProvider.Dispose()` and on `Reset(clearCache: true)`.
- **Size limits**: For very large extension sets, the per-message streaming (one DLL per message) keeps memory usage bounded. The gRPC max message size (`int.MaxValue`) accommodates individual DLLs.
- **Security**: DLLs from untrusted sources — for preview, network-level trust is sufficient. Post-preview, consider signing verification.
- **Deduplication**: If the same DLL name/version is sent by a reconnecting worker, skip the write.

### Modified files

```
src/WebJobs.Script/DependencyInjection/ScriptStartupTypeLocator.cs  — external worker extensions path resolution
src/WebJobs.Script.Grpc/Server/FunctionRpcService.cs               — intercept extensions.json from StartStream;
                                                                       send ExtensionAssembliesRequest; receive responses
azure-functions-language-worker-protobuf/src/proto/FunctionRpc.proto — StartStream.extensions_metadata_json,
                                                                       ExtensionAssembliesRequest,
                                                                       ExtensionAssemblyResponse
```

### New files

```
src/WebJobs.Script.Grpc/ExternalWorkers/ExternalWorkerExtensionsProvider.cs
```

---

## Other Post-Preview Items

The following items from the main plan's "Out of Scope" section are candidates for future work:

- **Scale controller integration** — external workers as scale units
- **Sidecar / wrapper process** — optional process management layer
- **`WorkerConnect` consolidated protocol message** — single round-trip handshake replacing the multi-message init sequence
- **ApplicationId-based routing** — routing connections to the correct host instance
- **gRPC auth** — mutual TLS or token-based authentication for the gRPC channel
- **SquashFS mounting / package download** — alternative payload delivery mechanisms
- **Worker-side warmup** — sending `WorkerWarmupRequest` to external workers that connect during placeholder (requires coordination with the orchestrator to pre-connect workers)
- **Load balancing** — round-robin or weighted distribution of invocations across multiple connected workers (M5 placeholder in main plan)

# Worker Model Refactor - Executive Summary

This document provides a high-level overview of the proposed Azure Functions Worker Model refactor. For detailed design information, see the linked documents.

---

## Table of Contents

1. [Overview](#overview)
2. [Key Architectural Change](#key-architectural-change)
3. [Component Changes](#component-changes)
4. [Expected Benefits](#expected-benefits)
5. [Rollout Strategy](#rollout-strategy)
6. [Detailed Documentation](#detailed-documentation)

---

## Overview

The current Azure Functions architecture tightly couples the **Runtime** and **Worker** into a single container. This refactor **decouples** them, allowing:

- Multiple workers to connect to a single Runtime
- Independent scaling of workers vs. Runtime
- Shared Runtime infrastructure across customers
- Smaller, simpler worker container images

---

## Key Architectural Change

### Before: Coupled Model

```
┌─────────────────────────────────┐
│  Container                      │
│  ┌───────────────────────────┐  │
│  │  Runtime (Script Host)    │  │
│  │  ┌─────────────────────┐  │  │
│  │  │  Worker (Child)     │  │  │
│  │  └─────────────────────┘  │  │
│  └───────────────────────────┘  │
│  Scales as one unit             │
└─────────────────────────────────┘
```

### After: Decoupled Model

```
                                  ┌───────────────────────┐
                          ┌──────►│  Worker Container 1   │
                          │       └───────────────────────┘
┌───────────────────────┐ │       ┌───────────────────────┐
│  Runtime Container    │ │ gRPC  │  Worker Container 2   │
│  ┌─────────────────┐  │◄┼──────►│                       │
│  │    JobHost      │  │ │(net)  └───────────────────────┘
│  └─────────────────┘  │ │       ┌───────────────────────┐
└───────────────────────┘ └──────►│  Worker Container 3   │
                                  └───────────────────────┘
                                  (scale independently)
```

**Key Insight**: Runtime hosts multiple **JobHosts** (one per Function App), each managing triggers via the WebJobs SDK. Workers connect over the network and are managed by infrastructure, not the Runtime.

→ *See [SYSTEM_DESIGN.md](SYSTEM_DESIGN.md) for full architecture*

---

## Component Changes

### Runtime

| Aspect | Current | New |
|--------|---------|-----|
| **Worker relationship** | Starts and monitors child process | Workers connect via gRPC (no process control) |
| **Worker lifecycle** | Runtime manages | Infrastructure manages (mostly) |
| **Authentication** | None (same instance) | Token-based (workers prove identity) |
| **Language awareness** | Special handling per language | Language-agnostic (Runtime doesn't care) |

**New Concepts**:
- **JobHostManager**: Manages multiple JobHosts, handles worker routing and specialization
- **ApplicationDefinition**: Identifies apps with `ApplicationId`, `MetadataVersion`, `CodeVersion`
- **Unified routing**: JobHostManager owns both JobHost lifecycle and worker-to-JobHost message routing

→ *See [RUNTIME_DESIGN.md](RUNTIME_DESIGN.md) for details*

---

### Worker

| Aspect | Current | New |
|--------|---------|-----|
| **Container contents** | .NET Runtime + Host + Language Worker | Language Worker + Sidecar only |
| **Connection** | localhost gRPC | Network gRPC to remote Runtime |
| **Process management** | Host starts/stops worker | Wrapper process + Sidecar coordination |
| **Authentication** | Not needed | Sidecar handles token injection |
| **Context** | Host reads ENV directly | Sidecar injects app context into messages |

**New Concepts**:
- **Worker Sidecar**: Transparent gRPC proxy handling auth and context injection
- **Worker Wrapper**: Lightweight supervisor that restarts worker when config changes
- **Simplified images**: Non-.NET workers no longer need .NET Runtime (44-58% smaller)

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Worker Pod                                                                 │
│  ┌──────────────────────────────────┐    ┌───────────────────────────────┐ │
│  │  Worker Container                │    │  Sidecar Container            │ │
│  ├──────────────────────────────────┤    ├───────────────────────────────┤ │
│  │                                  │    │                               │ │
│  │  ┌────────────────────────────┐  │    │  • Auth token injection       │ │
│  │  │ Wrapper                    │◄─┼────┼─ • Restart coordination ──────┤ │
│  │  │ Manages process lifecycle  │  │    │  • App context transform      │ │
│  │  └─────────────┬──────────────┘  │    │  • gRPC proxy                 │ │
│  │                │                 │    │                               │ │
│  │  ┌─────────────▼──────────────┐  │    │                               │ │
│  │  │ Worker Process             │◄─┼───►│                               │ │
│  │  │ (Python, Node, Java, etc.) │  │    │                               │ │
│  │  │ No .NET needed!            │  │    │                               │ │
│  │  └────────────────────────────┘  │    │                               │ │
│  └──────────────────────────────────┘    └───────────────┬───────────────┘ │
└──────────────────────────────────────────────────────────┼─────────────────┘
                                                           │
                                                      gRPC ▲ (network)
                                                           │
                                                           ▼
                                                ┌───────────────────────┐
                                                │  Runtime Container    │
                                                └───────────────────────┘
```

→ *See [WORKER_DESIGN.md](WORKER_DESIGN.md) for details*

---

### Scale Controller

| Aspect | Current | New |
|--------|---------|-----|
| **Unit of scaling** | Runtime + Worker together | Workers independently from Runtime |
| **Placeholder setup** | Single container | Start Runtime, then attach Workers |
| **Specialization** | In-place (same container) | Worker transfers to Customer JobHost |
| **Scale-out (1→N)** | Add more containers | Add workers to existing Runtime, or new Runtime |

**New Concepts**:
- **0→1 Scale-out**: Specialize placeholder worker in-place
- **1→N Scale-out**: Assign workers from different Runtime (worker reassignment)
- **Abandoned workers**: After specialization, remaining placeholder workers can be reclaimed

→ *See [SCALE_CONTROLLER_DESIGN.md](SCALE_CONTROLLER_DESIGN.md) for details*

---

### Versioning Model

Applications are identified by three version components:

| Version | Purpose | Change Impact |
|---------|---------|---------------|
| **ApplicationId** | Unique app identifier | Always new JobHost |
| **MetadataVersion** | Triggers, bindings, function signatures | New JobHost required |
| **CodeVersion** | Customer code (implementation) | Same JobHost, blue/green deployment |

**Key Insight**: `MetadataVersion` changes require a new JobHost (triggers must restart). `CodeVersion` changes can use the same JobHost (enables zero-downtime deployments).

→ *See [RUNTIME_DESIGN.md#application-definition-versioning](RUNTIME_DESIGN.md) for details*

---

## Expected Benefits

| Benefit | Improvement | Details |
|---------|-------------|---------|
| **Memory reduction** | 60-75% | Shared Runtime amortizes overhead |
| **Container density** | 3-4x | More apps per node |
| **Image size** | 44-58% smaller | Workers don't need .NET |
| **Compute efficiency** | Significant at scale | Workers scale without duplicating Runtime overhead |
| **Partition scaling** | Unlimited | Fan-out beyond partition count |
| **Future-ready** | Blue/green, canary | Versioning model enables zero-downtime deployments |
| **Future-ready** | Multi-tenant Runtimes | Architecture enables sharing Runtime across customers |

### Partition Fan-Out Example

Current model: 8 Event Hub partitions = max 8 workers
New model: 8 partitions → 1 Runtime → 32+ workers (4x throughput)

→ *See [GOALS.md](GOALS.md) for detailed benefits analysis*

---

## Rollout Strategy

### Versioned SKUs

Introduce **v2 SKUs** (e.g., `EP1v2`, `Y1v2`) that use the new model:

| v1 SKUs (Current) | v2 SKUs (New) |
|-------------------|---------------|
| Coupled model | Decoupled model |
| Full backward compat | Some features deprecated |
| Existing apps | New apps default here |

### Deprecated in v2

- `IWebJobsConfigurationStartup` worker overrides (use App Settings)
- Custom worker arguments via code
- `FUNCTIONS_WORKER_PROCESS_COUNT` (platform-managed)
- In-process .NET model

### Rollout Phases

1. **Preview** (Month 1-3): v2 opt-in only
2. **GA** (Month 4-6): New apps default to v2
3. **Migration** (Month 7-12): Auto-migrate compatible v1 apps
4. **Deprecation** (Month 13-18): v1 maintenance mode
5. **EOL** (Month 24+): v1 retired

**Goal**: 95% of customers migrate transparently with no action required.

→ *See [GOALS.md#rollout-strategy](GOALS.md) for full rollout plan*

---

## Detailed Documentation

| Document | Description |
|----------|-------------|
| [GOALS.md](GOALS.md) | Motivation, benefits analysis, rollout strategy |
| [SYSTEM_DESIGN.md](SYSTEM_DESIGN.md) | Overall system architecture, failure handling |
| [RUNTIME_DESIGN.md](RUNTIME_DESIGN.md) | Runtime internals, JobHostManager, channel routing |
| [WORKER_DESIGN.md](WORKER_DESIGN.md) | Worker Sidecar, wrapper process, lifecycle management |
| [SCALE_CONTROLLER_DESIGN.md](SCALE_CONTROLLER_DESIGN.md) | Placeholder setup, specialization, 0→1 vs 1→N |
| [WORKER_INITIALIZATION_FLOW.md](WORKER_INITIALIZATION_FLOW.md) | Worker startup sequence diagrams |
| [PLACEHOLDER_SPECIALIZATION_FLOW.md](PLACEHOLDER_SPECIALIZATION_FLOW.md) | Specialization workflow |

---

## Open Questions Summary

Key decisions still to be made:

| Question | Options | Document |
|----------|---------|----------|
| Runtime:Worker ratio | 10-20 workers per Runtime? | [SCALE_CONTROLLER_DESIGN.md](SCALE_CONTROLLER_DESIGN.md) |
| Abandoned workers | Re-assign, reclaim, or terminate? | [SCALE_CONTROLLER_DESIGN.md](SCALE_CONTROLLER_DESIGN.md) |
| SKU naming | `v2`, `-flex`, `-next`? | [GOALS.md](GOALS.md) |
| Worker restart | Wrapper in-place vs container restart? | [WORKER_DESIGN.md](WORKER_DESIGN.md) |
| Mixed vs dedicated Runtimes | Share Runtime across customers? | [SCALE_CONTROLLER_DESIGN.md](SCALE_CONTROLLER_DESIGN.md) |

---

## Quick Reference

### New gRPC Messages

| Message | Direction | Purpose |
|---------|-----------|---------|
| `WorkerContext` | Worker → Runtime | App identity, auth token |
| `WorkerRestartRequest` | Runtime → Sidecar | Restart worker with new config |
| `WorkerSpecializedNotification` | Runtime → Worker | Specialization complete |

### Key Configuration

```json
// host.json (new options)
{
  "extensions": {
    "workerModel": {
      "maxWorkersPerRuntime": 20,
      "minWorkersPerRuntime": 1,
      "workerAffinityMode": "none"
    }
  }
}
```

### Environment Variables (Unchanged)

Workers continue to use existing environment variables:
- `FUNCTIONS_WORKER_RUNTIME`
- `FUNCTIONS_WORKER_RUNTIME_VERSION`
- `AzureWebJobsStorage`
- Connection strings

The Sidecar reads these and injects them into gRPC messages for the Runtime.

# Roadmap — что делаем дальше

> Активная карта работ. **Канон поведения:** main OpenSpec
> [`openspec/specs/`](../openspec/specs/). Planning history:
> [`openspec/changes/archive/2026-08-14-architecture-runtime-core/`](../openspec/changes/archive/2026-08-14-architecture-runtime-core/).

## Текущий статус (2026-08-15)

| | |
|---|---|
| Architecture specs | ✅ archived → `openspec/specs/*` (15 capabilities) |
| MVP runtime | ✅ Pregel + InMemory + HITL + stream + DI + Testing + Generators |
| Samples | ✅ HelloWorld · InterruptResume · AotSmoke · ReviewBot · DocQ · MarketingAgent · MockAdMcp · UiHost · StudioHost · WorkerHost |
| Benchmarks | ✅ `benchmarks/Voluta.Benchmarks` (#10 closed) |
| Send / subgraph | ✅ `Send`, `ContinueWithSends`, `Subgraph.AsNode`, `Describe()` |
| Checkpointers | ✅ File · Sqlite · EF Core · S3 · Postgres (#71) |
| Agents.AI | ✅ `IGraphNode` adapters for MAF `AIAgent` + MEAI `IChatClient` |
| Graph DI | ✅ `GraphContext.Services`, `IGraphNode`, `AddNode<T>` |
| Studio API v1 | ✅ `MapStudioApi` `/api/v1` (resume/continue/update/fork/SSE/hitl, опц. API-key) (#69, #80) |
| Studio SPA | ✅ React 19 + Fluent v9 в `Voluta.UI` (`{prefix}/studio`, embedded wwwroot/studio) (#68, #86) |
| UI legacy shell | ✅ `MapVolutaUI` (inspector / HITL / topology) — остаётся как thin shell |
| Tools / MCP | ✅ `Voluta.Tools` — tool nodes + light MCP HTTP client (#72, #83) |
| Wake bus / Hosting | ✅ `Voluta.Hosting` — `IThreadWakeBus` + `GraphWorkerService` presets (#73 partial → #84) |
| Observability | ✅ `Voluta.OpenTelemetry` + Grafana dashboard pack (#74 → #77, #85) |
| Cross-thread Store | ✅ `IVolutaStore` + `InMemoryVolutaStore` (#75 partial → #78) |
| Templates | ✅ `templates/Voluta.Templates` — `dotnet new voluta-agent` (#70 → #81) |
| PublicAPI ship gate | ✅ `PublicAPI.{Shipped,Unshipped}.txt` + PublicApiAnalyzers on ship packages |
| NuGet | ✅ `v0.1.0` / `v0.1.1` / `v0.2.0` / `v0.3.0` / `v0.3.1` / `v0.4.0` |
| Arch tests (package isolation) | ✅ `tests/Voluta.Architecture.Unit` |
| OpenSpec main specs | ✅ synced post-MVP (public-api-hosting · checkpoint · quality-engineering · studio-host) |

## Shipped package map

| Package | Role | Tier |
|---------|------|------|
| `Voluta.Abstractions` | contracts | AOT |
| `Voluta` | runtime + InMemory + Subgraph | AOT |
| `Voluta.DependencyInjection` | `AddVoluta` | AOT |
| `Voluta.Testing` | doubles + conformance | full |
| `Voluta.Generators` | `[GraphState]` | full (analyzer) |
| `Voluta.Checkpoints.File` | JSON file store | full |
| `Voluta.Checkpoints.Sqlite` | SQLite single-file store | full |
| `Voluta.Checkpoints.Postgres` | Postgres-native Npgsql store | full |
| `Voluta.Checkpoints.EntityFrameworkCore` | provider-agnostic EF Core store | full |
| `Voluta.Checkpoints.S3` | AWS S3 / S3-compatible store | full |
| `Voluta.Agents.AI` | MAF + MEAI as `IGraphNode` | full |
| `Voluta.UI` | ops console: Studio SPA + legacy shell + `/api/v1` | full (ASP.NET) |
| `Voluta.Hosting` | wake bus + `GraphWorkerService` presets | full (worker host) |
| `Voluta.Tools` | tool nodes + light MCP HTTP client | full |
| `Voluta.OpenTelemetry` | `AddVolutaInstrumentation()` | full |

## Ближайшие шаги

1. docs.stbl.space sync to 0.2.x (#43) — сайт вне репо, этот репо держит README актуальным
2. Engine parity #75 remainder — subgraph stream namespaces · task journal A3 · durable Store · checkpoint migration
3. v0.3 · Studio + DX milestone close-out (SPA #68 ✅, API #69 ✅, template #70 ✅, Postgres #71 ✅)
4. v0.4 · Tools + scale remainder — Redis/NATS checkpointers (#76 Azure Blob part), task journal

## Shipped recently

| Item | Notes |
|------|--------|
| Studio SPA (#68) | React 19 + Fluent v9, embedded в `Voluta.UI`, `{prefix}/studio`, Ctrl+K palette, graph overlay, SSE panel v2, history diff |
| Studio API v1 (#69) | `MapStudioApi` `/api/v1` + GraphException→404/409/400 mapping |
| Cross-thread Store | `IVolutaStore` + `InMemoryVolutaStore` + DI (D-032 / #75 partial) |

## Не в планах

- Свой LLM SDK · замена MAF · Python-порт · LangSmith clone

## Связанное

- [decisions.md](./decisions.md)
- [conventions.md](./conventions.md)
- Epic #1 · UI #13 · backlog #8

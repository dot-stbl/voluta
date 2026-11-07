# Decisions — принятые решения по Voluta

> Хронологический лог решений. Каждое решение фиксируется здесь **до** того,
> как превращается в код. Канон требований: main OpenSpec
> [`openspec/specs/`](../openspec/specs/). Planning archive:
> [`openspec/changes/archive/2026-08-14-architecture-runtime-core/`](../openspec/changes/archive/2026-08-14-architecture-runtime-core/).
> ADR — в `.agents/adr/` для нетривиальных обоснований.

## Решения

### D-001 — Имя бренда: **Stemma** (лат. «гирлянда, родословная») — ОТМЕНЕНО

**Решение:** `Stemma`. Метафора линии состояний через чекпойнты.
**Статус:** отменено [D-024](#d-024--переименование-в-voluta). Текст сохранён как история —
имя действительно значило «гирлянда, родословная», и переписывать это задним числом нельзя.

### D-002 — Имя первого продукта: **StemmaGraph** — ОТМЕНЕНО

**Решение:** `StemmaGraph` (конкатенация, зеркало LangGraph).
**Статус:** отменено [D-024](#d-024--переименование-в-voluta) — именно мотив «зеркало LangGraph»
и оказался проблемой, а не преимуществом.

### D-003 — GitHub-репо: `dot-stbl/stemma.graph` — ПЕРЕИМЕНОВАНО

**Решение:** `dot-stbl/stemma.graph`; бренд Stemma независим.
**Статус:** репо переименовано в `dot-stbl/voluta` по [D-024](#d-024--переименование-в-voluta);
GitHub держит редирект со старого пути.

### D-004 — NuGet package ids: `Voluta.*`

**Решение:** `Voluta`, `Voluta.Abstractions`, … (не `Voluta.Graph.*`).

### D-005 — Подход: **не порт LangGraph**, .NET-native переосмысление

**Решение:** концепции (граф, cycles, channels, checkpoint, interrupt, stream);
API — generics, `IAsyncEnumerable`, без TypedDict-рефлексии hot path.

### D-006 — MVP scope: **honest Pregel + InMemory C-checkpoint** *(supersedes scaffold note)*

**Было (scaffold):** MVP без checkpointing.

**Решение (2026-08-13, architecture pass):** MVP 0.1 = full Pregel superstep
(all ready nodes, barrier, versions) + channels (LastValue, Append) +
**C-shape checkpoint** + **InMemory provider in core** + HITL (`NodeResult`) +
multi-mode stream + source-gen skeleton + `Voluta.Testing`.

**Не в 0.1:** Send execution, subgraphs, EF/S3/File packages, UI, MicrosoftAi.

**Почему изменили:** product harness / backend agents без durable pause/resume
и multi-writer merge — не library. Sequential-only B откладывал C-shape и
ломал forward-compat.

### D-007 — MAF / Microsoft.Extensions.AI: через `IChatClient`

**Решение:** не свой LLM SDK. Пакет интеграции — **отдельный**
`Voluta.MicrosoftAi` (post-0.1), не в core.

### D-008 — Технологический стек

| Слой | Технология |
|------|------------|
| Runtime | .NET 10 |
| Тесты | xUnit + Shouldly + NSubstitute + Bogus |
| AI (later) | `Microsoft.Extensions.AI` |
| Streaming | `IAsyncEnumerable<T>` |
| Bench (planned) | BenchmarkDotNet |
| Публикация | NuGet OIDC trusted publishing |

### D-009 — CI/CD

PR: build (warnings-as-errors) + unit (skip Integration). Planned: pack smoke,
openspec validate, expanded path filters. Release: tag `v*.*.*` → pack → nuget.org.

### D-010 — Пакеты (target map)

```
# AOT core tier
Voluta.Abstractions
Voluta                         # runtime + InMemory (no ME.DI reference)
Voluta.DependencyInjection     # AddVoluta for IServiceCollection

# Full .NET / ASP.NET tier (not AOT-claimed)
Voluta.OpenTelemetry           # AddVolutaInstrumentation (OTel SDK)
Voluta.Testing                 # doubles + conformance (pack carefully)
Voluta.Checkpoints.EntityFrameworkCore   # later
Voluta.Checkpoints.S3                    # later
Voluta.Checkpoints.File                  # later
Voluta.MicrosoftAi                       # later
Voluta.UI.*                              # later (#13)
```

Scaffold ships only Abstractions + Voluta markers until apply.

### D-011 — Docs site: `dot-stbl/voluta-docs`

Отдельный репо; движок/домен — после MVP.

### D-012 — Execution: **full Pregel (C)**

All ready tasks per superstep → barrier → `apply_writes` with channel versions /
`versions_seen`. Not sequential-only B.

### D-013 — State: **channels + reducers**

Named channels; LastValue (single writer/step); Append/binop multi-writer.
Typed `TState` is a view. DX: **source-gen primary** + fluent canon + reflection
fallback at compile-graph (not hot path).

### D-014 — Checkpoint: **full C-shape** + pluggable providers

Snapshot: channel values, versions, versions_seen, pending writes, step, status,
interrupt. InMemory in core; EF/S3/File separate packages. Storage ≠ “files on
disk only”.

### D-015 — HITL: **NodeResult**, not control-flow exceptions

`Continue(writes)` / `Interrupt(payload)`; `ResumeAsync(threadId, Command)`.

### D-016 — Streaming: multi-mode primary API

`values` / `updates` / `events`; `InvokeAsync` drains to terminal.

### D-017 — Hosting: **Both**

Standalone `CompiledGraph` (core) + DI via **`Voluta.DependencyInjection`**
(`AddVoluta`). Core has **zero** `Microsoft.Extensions.DependencyInjection`
package reference.

### D-018 — Send / subgraphs: designed, not MVP-shipped

Specs reserve PUSH/Send and subgraph composition; implement post-0.1.

### D-019 — Testing: **`Voluta.Testing`** + conformance

Recording/fault-injecting checkpointer, stream capture, fixtures, InMemory
conformance suite in CI. Quality-engineering: unit scenario matrix, BenchmarkDotNet
(non-gating PR), CI pack/openspec.

### D-020 — UI: separate package, post-0.1

`Voluta.UI` — run inspector, HITL queue, topology (MD3). Core must not
reference UI. Epic: github.com/dot-stbl/voluta/issues/13. Mockups:
local artifacts store (not required in repo for 0.1).

### D-021 — Architecture source of truth: OpenSpec **main specs**

**Update (2026-08-14):** Change `architecture-runtime-core` **archived** as
`openspec/changes/archive/2026-08-14-architecture-runtime-core/`. Canonical
behavior lives in **`openspec/specs/<capability>/spec.md`** (12 capabilities).
Validate: `openspec validate --specs --strict`.

### D-022 — Two runtime tiers: **AOT core** vs **full .NET host packages**

**Decision:** Split the product surface by publish model — no core rewrite.

| Tier | Packages | Target |
|------|----------|--------|
| **AOT core** | `Voluta`, `Voluta.Abstractions`, `Voluta.DependencyInjection` | `IsAotCompatible` + trim analyzer; smoke `samples/AotSmoke` (`PublishAot`) |
| **Full runtime** | `Voluta.Checkpoints.File`, `Voluta.UI`, `Voluta.Agents.AI`, Testing, Generators | Regular .NET / ASP.NET; may use reflection, JSON, browsers, etc. **Do not** claim AOT |

**AOT path (supported):** fluent `StateGraph` + InMemory (+ optional thin DI).  
**Full path (product hosts):** ASP.NET, file/EF checkpointers, UI console, LLM adapters — depend on core, run on complete CLR.

Checkpoint packages that need JSON must use STJ source-gen (or non-reflection serde) **if** they ever want AOT; until then they stay full-runtime only.

### D-023 — Post-MVP packages shipped on main (before NuGet 0.1)

**Decision (2026-08-14):** After MVP runtime, ship on `main` without waiting for
the NuGet tag:

| Item | Package / API |
|------|----------------|
| Send fan-out | `Send`, `NodeResult.ContinueWithSends`, `PendingSends`, engine PUSH tasks |
| Subgraph helper | `Subgraph.AsNode` |
| Topology export | `CompiledGraph.Describe()` → `GraphDescription` |
| File checkpointer | `Voluta.Checkpoints.File` |
| Agents.AI | `Voluta.Agents.AI` (MAF + MEAI nodes; replaces MicrosoftAi) |
| UI | `Voluta.UI` Razor RCL (`AddVolutaUI` / `MapVolutaUI` + SSE) — see D-025 |
| Harness samples | `ReviewBot`, `DocQ`, `MarketingAgent` + `MockAdMcp` + `Voluta.Samples.Shared`; UI host `UiHost` |
| Benchmarks | `benchmarks/Voluta.Benchmarks` |

Still deferred: NuGet `v0.1.0` (PublicAPI surface tracked — D-026), arch tests.
EF/S3 checkpointers: see D-027.

### D-024 — Переименование в **Voluta**

**Решение (2026-08-14):** бренд и продукт — `Voluta`. Отменяет D-001 и D-002, переименовывает
репо из D-003. Схема пакетов остаётся плоской по D-004: `Voluta`, `Voluta.Abstractions`, …

**Почему:**

1. **Паттерн «одно брендовое слово» вместо «имя + категория».** `StemmaGraph` зеркалил
   `LangGraph` (мотив D-002) и тем самым навсегда ставил проект в его тень, подрезая собственную
   позицию «не порт, мы вровень».
2. **У `Stemma` два живых омонима в dev-выдаче:** Stemma — data-каталог (деньги Sequoia, куплен
   Teradata) и Adafruit **STEMMA / STEMMA QT** — стандарт разъёма. На NuGet имя `Stemma` было
   свободно: коллизии нашлись только поиском, реестр их не показывал.
3. **`voluta` ← лат. `volvere` «вращать»** — цикл назван в самом имени. Для рантайма из цикличных
   супершагов это точнее, чем метафора родословной. В выдаче — только архитектурный завиток и род
   морских улиток, ни одного продукта. Свободно на NuGet, npm, crates.

**Окно:** 0.1 не затегирован и в NuGet ничего не опубликовано, поэтому цена переименования —
find/replace (793 вхождения в 166 файлах), а не deprecation старых пакетов + redirect-пакет.

**Что изменилось:** неймспейсы и package ids `StemmaGraph.*` → `Voluta.*`; солюшен
`stemma.graph.slnx` → `voluta.slnx`; коммит-префикс `[stemma]` → `[voluta]`; баннер —
`assets/banner-voluta.png` (старый `banner.png` оставлен как история). Условия в
`Directory.Build.props`, завязанные на литеральные имена проектов (AOT-ярус и выбор иконки),
обновлены вместе с именами — иначе они молча перестали бы срабатывать.

### D-025 — UI package: **Razor RCL + SSE + MapVolutaUI options**

**Решение (2026-08-14, issue #14):** `Voluta.UI` is a **Razor Class Library**
(`Microsoft.NET.Sdk.Razor` + `FrameworkReference Microsoft.AspNetCore.App`), not a
thin embedded-HTML-only package. Host integration stays Swagger-style:

```csharp
builder.Services.AddVolutaUI(session);
app.MapVolutaUI(o => o.PathPrefix = "/voluta"); // default /voluta
```

**Live stream:** Server-Sent Events from `IAsyncEnumerable<StreamEvent>`
(`GET {prefix}/api/threads/{threadId}/stream`) — **not** WebSocket / SignalR /
Blazor Interactive Server as the default. Cancel on client disconnect.

**UI chrome:** MD3 dark-ish tokens + Material Symbols; modular static shell
(inspector / HITL / topology). No MudBlazor, no Vite/React monorepo in this package.

**Isolation:** `Voluta` must not reference `Voluta.UI` (full-runtime host package only).

**Sample:** `samples/UiHost` — `dotnet run` → `http://localhost:5188/voluta`.

**Out of scope (still):** Interactive Server circuit default, multi-graph host,
auth, full graph canvas editor.

### D-026 — PublicAPI ship gate (v0.1 freeze prep)

**Решение (2026-08-14):** packable product libraries track public surface with
`Microsoft.CodeAnalysis.PublicApiAnalyzers` + `PublicAPI.Shipped.txt` /
`PublicAPI.Unshipped.txt` (wired in `Directory.Build.props` for
`Voluta`, `Voluta.Abstractions`, `Voluta.DependencyInjection`,
`Voluta.Checkpoints.File`, `Voluta.Agents.AI`, `Voluta.UI`).

- **Shipped** empty until first NuGet tag — all current surface is **Unshipped**.
- After `v0.1.0` publish: move Unshipped → Shipped; further API changes must
  land in Unshipped (or break intentionally with review).
- **Not tracked:** `Voluta.Testing` (`IsPackable=false`), `Voluta.Generators`
  (Roslyn analyzer package).
- **API hygiene while enabling:** `CompiledGraph.ResumeAsync` overloads split
  so only the full `(threadId, command, streamMode, cancellationToken)` form
  uses optional parameters; `MapVolutaUI` optional-configure overload replaced
  with required `Action<VolutaUiOptions>` + parameterless default (RS0026/RS0027).

### D-027 — EF Core + S3 checkpointer packages

**Decision (2026-08-14):** Ship two additional `ICheckpointer` providers:

| Package | Notes |
|---------|--------|
| `Voluta.Checkpoints.EntityFrameworkCore` | Provider-agnostic: depends on `Microsoft.EntityFrameworkCore` + Relational only. Consumers register Npgsql/SqlServer/SQLite themselves. Table `voluta_checkpoints`, composite key `(thread_id, step)`, full C-shape JSON in `payload_json`. Prefer `IDbContextFactory<VolutaCheckpointDbContext>`. Schema/migrations owned by the consumer (`EnsureCreated` OK for tests). |
| `Voluta.Checkpoints.S3` | `AWSSDK.S3`; key layout `{prefix}/{safeThreadId}/{step:D12}.json`. Same STJ C-shape wire as File. |

Both pass `CheckpointerConformance.RunAllAsync`. PublicAPI ship gate enabled. Same polymorphic JSON limits as File (D-023 open serde question).

### D-028 — Graph DI + Voluta.Agents.AI (native MEAI/MAF)

**Decision (2026-08-14):** Prefer **instance / DI composition** over extension-method glue.

| Piece | API |
|-------|-----|
| Core A | `CompileOptions.Services` → `GraphContext.Services` / `GetRequiredService<T>()` |
| Core B | `IGraphNode`, `StateGraph.AddNode<T>()`, `AddNode(IGraphNode)`, `AddNode(sp => IGraphNode)` |
| Package C | **`Voluta.Agents.AI`** — `AgentGraphNode` / `ChatClientGraphNode` + static `AgentNodes` / `ChatClientNodes` (not `this StateGraph` extensions) |
| Removed | **`Voluta.MicrosoftAi`** — static `ChatClientNode` helpers deleted; one AI package only |

Host pattern: `Compile(checkpointer, new CompileOptions { Services = sp })` inside `AddVoluta` graph factory. MAF package deps: `Microsoft.Agents.AI.Abstractions` 1.17 + `Microsoft.Extensions.AI.Abstractions` 10.9. Core stays AOT and AI-free.

### D-029 — Checkpoint wire-format version field

**Decision (2026-08-14):** File / EF / S3 checkpoint JSON documents carry
`formatVersion` (camelCase via `JsonSerializerOptions.Web`). Current value is
**1**. Missing field deserializes as **1** (backward-compatible with existing
on-disk / in-DB / S3 objects). Unsupported future versions throw
`CheckpointStoreException` with code `checkpoint.unsupported_format_version`.

Wire constant is **duplicated** per provider (`CheckpointWireFormat.Version`) —
no shared package, keeps checkpoint package isolation. Writes always stamp
version 1 (domain `CheckpointSnapshot.FormatVersion` is not trusted as wire
schema). InMemory stays process-local (no JSON wire).

### D-030 — Checkpoint wire v1 value allow-list (fail-loud)

**Decision (2026-08-14):** Durable providers (File / EF / S3) enforce a
**conservative allow-list** for channel values, pending write/send payloads, and
interrupt payloads at Put (before serialize). Unsupported types throw
`CheckpointStoreException` with code **`checkpoint.unsupported_value_type`**
rather than silently losing data across restart.

| Allowed (v1) | Rejected examples |
|--------------|-------------------|
| `null`, string, bool, char, numeric primitives | Custom domain POCOs / records without host registration |
| `Guid`, `DateTime` / `DateTimeOffset` / `DateOnly` / `TimeOnly` / `TimeSpan` | `Stream`, delegates, `Type`, open polymorphic graphs |
| `JsonElement`, `byte[]` | Non-string dictionary keys |
| Lists/arrays of allowed values (depth ≤ 8) | Nesting deeper than 8 |
| String-key dictionaries of allowed values | |

Rules are **duplicated** per provider package (same isolation as D-029).
`JsonSerializerOptions.Web` only — no project-level options singletons.
**InMemory** remains process-local and does **not** enforce the allow-list
(any CLR reference may be held within one process). Host-owned
`JsonSerializerContext` / custom DTO registration is deferred (optional later).

### D-030 — OpenTelemetry: BCL emit in core + `Voluta.OpenTelemetry` package

**Decision (2026-08-14):** Core `Voluta` ships `ActivitySource` + `Meter` named
`"Voluta"` (`System.Diagnostics` only — no OpenTelemetry SDK package reference,
AOT-safe). Hosts opt in with **`Voluta.OpenTelemetry`**:
`AddVolutaInstrumentation()` on `TracerProviderBuilder` and
`MeterProviderBuilder` (same pattern as `OpenTelemetry.Instrumentation.*`).
No `Voluta.AspNetCore`. Metric names `voluta.*` dot.case; tags
`node.name` / `run.status` / `error.type` / `provider.name`; never raw
`thread.id`. Issue #36.

### D-031 — Custom stream events + MEAI token bridge (node-local writer)

**Decision (2026-08-14):** Token-level LLM streaming is **node-local** via
`GraphContext.Stream` (`IStreamWriter`), not a separate LangGraph-style debug
multiplex. Additive kinds: `StreamEventKind.Custom` (progress / structured
payloads) and `StreamEventKind.Messages` (token fragments). Additive mode:
`StreamMode.Messages` = lifecycle Start/End + custom/messages only (no
Values/Updates dumps). **All** modes (Values/Updates/Events/Messages) forward
node-written Custom/Messages so hosts keep their current mode and still see
tokens. Runtime injects a channel-backed writer per node task; bare
`GraphContext` construction uses a no-op writer. MEAI bridge:
`ChatClientNodeOptions.Stream` / `ChatClientGraphNode.Create(..., stream: true)`
calls `GetStreamingResponseAsync` and `WriteMessageAsync` per non-empty
`ChatResponseUpdate.Text`, then writes full text to the output channel.
UI SSE already serializes `StreamEvent` — no separate wire protocol for v1.
Issue #39.

### D-032 — Cross-thread Store (`IVolutaStore`) — InMemory first (#75)

**Решение (2026-08-15):** ship LangGraph-class **BaseStore** parity as a host-scoped
KV independent of `ICheckpointer`:

| Piece | Location |
|-------|----------|
| `IVolutaStore` / `StoreItem` | `Voluta.Abstractions.Store` |
| `InMemoryVolutaStore` | `Voluta.Store` (core) |
| DI | `VolutaBuilder.Store.UseInMemory()` · `AddVolutaStore` |

- Addressing: hierarchical `IReadOnlyList<string>` namespace + string key; List is
  exact-namespace (not prefix); Get/Delete miss = null / no-op.
- Not wired into Pregel by default — nodes resolve via `GraphContext.Services`.
- Concurrent Put/Get safe; InMemory holds values by reference (same policy as
  InMemory checkpointer).

**Deferred (same issue #75):** subgraph stream namespaces; task journal (A3);
durable store providers + wire allow-list; checkpoint format migration.

**Почему Store first:** isolated surface (mirrors ICheckpointer package map), no
engine surgery; task journal needs Continue / pending-write path changes.

OpenSpec: `openspec/specs/cross-thread-store/`.

### D-033 — TimeProvider как абстракция времени для run-guards

**Решение (2026-11-07):** deadline-гарды `RunGuards.Deadline` читают «сейчас» через
инжектируемый `TimeProvider`: новые перегрузки
`Deadline(DateTimeOffset at, TimeProvider)` и `Deadline(TimeSpan fromNow, TimeProvider)`;
существующие фабрики остаются и делегируют с `TimeProvider.System` (поверхность
аддитивная, wall-clock поведение не меняется). `DeadlineGuard` хранит провайдер и
оценивает `GetUtcNow() >= at` — тесты замораживают/двигают время через
`FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing`, только тестовая
зависимость — ядро остаётся zero-dep, без Polly/M.E.Resilience). Публичные
перегрузки бросают доменный `ArgumentException` на null-провайдер (стиль guard-ов
проекта, не ThrowIfNull-хелперы). Движковый `StartedAt` (captured at run entry в
`RunEngine`) остаётся на wall-clock `UtcNow` — вне скоупа этого решения.

Подтверждено владельцем 2026-11-07 рядом с этим решением: **D-029 / D-030 —
wire-копии per-provider остаются** (изоляция пакетов чекпойнтеров, linked-исходники
не вводить) и **ядро остаётся zero-dep** (Polly / Microsoft.Extensions.Resilience
не тянем).

## Open questions (remaining)

1. **Command taxonomy** — approve / reject / update-state / opaque payload shapes.
2. **Checkpoint serde** — wire versioning D-029 + value allow-list D-030 closed for v1; host pluggable converters still open.
3. ~~**Token-level LLM streaming**~~ — closed D-031 (node-local `IStreamWriter` + Messages kind/mode).
4. **Docs site** engine/hosting/domain.
5. **InMemory** re-export from Testing? Prefer core only; Testing wraps.
6. **UI next** — multi-thread discovery beyond process-tracked ids; richer inspector.
7. **AOT CI** — optional `dotnet publish` smoke on schedule vs every PR (slow).
8. **0.1 NuGet** — owner review of `PublicAPI.Unshipped.txt` then tag (D-026).
9. **Durable IVolutaStore** — File / EF / S3 when hosts need multi-process memory.
10. **Task journal (A3)** — completed task ids for stronger Continue (#75 remainder).

## GitHub tracking

Milestone: `v0.1 · MVP runtime`. Epic #1 (close after tag); UI #14 closed (RCL+SSE);
UI epic #13 first-cut done (follow-ups open); backlog #8 EF/S3 done (D-027).

## Связанное

- [roadmap.md](./roadmap.md)
- [conventions.md](./conventions.md)
- [`../openspec/specs/`](../openspec/specs/)
- [`../openspec/changes/archive/2026-08-14-architecture-runtime-core/`](../openspec/changes/archive/2026-08-14-architecture-runtime-core/)
- [`../CLAUDE.md`](../CLAUDE.md)

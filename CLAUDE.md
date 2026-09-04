# CLAUDE.md — Voluta agent & developer handbook

> **AGENTS.md** — это короткий указатель на этот файл. Полные правила,
> архитектура и процедуры — здесь. AGENTS.md содержит только legacy-тезисы
> для совместимости с tooling, который ожидает именно его.

## Что такое Voluta

Low-level orchestration framework для stateful-агентов в .NET. Концептуальный
источник — [LangGraph](https://github.com/langchain-ai/langgraph) (MIT), но
API .NET-native: generic state, типизированные редьюсеры, `IAsyncEnumerable`,
`Microsoft.Extensions.AI`.

**Не порт, не замена MAF.** Циклы + (опционально) checkpointing — то, что у MAF
нет и что Voluta может дать.

## Текущий статус

**Shipped on `main`:** Pregel runtime, checkpointers (InMemory · File · EF Core · S3) with
`AddVolutaCheckpoints` / `Use*`, Send fan-out, `Subgraph.AsNode`, topology export, Testing,
Generators, Agents.AI (MEAI/MAF), `MapVolutaUI`, samples (incl. MarketingAgent + MockAdMcp),
BenchmarkDotNet. **NuGet:** `v0.4.0`.

**Specs (source of truth):** main OpenSpec under
[`openspec/specs/`](openspec/specs/) (12 capabilities). Planning change archived:
[`openspec/changes/archive/2026-08-14-architecture-runtime-core/`](openspec/changes/archive/2026-08-14-architecture-runtime-core/).
Decisions: [`.agents/decisions.md`](.agents/decisions.md) (D-001…D-024).
Roadmap: [`.agents/roadmap.md`](.agents/roadmap.md).

**Two tiers (D-022):**
- **AOT core:** `Voluta` + Abstractions + DependencyInjection (`IsAotCompatible`); smoke `samples/AotSmoke`
- **Full .NET / ASP.NET:** Checkpoints.File / EF / S3 / Redis, UI, Agents.AI, Testing, Generators — regular CLR, not AOT-claimed

## Tech stack

| Слой | Технология |
|------|------------|
| Runtime | .NET 10 |
| Тесты | xUnit + Shouldly + NSubstitute + Bogus |
| Streaming | `IAsyncEnumerable<T>` (values / updates / events) |
| AI helpers | MEAI/MAF via `Voluta.Agents.AI` (`IGraphNode`) |
| Benches | BenchmarkDotNet (`benchmarks/`) |
| Публикация | NuGet через GitHub Actions OIDC trusted publishing |

## Layout (текущий)

```
voluta/
├── src/
│   ├── core/           ← Abstractions · Voluta · DependencyInjection
│   ├── checkpoints/    ← File · EntityFrameworkCore · S3 · Redis
│   ├── hosting/        ← Hosting · OpenTelemetry
│   ├── ops/            ← Voluta.UI (Studio SPA + MapStudioApi)
│   ├── agents/         ← Voluta.Agents.AI
│   └── devtools/       ← Testing · Generators
├── samples/     ← HelloWorld · InterruptResume · AotSmoke · ReviewBot · DocQ · MarketingAgent · MockAdMcp · UiHost · StudioHost · WorkerHost
├── templates/   ← Voluta.Templates (dotnet new voluta-agent)
├── benchmarks/  ← Voluta.Benchmarks
├── tests/
│   ├── unit/           ← package unit tests
│   │   └── checkpoints/
│   └── integration/
├── openspec/
│   ├── specs/   ← main capability specs (canonical)
│   └── changes/archive/…
└── voluta.slnx
```

**Studio SPA dev** (только при правке `src/ops/Voluta.UI/spa/`):
```bash
cd src/ops/Voluta.UI/spa
bun install && bun run dev      # :3847, proxy /voluta → :5188
bun run typecheck && bun run lint && bun run test && bun run build  # FE gates
```

## Сборка и команды

```bash
# Build всего solution (warnings-as-errors)
dotnet build voluta.slnx

# Tests
dotnet test voluta.slnx

# Format gate (drift-check, --severity hidden для auto-fix)
dotnet format voluta.slnx --severity hidden
```

**Build gate:** 0 warnings, 0 errors. `TreatWarningsAsErrors=true` +
`EnforceCodeStyleInBuild=true` в `Directory.Build.props` ловят всё
автоматически.

## Конвенции

Style enforced автоматически — большая часть **не** записана в prose:

- **`.editorconfig`** (repo root) — severity error/warning, плюс analyzers
  (`Microsoft.CodeAnalysis.NetAnalyzers`, `Microsoft.VisualStudio.Threading.Analyzers`,
  source-link).

Quick reference (конвенции, которые build enforce'ит):

- Match neighboring code style.
- Block-bodied members only (no expression-bodied methods); explicit access modifiers.
- **NO** underscore prefix for private fields; **NO** single-letter lambda parameters.
- **ВСЕГДА** primary constructors + Pyramid Rule (short → long parameter ordering).
- **Default** to `sealed class`, not `record` (records only for value objects / DTO).
- Pattern matching: `is { }` / `is not { }` for null checks.
- **NO** `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`.
- **NO** private business-logic methods — use services or `file static` helpers.
- DI-регистрация: руками через installer-классы. Без reflection auto-reg.

## NuGet-публикация

GitHub Actions OIDC → nuget.org trusted publishing. **Без долгоживущих
API-ключей.** Workflow: `.github/workflows/publish.yml` — триггер на тег
`v*.*.*`, после успешного CI. Детали — в workflow.

## Communication

Русский — основной. Документация для пользователей (README, доки на сайте) —
на английском, потому что OSS-аудитория международная.

## Где что лежит

**Корень** — публичные точки входа и общие конфиги:

- README.md — публичный обзор, quick start, roadmap.
- CLAUDE.md — **этот файл**: внутренняя кухня, конвенции, workflow.
- AGENTS.md — короткий указатель на CLAUDE.md для AI-агентов.
- CONTRIBUTING.md — как контрибьютить (setup, build, tests, commit).
- LICENSE — MIT.
- .editorconfig / Directory.Build.props / Directory.Packages.props —
  enforced стиль и build defaults.
- .gitignore / .gitattributes — git hygiene.
- .githooks/ — commit-msg (AI attribution strip), pre-commit (no-op).
- assets/ — графика:
  - `banner.png` (2400×600) — README header; не ресайзить.
  - NuGet icons (wired in `Directory.Build.props`):
    - `icon-i1.png` — **Voluta** (core runtime)
    - `icon-i4.png` — **Voluta.Checkpoints.*** (providers)
    - `icon-i5.png` — Abstractions / Testing / Generators / everything else
- .github/workflows/ — CI + publish.
- voluta.slnx — solution.

**`.agents/`** — внутренние документы (не публикуются на NuGet, не
рендерятся в README):

- `.agents/decisions.md` — принятые решения + open questions.
- `.agents/roadmap.md` — порядок работ.
- `.agents/conventions.md` — где живут конвенции (single source of truth).
- `.agents/adr/` — ADR (architecture decision records) для нетривиальных решений.
- `.agents/research/` — внешние исследования (LangGraph internals и т.п.).

## Связанное

- [`.agents/decisions.md`](.agents/decisions.md) — принятые решения.
- [`.agents/roadmap.md`](.agents/roadmap.md) — что делаем дальше.
- [`.agents/conventions.md`](.agents/conventions.md) — где живут конвенции.
- [`.agents/research/`](.agents/research/) — внешние исследования.
- LangGraph research notes (background agent, ещё в процессе):
  `C:/Users/bradw/AppData/Local/Temp/opencode/voluta-research.md`.
- `~/.agents/rules/` в user-global — общие C#/process правила (приоритет ниже
  repo-local).
# Tasks

Порядок подобран по зависимостям (см. design.md): абстракции (2) → ретрай (1) → per-task writes (2-я механика) → join (3) → статические паузы (4) → guards/Unlimited (6) → реестр типов (5) → доки/CI (8, 9 — независимы, можно параллельно). Пункты 1 и 2 оба трогают суперстеп-цикл: ретрай садится внутрь тела задачи (`ExecuteOneAsync`), per-task puts — в drain завершений; делать именно в этом порядке, чтобы progress put сразу нёс `TaskAttempts`.

Гейт после каждой кодовой группы (правила ВМ, дословно):
`flock /tmp/apiarion-build.lock dotnet build voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` — 0 warnings/errors; при load>12 (uptime) подожди 90с и повтори. git stash запрещён — откладывание правок только WIP-коммитом в своей ветке. Без NuGet-публикации и тегов.

## 1. Bugfix: ForkAsync теряет PendingInterrupts (пункт 7)

- [ ] 1.1 `src/core/Voluta/Runtime/Engine/Support/CheckpointStateMutation.cs` (инициализатор forked-снапшота L122–139): добавить `PendingInterrupts = [.. source.PendingInterrupts]` (прецедент — `CompiledGraph.CancelAsync` L445). Проверить: `flock`-сборка 0/0.
- [ ] 1.2 Тест в `tests/unit/Voluta.Unit/Runtime/UpdateStateForkReplayShould.cs`: fork Interrupted-снапшота переносит `PendingInterrupts` + `InterruptPayload` + `Status`, resume форка не падает `graph.invalid_resume` (сейчас форкают только Running/Done). Верификация: `flock /tmp/apiarion-build.lock dotnet test tests/unit/Voluta.Unit -p:MaxCpuCount=2 -p:NodeReuse=false`.

## 2. Базовые типы в Voluta.Abstractions (разблокирует пункты 1–6)

- [ ] 2.1 `RetryPolicy` (`Abstractions/Runtime/RetryPolicy.cs`: `Exponential`, `ShouldRetry`, `DelayFor`, капа сдвига 63), `NodeOptions` (`Voluta/Graph/Options/`), новый код `VolutaErrorCodes.GraphInvalidRetryPolicy = "graph.invalid_retry_policy"` + typed exception. Юнит-тесты в `tests/unit/Voluta.Abstractions.Unit`.
- [ ] 2.2 `RunStopReason`, `RunGuardAction`, `RunGuardContext`, `RunGuardDecision`, `IRunGuard`, `RunGuards` (`Abstractions/Runtime/`) + тесты фабрик.
- [ ] 2.3 Снапшот: `CompletedTaskIds`, `TaskAttempts`, `JoinArrivals`, `StopReason` (init-only, пустые дефолты) в `CheckpointSnapshot`; `CheckpointTypeRegistry` + `IRegistryAwareCheckpointer` (`Abstractions/Serialization`, `Abstractions/Checkpoint`). Тесты дефолтов/реестра (дубликаты id/типа → ArgumentException).
- [ ] 2.4 Внести всё новое в `PublicAPI.Unshipped.txt` затронутых проектов (Shipped не трогать). Верификация: `flock`-сборка (RS-анализаторы гейтят) + тесты Abstractions.

## 3. Retry policy узла (пункт 1)

- [ ] 3.1 Builder: 4 перегрузки `AddNode(..., NodeOptions)` в `StateGraph`, валидация (`options` null → ANE; `MaxAttempts < 1` → compile-fail `graph.invalid_retry_policy`); `GraphTopology.NodeRetries`. Тесты `Graph/StateGraphValidationShould.cs`.
- [ ] 3.2 `GraphContext.Attempt`: старый 11-параметрический ctor НЕ меняется (Shipped), attempt-вариант — ОТДЕЛЬНАЯ перегрузка ctor (шipped-ctor цепляется в неё с `attempt: 0`, сигнатура и наблюдаемое поведение прежние); прокинуть из `RunEngineExecutionHelpers.ExecuteOneAsync`.
- [ ] 3.3 Retry-цикл в `ExecuteOneAsync`: попытки до `MaxAttempts`, `ShouldRetry`/дефолт-предикат (OCE не ретраится), `Task.Delay(DelayFor(n), ct)`, исчерпание → обычный failure-путь. Юнит-тесты `Runtime/NodeRetryShould.cs`: успех на 3-й попытке (Attempt 1,2,3 виден узлу), исчерпание → Failed, отмена → без ретрая. Верификация: `flock`-сборка + `dotnet test tests/unit/Voluta.Unit`.

## 4. Per-task durable writes (пункт 2)

- [ ] 4.1 `RunEngineSnapshots.Build`: параметры `pendingWrites`/`completedTaskIds`/`taskAttempts` (сейчас хардкод `[]`); boundary-коммит пишет пустые. Прогресс-put: последовательный, из drain завершений (`RunEngineLoopHelpers.RunSuperstepStreamingAsync`), guarded `PutAsync(snapshot, expectedParentStep: step - 1)`; батч-слияние нескольких completions между await'ами (см. Risks в design.md).
- [ ] 4.2 Resume-пути `RunEngine` (3 входа: partial-resume ~L357, pending-sends ~L510, RunLoopAsync ~L629): применить `PendingWrites` через `TryApplyWrites` на восстановленном store; ready = scheduled − `CompletedTaskIds`; pending-sends путь — объединение PendingSends с незавершёнными pull-задачами.
- [ ] 4.3 Тесты `Runtime/PerTaskWritesShould.cs` (fault-injecting checkpointer из Voluta.Testing): краш после 1 из 2 задач → записанное применяется, завершённая не перевыполняется; чистый суперстеп → без остатка в boundary-снапшоте. Верификация: `flock`-сборка + тесты Voluta.Unit (A2-сценарии из `checkpoint`-спеки не регрессируют).

## 5. Wait-for-all join (пункт 3)

- [ ] 5.1 Builder: `AddEdge(string[] sources, string target)`; валидация (пустые/дубли/неизвестные/sentinel → `graph.invalid_edge`/`graph.unknown_endpoint`; target со смешанными входами → `graph.invalid_edge`); `GraphTopology.JoinSources` (StaticEdges не ломать — source→targets сохраняется).
- [ ] 5.2 Routing: resolve join'ов post-apply (`RunEngineRouting`), прибытия в `CheckpointSnapshot.JoinArrivals`, срабатывание → очистка epoch + target в ready; запись arrivals во все commit-put'ы.
- [ ] 5.3 Тесты `Runtime/JoinBarrierShould.cs` (wait/fire/epoch-переоткрытие через цикл/краш-резюм arrivals) + валидация в `Graph/`. Верификация: `flock`-сборка + тесты Voluta.Unit.

## 6. Статические паузы InterruptBefore/After (пункт 4)

- [ ] 6.1 `CompileOptions.InterruptBefore/InterruptAfter` + валидация в `Compile/Validate` (`graph.unknown_endpoint`); зеркала в `GraphTopology`. Тесты `Graph/`.
- [ ] 6.2 Engine: `StaticInterrupts.EvaluateBefore/EvaluateAfter`, вызовы на всех 3 входах суперстепа (порядок на входе: cancellation → guards (пуст, см. группу 7) → recursion limit → static-before); снапшот Interrupted + `StopReason = StaticBefore/StaticAfter` + `PendingInterrupts = []`; resume/continue без payload; `ResumeAsync` с командой по статической паузе → `graph.invalid_resume`.
- [ ] 6.3 Тесты `Runtime/StaticInterruptsShould.cs` (pause-before/continue, pause-after, цикл повторно паркует, различение Static/HITL по паре (PendingInterrupts, StopReason)). Верификация: `flock`-сборка + тесты Voluta.Unit.

## 7. Budget/deadline + Unlimited (пункт 6)

- [ ] 7.1 `RecursionLimit.Unlimited` (static class, `Voluta/Graph/Options/`), `CompileOptions.WithoutRecursionLimit()`, `CompileOptions.RunGuards`; хелпер `IsOutOfSteps(step, limit)` и замена 3 сравнений в `RunEngine.cs` (~L357/~L510/~L629). Тест: цикл > 25 шагов под `WithoutRecursionLimit()` доходит до END (`Runtime/UnlimitedRecursionShould.cs`).
- [ ] 7.2 Guard-оценка перед каждым суперстепом на 3 входах (ДО recursion limit и static-before; `StartedAt` фиксируется на входе в run): Stop → put Interrupted + `StopReason` (BudgetExhausted/DeadlineExceeded) + ready в `NextNodes` + `PendingInterrupts = []` + PendingSends сохранены, Interrupt-терминал с reason, без throw; ContinueAsync возобновляет и чистит StopReason на следующем commit.
- [ ] 7.3 Тесты `Runtime/RunGuardShould.cs`: Deadline(DateTimeOffset/TimeSpan) паркует, Budget-предикат паркует, guard не превращает run в Failed, Continue работает. Верификация: `flock`-сборка + тесты Voluta.Unit.

## 8. Реестр типов: провайдеры + conformance (пункт 5)

- [ ] 8.1 Engine-прокидка: `RunCheckpointWriter`/restore присваивает `options.CheckpointTypes` в `IRegistryAwareCheckpointer` при реализации (InMemory — нет, поведение 1:1; сам `ICheckpointer` не расширяется).
- [ ] 8.2 Провайдеры File/Redis/S3/EF: registry-aware `EnsureAllowed` (stamped `{"$type","$value"}` разрешён только с реестром; `$type` ≤ 128, резолвится), stamp на encode по точному типу, typed restore на decode, неизвестный `$type` → raw JSON + warning. Небольшие тесты каждого провайдера в его unit-проекте.
- [ ] 8.3 Conformance (`src/devtools/Voluta.Testing/Conformance/CheckpointerConformance.cs`): capability-gated `StampedTypeRoundtripAsync` (typed roundtrip + fallback без реестра); расширить `AdditiveFieldsRoundtripAsync` полями `completedTaskIds`/`taskAttempts`/`joinArrivals`/`stopReason`. Прогнать suite на всех 4 провайдерах + InMemory (существующий CI-binding в тестах провайдеров). Верификация: `flock /tmp/apiarion-build.lock dotnet test tests/unit -p:MaxCpuCount=2 -p:NodeReuse=false` (все unit-проекты, включая provider-конформанс).

## 9. Docs + CI/CD (пункты 8 и 9 — независимы от групп 1–8, можно параллельно)

- [ ] 9.1 `CHANGELOG.md` (Keep a Changelog): секция `## [0.5.0] - Unreleased` со всеми девятью пунктами.
- [ ] 9.2 ci.yml: явный список unit-проектов в `dotnet test` (все `tests/unit/**`, включая provider unit; без name-фильтра), TRX (`--logger trx`) + `upload-artifact` `if: failure()`, `timeout-minutes: 30`, pack-smoke по явному packable-списку (Voluta.Testing `IsPackable=false` исключён, nupkg-ассерты сохранены). Верификация: actionlint или `gh workflow view`/локальный YAML-parse; список проектов сверить с `ls tests/unit`.
- [ ] 9.3 publish.yml: честный header (API-key по умолчанию, OIDC за флагом), шаг `verify tag vs packages` на push (mismatch → exit 1 до push), OIDC-ветка `nuget/login@v1` за `vars.NUGET_OIDC == 'true'` (взаимоисключимо с API-key), dispatch с `dry_run=false` пушит, dry_run=true — никогда; release notes из CHANGELOG (awk-экстракция секции, fallback `--generate-notes`).
- [ ] 9.4 `docs/0.x/concurrency-migration.md`: EF `ConcurrencyToken` (CheckpointRecord.cs L33) — миграция колонки, opt-in guarded UpdateState (сверить точные имена флагов с кодом PR #103 перед написанием), обработка `checkpoint.conflict`; `docs/0.x/nuget-trusted-publisher.md`: 3 шага trusted publisher (политика на nuget.org, environment/vars, переключение флага и ревокация API-ключа).
- [ ] 9.5 Верификация доков: страницы рендерятся (mkdocs/build док-хаба, если настроен), ссылки на символы существуют в коде.

## 10. Финальная интеграция

- [ ] 10.1 `flock /tmp/apiarion-build.lock dotnet build voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` → 0 warnings/errors; при load>12 — пауза 90с, повтор.
- [ ] 10.2 `flock /tmp/apiarion-build.lock dotnet test voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` — все unit-проекты зелёные, integration-скелет не задет.
- [ ] 10.3 Гейт политики эволюции API: `git diff` относительно main не содержит правок ни в одном `PublicAPI.Shipped.txt` и не меняет сигнатуры shipped-членов (старый ctor `GraphContext` 11 параметров, `AddNode`/`AddEdge`/`CompileOptions`/`ICheckpointer`/снапшот — только добавления: перегрузки, новые типы, DIM, новые init-свойства; см. «API evolution policy» в design.md).
- [ ] 10.4 Сверка `PublicAPI.Unshipped.txt` всех затронутых проектов со списком из design.md.
- [ ] 10.5 `openspec validate --strict` зелёный; статусы спек-дельт соответствуют proposal (5 modified capabilities).

## Workflow follow-up (вне tracked-прогресса)

- Владелец при релизе переносит Unshipped→Shipped и ставит тег (оркестратор); изменение NuGet-публикации не выполняет.
- Архивация change по завершении apply: `openspec archive v050-features` (sync дельт в openspec/specs/).

# Tasks

Порядок подобран по зависимостям (см. design.md): абстракции (2) → ретрай (1) → per-task writes (2-я механика) → join (3) → статические паузы (4) → guards/Unlimited (6) → реестр типов (5) → доки/CI (8, 9 — независимы, можно параллельно). Пункты 1 и 2 оба трогают суперстеп-цикл: ретрай садится внутрь тела задачи (`ExecuteOneAsync`), per-task puts — в drain завершений; делать именно в этом порядке, чтобы progress put сразу нёс `TaskAttempts`.

Гейт после каждой кодовой группы (правила ВМ, дословно):
`flock /tmp/apiarion-build.lock dotnet build voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` — 0 warnings/errors; при load>12 (uptime) подожди 90с и повтори. git stash запрещён — откладывание правок только WIP-коммитом в своей ветке. Без NuGet-публикации и тегов.

## 1. Bugfix: ForkAsync теряет PendingInterrupts (пункт 7)

- [ ] 1.1 `src/core/Voluta/Runtime/Engine/Support/CheckpointStateMutation.cs` (инициализатор forked-снапшота L122–139): добавить `PendingInterrupts = [.. source.PendingInterrupts]` (прецедент — `CompiledGraph.CancelAsync` L445). Проверить: `flock`-сборка 0/0.
- [ ] 1.2 Тест в `tests/unit/Voluta.Unit/Runtime/UpdateStateForkReplayShould.cs`: fork Interrupted-снапшота переносит `PendingInterrupts` + `InterruptPayload` + `Status`, resume форка не падает `graph.invalid_resume` (сейчас форкают только Running/Done). Верификация: `flock /tmp/apiarion-build.lock dotnet test tests/unit/Voluta.Unit -p:MaxCpuCount=2 -p:NodeReuse=false`.

## 2. Базовые типы в Voluta.Abstractions (разблокирует пункты 1–6)

- [ ] 2.1 `RetryPolicy` (`Abstractions/Runtime/RetryPolicy.cs`: `Exponential`, `ShouldRetry`, `DelayFor`, капа сдвига 63), `NodeOptions` (`Voluta/Graph/Options/`), новый код `VolutaErrorCodes.GraphInvalidRetryPolicy = "graph.invalid_retry_policy"` (валидация позже мапится в существующий `GraphCompileException` — новых типов исключений не заводить). Юнит-тесты в `tests/unit/Voluta.Abstractions.Unit`.
- [ ] 2.2 `RunStopReason`, `RunGuardAction`, `RunGuardContext`, `RunGuardDecision`, `IRunGuard`, `RunGuards` (`Abstractions/Runtime/`) + тесты фабрик.
- [ ] 2.3 Снапшот: `CompletedTaskIds`, `TaskAttempts`, `JoinArrivals`, `StopReason` (init-only, пустые дефолты) в `CheckpointSnapshot`; `CheckpointTypeRegistry` + `IRegistryAwareCheckpointer` (`Abstractions/Serialization`, `Abstractions/Checkpoint`). Тесты дефолтов/реестра (дубликаты id/типа → ArgumentException).
- [ ] 2.4 Внести всё новое в `PublicAPI.Unshipped.txt` затронутых проектов (Shipped не трогать; `GraphTopology` — internal-тип, PublicAPI-строк не получает; новых типов исключений нет — валидация в `GraphCompileException`). Верификация: `flock`-сборка (RS-анализаторы гейтят) + тесты Abstractions.
- [ ] 2.5 Протащить новые поля снапшота (`CompletedTaskIds`/`TaskAttempts`/`JoinArrivals`/`StopReason`) во все 3 копировщика — `InMemoryCheckpointClone.Clone` (`InMemoryCheckpointer.cs` L252–270), `CompiledGraph.CancelAsync` (L431–446), `CheckpointStateMutation.ForkAsync` (L122–139, там же фикс `PendingInterrupts` из группы 1) — иначе InMemory-Get молча теряет поля. Тест: clone/fork/cancel roundtrip всех новых полей. Верификация: `flock`-сборка + тесты Voluta.Unit.

## 3. Retry policy узла (пункт 1)

- [ ] 3.1 Builder: 4 перегрузки `AddNode(..., NodeOptions)` в `StateGraph`, валидация (`options` null → ANE; `MaxAttempts < 1` → compile-fail `graph.invalid_retry_policy` через `GraphCompileException`); `GraphTopology.NodeRetries`. Тесты — расширить существующий `Graph/StateGraphCompileShould.cs` (нового файла валидаций не заводить).
- [ ] 3.2 `GraphContext.Attempt`: существующий 10-параметрический ctor (`GraphContext.cs` L32–42, запись в Unshipped) НЕ меняется; attempt-вариант — ОТДЕЛЬНАЯ перегрузка ctor с 11-м параметром `int attempt` последним и без дефолта (RS0027 против optional-параметров старого; существующий ctor цепляется в неё с `attempt: 0`, сигнатура и наблюдаемое поведение прежние); прокинуть из `RunEngineExecutionHelpers.ExecuteOneAsync`.
- [ ] 3.3 Retry-цикл в `ExecuteOneAsync`: попытки до `MaxAttempts`, `ShouldRetry`/дефолт-предикат (OCE не ретраится), `Task.Delay(DelayFor(n), ct)`, исчерпание → обычный failure-путь. Юнит-тесты `Runtime/NodeRetryShould.cs`: успех на 3-й попытке (Attempt 1,2,3 виден узлу), исчерпание → Failed, отмена → без ретрая. Верификация: `flock`-сборка + `dotnet test tests/unit/Voluta.Unit`.

## 4. Per-task durable writes (пункт 2)

- [ ] 4.1 `RunEngineSnapshots.Build`: параметры `pendingWrites`/`completedTaskIds`/`taskAttempts` (сейчас хардкод `[]`); boundary-коммит пишет пустые. Единственное место правки drain'а — замена одного `Task.WhenAll` (`RunEngineExecution.cs` L51–71) на WhenEach-подобный последовательный drain в `ExecuteSuperstepAsync` (`RunEngine.cs` L833+), батч-слияние нескольких completions между await'ами; прогресс-put через `RunCheckpointWriter.PutAsync` с его внутренним guard'ом (lastStep; у враппера нет `expectedParentStep`) — same-step rewrite легален по conformance `GuardedPutSameStepRewriteAllowedAsync`.
- [ ] 4.2 Resume-пути `RunEngine` (3 входа: partial-resume ~L358, pending-sends ~L511, RunLoopAsync ~L630): применить `PendingWrites` через `TryApplyWrites` на восстановленном store; ready = scheduled − `CompletedTaskIds`; pending-sends путь — объединение PendingSends с незавершёнными pull-задачами.
- [ ] 4.3 Тесты `Runtime/PerTaskWritesShould.cs` (fault-injecting checkpointer из Voluta.Testing): краш после 1 из 2 задач → записанное применяется, завершённая не перевыполняется; чистый суперстеп → без остатка в boundary-снапшоте. Верификация: `flock`-сборка + тесты Voluta.Unit (A2-сценарии из `checkpoint`-спеки не регрессируют).

## 5. Wait-for-all join (пункт 3)

- [ ] 5.1 Builder: `AddEdge(string[] sources, string target)`; валидация (пустые/дубли/неизвестные/sentinel → `graph.invalid_edge`/`graph.unknown_endpoint`; target со смешанными входами → `graph.invalid_edge`); `GraphTopology.JoinSources` (StaticEdges не ломать — source→targets сохраняется).
- [ ] 5.2 Routing: resolve join'ов post-apply (`RunEngineRouting`), прибытия в `CheckpointSnapshot.JoinArrivals`, срабатывание → очистка epoch + target в ready; запись arrivals во все commit-put'ы.
- [ ] 5.3 Тесты `Runtime/JoinBarrierShould.cs` (wait/fire/epoch-переоткрытие через цикл/краш-резюм arrivals) + валидация в `Graph/StateGraphCompileShould.cs`. Верификация: `flock`-сборка + тесты Voluta.Unit.

## 6. Статические паузы InterruptBefore/After (пункт 4)

- [ ] 6.1 `CompileOptions.InterruptBefore/InterruptAfter` + валидация в `Compile/Validate` (`graph.unknown_endpoint`); зеркала в `GraphTopology`. Тесты `Graph/`.
- [ ] 6.2 Engine: `StaticInterrupts.EvaluateBefore/EvaluateAfter`, вызовы на всех 3 входах суперстепа (порядок на входе: cancellation → guards (пуст, см. группу 7) → recursion limit → static-before); снапшот Interrupted + `StopReason = StaticBefore/StaticAfter` + `PendingInterrupts = []`; ослабить Continue-гейт `RunEngine.cs` L266–271 (сейчас жёстко `Status == Running` → `GraphInvalidContinueException`): пускать Interrupted со `StopReason` Static*/Budget/Deadline; проверка «static pause + Command → `graph.invalid_resume`» — ДО legacy-fallback `ResolvePendingInterrupts` (`RunEngine.cs` L1148–1166), иначе фолбэк синтезирует фейковый interrupt из `NextNodes[0]`.
- [ ] 6.3 Тесты `Runtime/StaticInterruptsShould.cs` (pause-before/continue, pause-after, цикл повторно паркует, различение Static/HITL по паре (PendingInterrupts, StopReason), continue-гейт принимает статическую паузу и по-прежнему отбивает Failed/Cancelled). Верификация: `flock`-сборка + тесты Voluta.Unit.

## 7. Budget/deadline + Unlimited (пункт 6)

- [ ] 7.1 `RecursionLimit.Unlimited` (static class, `Voluta/Graph/Options/`), `CompileOptions.WithoutRecursionLimit()`, `CompileOptions.RunGuards`; хелпер `IsOutOfSteps(step, limit)` и замена 3 сравнений в `RunEngine.cs` (L358/L511/L630). Тест: цикл > 25 шагов под `WithoutRecursionLimit()` доходит до END (`Runtime/UnlimitedRecursionShould.cs`).
- [ ] 7.2 Guard-оценка перед каждым суперстепом на 3 входах (ДО recursion limit и static-before; `StartedAt` фиксируется на входе в run): Stop → put Interrupted + `StopReason` (BudgetExhausted/DeadlineExceeded) + ready в `NextNodes` + `PendingInterrupts = []` + PendingSends сохранены, Interrupt-терминал с reason, без throw; ContinueAsync возобновляет через ослабленный гейт из 6.2 и чистит StopReason на следующем commit.
- [ ] 7.3 Тесты `Runtime/RunGuardShould.cs`: Deadline(DateTimeOffset/TimeSpan) паркует, Budget-предикат паркует, guard не превращает run в Failed, Continue работает. Верификация: `flock`-сборка + тесты Voluta.Unit.

## 8. Реестр типов: провайдеры + conformance (пункт 5)

- [ ] 8.1 Транспорт реестра: `CompileOptions.CheckpointTypes` → новое internal-свойство на `GraphTopology` (ctor L12–20, заполнение в `StateGraph.Compile` L232–245) → `CompiledGraph`/`RunEngine`; присваивание в провайдер при `checkpointer is IRegistryAwareCheckpointer` — там, где доступна топология (`RunEngine.CreateWriter` на L28–35 получает `RunOptions`, а не `CompileOptions`); InMemory — нет, поведение 1:1; сам `ICheckpointer` не расширяется.
- [ ] 8.2 Провайдеры File/Redis/S3/EF: registry-aware `EnsureAllowed` (stamped `{"$type","$value"}` разрешён только с реестром; `$type` ≤ 128, резолвится), stamp на encode по точному типу, typed restore на decode, неизвестный `$type` → raw JSON + warning. Небольшие тесты каждого провайдера в его unit-проекте.
- [ ] 8.3 Conformance (`src/devtools/Voluta.Testing/Conformance/CheckpointerConformance.cs`): capability-gated `StampedTypeRoundtripAsync` (typed roundtrip + fallback без реестра); расширить `AdditiveFieldsRoundtripAsync` полями `completedTaskIds`/`taskAttempts`/`joinArrivals`/`stopReason`. Прогнать suite на всех 4 провайдерах + InMemory (существующий CI-binding в тестах провайдеров). Верификация: `flock /tmp/apiarion-build.lock dotnet test tests/unit -p:MaxCpuCount=2 -p:NodeReuse=false` (все unit-проекты, включая provider-конформанс).

## 9. Docs + CI/CD (пункты 8 и 9 — независимы от групп 1–8, можно параллельно)

- [ ] 9.1 `CHANGELOG.md` (Keep a Changelog): секция `## [0.5.0] - Unreleased` со всеми девятью пунктами.
- [ ] 9.2 ci.yml: явный список unit-проектов в `dotnet test` (все `tests/unit/**`, включая provider unit; без name-фильтра), TRX (`--logger trx`) + `upload-artifact` `if: failure()`, `timeout-minutes: 30`, pack-smoke по явному packable-списку: 12 из 13 src-проектов (исключён только `Voluta.Testing`, `IsPackable=false`; `Voluta.UI` явного `IsPackable` не имеет — packable по умолчанию, publish.yml пакует его сегодня → включить; `templates/Voluta.Templates` в publish.yml пакуется отдельным шагом L90–93 — в ci не нужен, паритет списков сверить), nupkg-ассерты сохранены. Верификация: actionlint или `gh workflow view`/локальный YAML-parse; список проектов сверить с `ls tests/unit` и `find src -name '*.csproj'`.
- [ ] 9.3 publish.yml: честный header (API-key по умолчанию, OIDC за флагом), шаг `verify tag vs packages` на push (mismatch → exit 1 до push), OIDC-ветка `nuget/login@v1` за `vars.NUGET_OIDC == 'true'` (взаимоисключимо с API-key), dispatch с `dry_run=false` пушит, dry_run=true — никогда; release notes из CHANGELOG (awk-экстракция секции, fallback `--generate-notes`).
- [ ] 9.4 `docs/0.x/concurrency-migration.md` по реальным символам HEAD: `ConcurrencyToken` (`CheckpointRecord.cs` L33) — миграция колонки для существующих стор; `.IsConcurrencyToken()` (`CheckpointRecordConfiguration.cs` L62–63); конфликт = `DbUpdateConcurrencyException` → `CheckpointStoreException`/`checkpoint.conflict` (`EntityFrameworkCoreCheckpointer.cs` L118–130); восстановление через `GetStateAsync`/`GetHistoryAsync`. Guarded UpdateState — уже поведение (всё идёт через `RunCheckpointWriter` с внутренним guard'ом), опций/флагов нет — их не документировать; `docs/0.x/nuget-trusted-publisher.md`: 3 шага trusted publisher (политика на nuget.org, environment/vars, переключение флага и ревокация API-ключа).
- [ ] 9.5 Верификация доков: страницы рендерятся (mkdocs/build док-хаба, если настроен), ссылки на символы существуют в коде.

## 10. Финальная интеграция

- [ ] 10.1 `flock /tmp/apiarion-build.lock dotnet build voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` → 0 warnings/errors; при load>12 — пауза 90с, повтор.
- [ ] 10.2 `flock /tmp/apiarion-build.lock dotnet test voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` — все unit-проекты зелёные, integration-скелет не задет.
- [ ] 10.3 Гейт политики эволюции API: `git diff` относительно main не содержит правок ни в одном `PublicAPI.Shipped.txt` и не меняет существующие сигнатуры — независимо от их Shipped/Unshipped статуса (существующий ctor `GraphContext` — 10 параметров, `AddNode`/`AddEdge`/`CompileOptions`/`ICheckpointer`/снапшот — только добавления: перегрузки, новые типы, DIM, новые init-свойства; новых типов исключений нет; `GraphTopology` остаётся internal; см. «API evolution policy» в design.md).
- [ ] 10.4 Сверка `PublicAPI.Unshipped.txt` всех затронутых проектов со списком из design.md.
- [ ] 10.5 `openspec validate --strict` зелёный; статусы спек-дельт соответствуют proposal (5 modified capabilities).

## Workflow follow-up (вне tracked-прогресса)

- Владелец при релизе переносит Unshipped→Shipped и ставит тег (оркестратор); изменение NuGet-публикации не выполняет.
- Архивация change по завершении apply: `openspec archive v050-features` (sync дельт в openspec/specs/).

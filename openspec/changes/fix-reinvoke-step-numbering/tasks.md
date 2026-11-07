# Tasks

Порядок — по зависимостям (см. design.md): правка движка → конформанс-сценарии → юнит-тесты →
релизная мелочь (CHANGELOG + версия) → финальный гейт. Каждая группа закрывает свои тесты
сразу: конформанс-сценарии падают без правки движка, юнит-тесты — без конформанса.

Гейт после каждой кодовой группы (правила ВМ, дословно):
`flock -o /tmp/apiarion-build.lock dotnet build voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` —
0 warnings/errors; при load>12 (uptime) подожди 90с и повтори. git stash запрещён — откладывание
правок только WIP-коммитом в своей ветке. Без NuGet-публикации и тегов.

## 1. Движок: продолжение нумерации шагов при повторном InvokeAsync

- [ ] 1.1 `src/core/Voluta/Runtime/Engine/Support/RunCheckpointWriter.cs`: засид guard'а из загруженного latest. Предпочтительный вариант (design.md D2a) — переиспользовать `RunCheckpointWriter.GetAsync` (он уже ставит `lastStep = snapshot.Step`, L45–54); альтернатива (D2b) — отдельный внутренний метод сидирования, если в apply-пункте load придётся поднимать выше. Попутно поправить XML-doc писателя: «guard null до первого» теперь верно только для треда без истории. Проверка: код компилируется, guard сидится тем же чтением, из которого берётся базовый шаг (иначе база и guard разойдутся).
- [ ] 1.2 `src/core/Voluta/Runtime/Engine/RunEngine.cs`, `StreamAsync` (L63–125): перед первым put'ом загрузить latest снапшота треда (`options.ThreadId`) через writer'а; при `null` (свежий тред) — поведение как сегодня (`Running`@0 слепой, суперстепы с 1); при latest на шаге `N` — `Running`@(N+1) **guarded** (`expectedParentStep = N`), суперстепы с N+2 (значение `step`, передаваемое в `RunLoopAsync` L110–121, становится N+1 — его `step++` на L778 даёт N+2). Каналы — как сейчас: свежий `ChannelStore` + input writes (регрессия 0.4.0 не возвращается: состояние прошлого прогона не подмешивается). Проверка: `flock`-сборка 0/0 + ручной прогон сценария «два InvokeAsync на одном треде» без исключения.
- [ ] 1.3 Там же: валидация identity на входе invoke — прогнать загруженный снапшот через существующий `ValidateGraphIdentity` (L43–58) ДО первого put'а: чужой `(GraphId, GraphVersion)` → `GraphVersionMismatchException` (`graph.version_mismatch`) и снапшот не тронут; легаси-снапшот без `GraphVersion` — без проверки (как на resume/continue, L149/L312). Resume/Continue/`UpdateStateAsync`/`ForkAsync`/`CancelAsync` не трогать. Проверка: `flock`-сборка + тесты `Voluta.Unit` (существующие identity-тесты не должны сломаться).
- [ ] 1.4 Прогнать сценарий гонки руками: два конкурентных `InvokeAsync` на одном треде с историей → ровно один успех, второй падает с `CheckpointStoreException` (`checkpoint.conflict`), никакой `Failed`-снапшот не синтезируется. Проверка: временный юнит-тест или xUnit-скрипт (в группе 3 тест становится постоянным).

## 2. Конформанс-сценарии (прогоняются всеми 5 провайдерами)

- [ ] 2.1 `src/devtools/Voluta.Testing/Conformance/CheckpointerConformance.cs`: сценарий «повторный InvokeAsync ×N на том же треде»: собрать линейный граф над проверяемым провайдером и вызвать его N раз на одном thread id; проверки — ни один вызов не бросает исключение, `ListAsync` отдаёт историю, которая **растёт** с каждым вызовом, шаги не убывают (non-decreasing; терминальный `Done` переиспользует шаг последнего коммита — сравнить с `RuntimeCheckpointShould.MultiNodeListAsyncHistory`, где та же оговорка), и каждый шаг более позднего прогона строго больше последнего шага предыдущего. Проверка: сценарий зелёный на InMemory.
- [ ] 2.2 Там же: сценарий «реальная гонка двух InvokeAsync одного треда»: предварительно засеять тред одним завершённым прогоном (чтобы гонку решала guarded-пут маркера, а не слепой старт на пустом треде — см. design.md D4), затем запустить два `InvokeAsync` конкурентно и дождаться обоих; проверки — ровно один завершился успешно, второй бросил `CheckpointStoreException` с кодом `checkpoint.conflict`. Проверка: сценарий зелёный на InMemory.
- [ ] 2.3 Включить оба сценария в `RunAllAsync` (L565–581) — тогда они автоматически поедут во всех 5 существующих сьют-провайдерах (`tests/unit/checkpoints/*.Unit/*ConformanceShould.cs` + `tests/unit/Voluta.Testing.Unit/Conformance/InMemoryCheckpointerConformanceShould.cs`), новых тест-проектов не заводить. Проверка: `flock -o /tmp/apiarion-build.lock dotnet test tests/unit -p:MaxCpuCount=2 -p:NodeReuse=false` — все 5 конформанс-сьютов зелёные (File / Redis / S3 / EF / InMemory).

## 3. Юнит-тесты рантайма (InMemory)

- [ ] 3.1 `tests/unit/Voluta.Unit/Runtime/` (стиль — рядом лежащие `GraphIdentityGuardedPutsAndCancelShould.cs`, `RuntimeCheckpointShould.cs`): новый файл с тремя кейсами — (а) ×N `InvokeAsync` на одном треде: без конфликта, история растёт, шаги строго монотонны по прогонам; (б) identity mismatch: тред с чужим `(GraphId, GraphVersion)` → `GraphVersionMismatchException`, снапшот не изменён; (в) гонка: два конкурентных `InvokeAsync` на треде с историей → ровно один успех + ровно один `CheckpointStoreException` с кодом `checkpoint.conflict`. Проверка: `flock -o /tmp/apiarion-build.lock dotnet test tests/unit/Voluta.Unit -p:MaxCpuCount=2 -p:NodeReuse=false`.

## 4. CHANGELOG 0.5.1

- [ ] 4.1 `CHANGELOG.md`: добавить секцию `## [0.5.1] - Unreleased` **над** секцией `## [0.5.0] - Unreleased` (Keep a Changelog: newest first) с блоком `### Fixed` — описание регрессии (повторный `InvokeAsync` на том же треде падал `checkpoint.conflict` из-за слепого старта на шаге 0 при guard'е на `N`) и что именно починено (нумерация шагов продолжается, первый put треда с историей — guarded, гонка по-прежнему даёт ровно одного победителя). Проверка: секция присутствует, формат совпадает с остальным файлом.

## 5. Версия 0.5.1 (три места)

- [ ] 5.1 `Directory.Build.props` L21 → `<Version>0.5.1</Version>`.
- [ ] 5.2 `templates/Directory.Build.props` L16 → `<Version>0.5.1</Version>`.
- [ ] 5.3 `templates/Voluta.Templates/content/.template.config/template.json` L39 → `defaultValue` `"0.5.1"`. Проверка: `rg -n "0\.5\.[01]" Directory.Build.props templates/` — других вхождений `0.5.0` в версиях не осталось; NuGet-публикацию и теги НЕ делать (релиз — за оркестратором).

## 6. Финальный гейт

- [ ] 6.1 `flock -o /tmp/apiarion-build.lock dotnet build voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` → 0 warnings/errors; при load>12 (uptime) — пауза 90с и повтор.
- [ ] 6.2 `flock -o /tmp/apiarion-build.lock dotnet test voluta.slnx -p:MaxCpuCount=2 -p:NodeReuse=false` — все unit-проекты зелёные, включая 5 конформанс-сьютов.
- [ ] 6.3 Гейт скоупа: `git diff` относительно main не трогает `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` (правка целиком internal), не меняет `src/checkpoints/*`, не меняет wire v1 и не трогает resume/continue-входы — только `RunEngine.StreamAsync`, `RunCheckpointWriter`, конформанс-сьют и тесты.
- [ ] 6.4 `openspec validate fix-reinvoke-step-numbering --strict` — зелёный; дельты `checkpoint` / `graph-runtime` соответствуют proposal.

## Workflow follow-up (вне tracked-прогресса)

- Владелец/оркестратор релизит 0.5.1: перенос `PublicAPI.Unshipped.txt` → `Shipped.txt` (правок API тут нет), тег `v0.5.1`, публикация в NuGet. В этом change не публикуется и теги не ставятся.
- Архивация change после apply: `openspec archive fix-reinvoke-step-numbering` (дельты уедут в `openspec/specs/`).

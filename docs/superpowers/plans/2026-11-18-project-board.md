# Project Board Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans or, if the user selects delegation, superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Добавить доску проекта с независимыми карточками и управлением через MCP обычных проектных сессий.

**Architecture:** Core определяет команды и ревизии; Infrastructure последовательно читает и атомарно записывает один JSON доски. Единственный владелец в проектном кэше Presentation обслуживает UI и MCP. Нативная вкладка Avalonia отображает его состояние независимо от работы терминалов.

**Tech Stack:** C#, .NET 10, Avalonia 12, System.Text.Json, NUnit/VSTest, существующие AtomicFileWriter и MCP transport. Новые пакеты не нужны.

**Spec:** [Согласованная спека](../specs/2026-11-18-project-board-design.md).

## Результат реализации — 2026-11-18

Tasks 1–6 реализованы в `feat/project-board`. Core и storage записаны отдельными
коммитами; Presentation, MCP, UI и lifecycle объединены из-за общего владельца
доски. Детали карточки встроены в ProjectBoardView. Удаление комментария без
подтверждения соответствует спецификации, где подтверждение необязательно.

Проверки: Core 613/613, Presentation 1186/1186. Infrastructure: первоначальный
полный прогон 445/445; после изменений две ошибки повторного прогона устранены
или перепроверены целевым прогоном 15/15 (точная проверка sentinel miniskill и
повтор существующего ConPTY-теста после гонки чтения файла). Все 34 UI-fixture
прошли изолированно; 39 JavaScript-тестов повторно прошли после добавления
локального Node в PATH. Финальные проверки Board/MainWindow/controller — 15/15,
затем Board с дополнительными проверками фокуса и Jira — 4/4.

Fable одобрил ревью b5135310e0a74a3092a292fcc481e282: все 11 замечаний закрыты.
Ручной протокол остаётся непроверенным; автоматические тесты его не заменяют.
Диагностический пакет собирается напрямую теми же framework-dependent win-x64
аргументами publish: Publish-Pact.ps1 не поддерживает произвольную выходную
папку и дополнительно выпускает установщик. Релиз не выполняется.
Диагностическая сборка `artifacts/local-diag/Pact.App.Avalonia.exe` из коммита
`2eacd78311f2c5df4acdd87c8a47af3858755548` собрана успешно; native WebView gate
прошёл, включая styled snapshot round-trip. Доказательство привязано к SHA-256
исполняемого файла и коммиту в локальном журнале
`.superpowers/sdd/2026-11-18-project-board/native-gate.json`.
Два предупреждения AVLN5001 относятся к прежним RemarkOverlay/RemarkThreadView.
Чекбоксы ниже сохраняют исходный порядок работ; актуальный итог проверок — здесь.

## Global Constraints

- Статусы: Backlog, Ready, In Progress, Verification, Done; любые явные переходы разрешены.
- Только данные Pact: `Settings/Boards/<ProjectNoteFileKey>.json`; один упорядоченный массив карточек, колонки — фильтр.
- `take_next` выбирает первую Ready и назначает вызывающую сессию, даже если назначение было другим. Пустая Ready не запускает анализ Backlog.
- ROOT и оркестратор не получают инструменты Board. `pact_report` не входит в работу.
- В UI одна кнопка создания сверху; новая карточка появляется в конце Backlog. Архив только для Done, восстановление в конец Done.
- Комментарий может удалить пользователь или любой агент своего проекта. Назначение не блокирует карточку; несколько карточек могут иметь одного исполнителя.
- Только видимые существующие TUI-сессии. Работа доски не запускает процессы и не доставляет промпты.
- Public API имеет XML-документацию. Сохраняются состояния Clean, Dirty, Saving, Conflict, Failed.
- Предварительные план и спека не коммитятся отдельно от реализации. Чужие изменения сохраняются.
- Одна тяжёлая команда за раз, build максимум две MSBuild-ноды, тесты одна нода/два NUnit workers. Диагностический пакет — в существующем `artifacts/local-diag`.

## Review Focus

1. Один MCP-вызов дошёл до диска, ответ потерялся: повтор `take_next` не берёт вторую карточку (шаги 1, 4).
2. Чужой комментарий появился во время ввода описания: текст сохраняется без ложного конфликта; конкурирующая правка самого описания вызывает Conflict (шаг 3).
3. Удалена сессия-исполнитель: карточка и комментарии остаются, переход не запускает другой процесс (шаги 3, 5).
4. Удалён элемент, по которому получен курсор истории: чтение следующей страницы продолжается; новые комментарии не заставляют начинать обход заново (шаг 4).
5. Перезапуск/смена проекта при сохранении: принимается целый снимок, поздняя UI-публикация не попадает в чужой проект (шаги 2, 3, 6).

## Решения для реализации

### Размеры и выдача

Конкретизация оставленных в спеке лимитов; это ограничения самой Board,
а не утверждение о существующем глобальном лимите endpoint:

- Максимум 200 активных карточек; архив в этот лимит не входит.
- Название: 1–256 UTF-16 code units после Trim; описание до 16 384;
  комментарий 1–4 096 после проверки на whitespace; Jira URL до 2 048.
- Страница архива: по умолчанию 50, максимум 100 кратких карточек.
- Страница комментариев: по умолчанию 10, максимум 20. Ответ выдаёт
  комментарии в хронологическом порядке внутри страницы, курсор ведёт к старшим.
- Все текстовые ограничения одинаковы в UI/MCP; обрезание запрещено.
  Проверить сериализованный MCP-ответ на предельных данных: не больше 1 MiB
  UTF-8 для list/get с максимальной страницей. В summary не включать описание,
  URL, текст комментариев и произвольное имя исполнителя: только sessionId,
  номер, id, заголовок, статус, счётчик комментариев и токены ревизий.
- Имя автора комментария берётся из существующей сессии, хранится как snapshot;
  в выдаче допускается отдельный короткий display label до 128 code units,
  полный текст комментария никогда не усекается. UI показывает текущее имя
  существующей сессии, сохранённое — если сессии больше нет.
- Список активных карточек — один ответ; архив — `afterNumber`, комментарии —
  `beforeNumber`. Номера монотонные Int64 с checked increment, не индексы массива.

### Контракты между шагами

Новые типы помещаются в namespace `Pact.Core.Boards`:

```csharp
enum BoardCardStatus { Backlog, Ready, InProgress, Verification, Done }
record BoardActor(string? SessionId, string DisplayName); // null: пользователь
record BoardSnapshot(BoardState State, string DiskRevision, string OrderRevision);
record BoardMutationResult(BoardMutationOutcome Outcome, BoardSnapshot Snapshot,
    string? CardId = null, string? CommentId = null);
interface IProjectBoardStore
{
    Task<BoardSnapshot> LoadAsync(string projectRootPath, CancellationToken cancellationToken);
    Task<BoardMutationResult> ExecuteAsync(string projectRootPath, BoardCommand command,
        BoardActor actor, CancellationToken cancellationToken);
}
```

`BoardState`: версия схемы 1, NextCardNumber, OrderVersion, Cards.
`BoardCard`: Guid-id в формате N, Number, Title, Description, JiraUrl,
Status, AssignedSessionId, AssignedSessionName, IsArchived, FieldsVersion,
CreatedAt, UpdatedAt, NextCommentNumber, Comments.
`BoardComment`: Guid-id, Number, AuthorSessionId, AuthorName, CreatedAt, Text.
Коллекции публикуются неизменяемыми снимками, не List, который UI может мутировать.

`BoardMutationOutcome`: Applied, Empty, Conflict, NotFound, InvalidArgument,
Archived. Ошибки чтения/записи Infrastructure проходят через исключения
к UI/MCP boundary; там становятся Failed/storage_error без успешного результата.

Команды — закрытая иерархия `BoardCommand`, без универсального JSON patch:

- `CreateCard(Title, Description, JiraUrl)`;
- `UpdateCard(CardId, ExpectedCardRevision, BoardCardPatch Patch,
  string? BeforeCardId, string? ExpectedOrderRevision, bool Move)`;
- `AddComment(CardId, Text)`, `DeleteComment(CardId, CommentId)`;
- `SetArchived(CardId, bool Archived, ExpectedCardRevision)`;
- `TakeNext(ExpectedOrderRevision)`.

`BoardCardPatch` использует явный список изменяемых полей и типизированные
значения, чтобы отсутствие поля отличалось от снятия назначения/URL.
При `Move=true` отсутствие BeforeCardId означает конец целевой колонки;
якорь должен принадлежать той же активной колонке, иначе InvalidArgument.
Вся команда применяется целиком или не применяется.

`BoardRules.Apply(BoardState state, BoardCommand command, BoardActor actor,
DateTimeOffset now)` возвращает `BoardTransition(Outcome, State, CardId,
CommentId)`, где Outcome — BoardMutationOutcome, State — BoardState,
а оба id имеют тип string?.
Guid создаётся только в ветках создания карточки/комментария; тесты не зависят
от его значения. Store использует внедрённый TimeProvider.

Ревизия карточки — SHA-256 канонической проекции её редактируемых полей,
архивности и FieldsVersion. Порядка — SHA-256 OrderVersion и списка активных
id/статусов. Комментарии, их количество и UpdatedAt не входят в эти проекции.
Счётчик повышается только при реальном изменении соответствующей проекции;
no-op не меняет токен. Файловая ревизия отдельно вычисляется по прочитанным байтам.

## Task 1: Модель, команды и ревизии

**Files:** создать `src/Pact.Core/Boards/BoardState.cs`, `BoardCard.cs`,
`BoardComment.cs`, `BoardCommand.cs`, `BoardRules.cs`, `BoardRevisions.cs`,
`BoardLimits.cs`, `IProjectBoardStore.cs` в том же каталоге.
Тесты: `tests/Pact.Core.Tests/Boards/BoardRulesTests.cs`, `BoardRevisionsTests.cs`.

**Interfaces:** производит контракты выше и функции
`BoardRevisions.ForCard(BoardCard)`, `ForOrder(BoardState)` → string.

- [x] Написать поведенческие тесты команд и ревизий. Именованные случаи:
  `TakeNext_ClaimsFirstReadyAndReassigns`, `TakeNext_EmptyDoesNotChangeState`,
  `TakeNext_StaleRevisionDoesNotClaimSecond`, `OrderRevision_DetectsMoveAwayAndBack`,
  `Comment_DoesNotInvalidateCardOrOrder`, `Archive_OnlyDone_RestoreAtEnd`,
  `Move_InvalidAnchorLeavesWholeStateUntouched`, `Limits_RejectGrowthAllowArchive`.
  Проверять результат и сохранённые поля, а не внутренние коллекции/рефлексию.

```csharp
// После первой выдачи A из Ready[A,B], с тем же order token:
second.Outcome.ShouldBe(BoardMutationOutcome.Conflict);
second.State.Cards.Single(c => c.Id == b).Status.ShouldBe(BoardCardStatus.Ready);
// После добавления комментария:
BoardRevisions.ForCard(afterCard).ShouldBe(beforeCardRevision);
BoardRevisions.ForOrder(afterState).ShouldBe(beforeOrderRevision);
```

- [x] Собрать только Core test project с ограничениями ниже; зафиксировать
  ожидаемое падение новых тестов, затем реализовать минимальные команды.
- [x] Проверить все явные переходы, несколько назначений одной сессии,
  создание в конце Backlog, порядок после перемещения и восстановления.
- [x] Выполнить Core board tests; требуется ненулевое число тестов, Failed: 0.
  Коммит с Core-реализацией включает эту спеку и план, не наоборот.

## Task 2: Атомарное хранилище

**Files:** создать `src/Pact.Infrastructure/Storage/ProjectBoardStore.cs`;
изменить `src/Pact.Infrastructure/Storage/AppPaths.cs`.
Тесты: `tests/Pact.Infrastructure.Tests/Storage/ProjectBoardStoreTests.cs`,
существующий `AppPathsTests.cs`.

**Interfaces:** реализует `IProjectBoardStore` из Task 1;
конструктор `(AppPaths paths, TimeProvider timeProvider)`.
Добавляет `AppPaths.BoardsDirectory` внутри Settings.

- [x] Написать тесты на пустую доску без создания файла, round-trip,
  одинаковый ключ нормализованных путей и сохранность после пересоздания store.
- [x] Добавить `ConcurrentTakeNext_OnlyOneWins`, `ExternalEdit_RefreshesProjection`,
  `MalformedOrUnknownSchema_DoesNotOverwrite`, `CommentDeletion_Persists`,
  `WriteFailure_LeavesPreviousBytes`. Для конкуренции использовать barriers/TCS,
  не sleep. Ошибку записи воспроизвести файловой блокировкой Windows или
  существующим тестовым механизмом, не добавлять универсальный filesystem wrapper.
- [x] Реализовать общий gate по нормализованному полному пути файла,
  чтение текущих байтов под gate перед каждой командой, проверку схемы и правил,
  `AtomicFileWriter.WriteTextAsync` с `AppPaths.AtomicTempDirectory`.
  Несохранённое новое состояние не публикуется. Не создавать lock-файлы/backup.
- [ ] Проверить большие архивы, которые превышают размер ответа MCP:
  store их читает; ограничения активных элементов не запрещают архивирование.
- [ ] Выполнить Infrastructure board tests, убедиться в Failed: 0 и отсутствии
  подмены повреждённого файла пустой доской. Коммитить только этот шаг.

## Task 3: Общий владелец и редактор карточки

**Files:** создать `src/Pact.Presentation/Services/ProjectBoardWorkspace.cs`,
`src/Pact.Presentation/ViewModels/ProjectBoardViewModel.cs`,
`src/Pact.Presentation/ViewModels/BoardCardEditorViewModel.cs`;
изменить `src/Pact.Presentation/ViewModels/MainWindowViewModel.cs`,
`src/Pact.App.Avalonia/CompositionRoot.cs`.
Тесты: `tests/Pact.Presentation.Tests/Services/ProjectBoardWorkspaceTests.cs`,
`tests/Pact.Presentation.Tests/ViewModels/BoardCardEditorViewModelTests.cs`.

**Interfaces:** `MainWindowViewModel.GetOrCreateProjectBoard(string rootPath)`
возвращает один `ProjectBoardWorkspace` для нормализованного корня.
Workspace предоставляет `LoadAsync(CancellationToken)`,
`ExecuteAsync(BoardCommand, BoardActor, CancellationToken)` и событие
`SnapshotChanged` после успешной записи. ViewModel подписывается на него через
существующий UI dispatcher. Store — из Task 2, не отдельный экземпляр на вкладку.

- [ ] Написать тесты на общий экземпляр для UI/MCP до открытия экрана,
  отсутствие публикации при неудачной записи и обработку последовательных
  команд без обратного порядка уведомлений.
- [ ] Написать тесты редактора: `OtherCardComment_DoesNotConflict`,
  `SameDescriptionEdit_PreservesDirtyBufferAndConflicts`,
  `UnrelatedFieldEdit_RebasesOnlyUserFields`, `FailedSave_KeepsRetryableText`.
  Использовать управляемый TimeProvider для debounce; никаких timing-sleep.
- [ ] Реализовать lifecycle владельца и буфер названия/описания с существующим
  набором Document states. Самое свежее локальное редактирование не теряется,
  если предыдущая запись завершилась позже. Добавление комментария очищает
  composer только после успеха, удаление обновляет список после записи.
- [ ] Добавить `FlushAsync(CancellationToken)` на workspace для pending editor saves;
  ошибку/конфликт возвращать вызывающему, а не глотать при уходе с вкладки.
  Поле Jira и metadata сохранять командами, не общей заменой BoardState.
- [ ] Проверить `DeletedAssignee_PreservesCardAndDisablesNavigation`:
  исчезнувшая сессия не меняет статус, не запускает runtime.
- [ ] Выполнить точечные Presentation tests; проверить Failed: 0. Коммитить шаг.

## Task 4: MCP обычной проектной сессии

**Files:** изменить `src/Pact.Presentation/Services/AgentControl/SessionTools.cs`,
`AgentControlDispatcher.cs`, `IAgentControlHost.cs`;
`src/Pact.App.Avalonia/Controllers/AgentControlHost.cs`;
`src/Pact.Infrastructure/AgentControl/PactSkills/PactMcpSkill.md`.
Добавить `src/Pact.Presentation/Services/AgentControl/BoardToolResponses.cs`
для кратких DTO и страниц. Тесты: существующие `SessionToolsTests.cs`,
`AgentControlDispatcherTests.cs` и новый `BoardToolsTests.cs` рядом с ними.

**Interfaces:** IAgentControlHost получает
`ReadProjectBoardAsync(string projectId, CancellationToken)` → BoardSnapshot и
`MutateProjectBoardAsync(string projectId, BoardCommand command, BoardActor actor,
CancellationToken)` → BoardMutationResult. Host резолвит общий workspace Task 3.
Dispatcher устанавливает actor из токена и проверяет принадлежность назначения
проекту; не принимает авторство или projectId из аргументов MCP.

- [ ] Написать тесты восьми инструментов со схемой из спеки: list/get/create/update,
  add_comment/delete_comment/archive/take_next. Проверить отсутствие каталога
  для ROOT/оркестратора и отказ вызова по имени, stale token и чужой sessionId.
- [ ] Добавить проверки `LostTakeResponse_StaleTokenCannotTakeAgain`,
  `Paging_DeletedBoundaryStillContinues`, `NewComments_DoNotRestartHistory`,
  `MaxPayload_EscapedUnicodeFitsBudget`. Последняя использует настоящий
  сериализатор ответа инструментов, Unicode и управляющие символы, не подсчёт
  длины исходной строки. До 1 MiB проверяется размер готового ответа.
- [ ] Реализовать tools в существующем каталоге/dispatcher. Для больших
  `SessionTools.cs` допустим partial `SessionTools.Board.cs` без изменения
  общего механизма регистрации. Не создавать отдельный endpoint.
- [ ] Реализовать стабильные курсоры по номеру; `get_card` возвращает поля
  карточки без полного встроенного массива Comments, только выбранную страницу.
  Комментарии всех страниц доступны, лимит ответа не обрывает текст.
- [ ] Для take_next брать order token из запроса; status/assignment/card changes
  проверяют card token. Структурированные ошибки: empty/conflict/not_found/
  invalid_argument/archived/owner_unavailable/storage_error. Не открывать UI
  для подтверждения MCP-команд.
- [ ] Обновить miniskill: Ready-only, отсутствие авторазбора Backlog,
  правила конфликтов и потерянных ответов, no-auto-retry create/comment,
  Verification/Done. Выполнить тесты BoardTools + существующих SessionTools/
  Dispatcher, не меняя другие инструменты. Коммитить шаг.

## Task 5: Нативная доска и панель карточки

**Files:** создать `src/Pact.App.Avalonia/Views/ProjectBoardView.axaml`,
`ProjectBoardView.axaml.cs`, `BoardCardDetailView.axaml`,
`BoardCardDetailView.axaml.cs`; изменить `ProjectScreenView.axaml`,
`ProjectScreenView.axaml.cs`, `src/Pact.Presentation/ViewModels/ProjectScreenViewModel.cs`,
`src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.ProjectScreen.cs`.
Тесты: `tests/Pact.App.Avalonia.Tests/Views/ProjectBoardHeadlessTests.cs` и
существующие `ProjectScreenViewHeadlessTests.cs`.

**Interfaces:** ProjectScreenViewModel получает Board view model из Task 3;
значение `ProjectScreenTab.Board` добавляется в конец enum, визуально после
Overview. Существующие числовые значения/имена вкладок остаются прежними.

- [ ] Написать headless behavioral tests: единственная Create создаёт Backlog,
  open/close детали с возвратом фокуса, смена статуса, add/delete comment,
  архив/восстановление, пустая Ready, клавиатурное перемещение вверх/вниз.
  Взаимодействовать с контролами/командами, не проверять текст XAML-файла.
- [ ] Реализовать пять колонок со счётчиками и marker первой Ready,
  горизонтальную прокрутку на узком окне, native drawer внутри project surface.
  Повторить принятую компоновку v2, используя текущие ресурсы темы.
- [ ] Реализовать DnD через тот же Move command, что клавиатурное перемещение.
  Сохранить expected tokens начала drag; при конфликте восстановить фактический
  порядок, объяснить причину и позволить повторить. Комментарий не ломает drag.
- [ ] В деталях реализовать описание с Markdown preview/editor, необязательный
  URL, выбор сессии/снятие назначения, переход к доступной сессии штатным путём.
  Escape/focus trap и конфликт несохранённого текста проверять отдельно.
- [ ] Добавить кнопку удаления комментария и обычное UI-подтверждение;
  архивировать/восстанавливать Done, архив читать без редактирования.
- [ ] Выполнить headless tests с Failed: 0. Коммитить UI вместе с проверками.

## Task 6: Восстановление, документация и проверка целиком

**Files:** `src/Pact.App.Avalonia/Controllers/SessionRestorationCheckpoint.cs`,
`SoftRestartSnapshotBuilder.cs`, `SoftRestartRestorer.cs`,
`AvaloniaMainShellController.cs` и соответствующие lifecycle partial-файлы,
найденные по существующим flush/close методам;
`tests/Pact.App.Avalonia.Tests/Controllers/SoftRestartRestorerTests.cs`,
`SessionRestorationCheckpointTests.cs`.
Документы: README.md, README.ru.md, docs/architecture.md,
docs/agent-onboarding.md, docs/backlog/2026-10-25-next-steps.md;
новый `docs/manual-tests/project-board.md`.

**Interfaces:** существующий checkpoint хранит имя вкладки Board;
shutdown/project departure дожидаются `ProjectBoardWorkspace.FlushAsync` Task 3
до disposal. При Failed/Conflict пользователь получает возможность остаться
и исправить сохранение; неподтверждённый текст не считается сохранённым.

- [ ] Добавить тест восстановления Board без запуска терминала только ради
  выбора вкладки. Проверить переход между проектами во время ожидающего
  сохранения и поздний результат после ухода владельца.
- [ ] Встроить flush в переходы, паузу/закрытие проекта и shutdown.
  Для изменения RootPath явно объяснить смену доски, сохранить старый файл,
  не переносить старый dirty buffer в новый корень.
- [ ] Дополнить руководства контрактами Board, хранением, MCP и отсутствием
  автоматической синхронизации Jira/Backlog. Бэклог помечать реализованным
  только после проверок, выпуск не утверждать до реального релиза.
- [ ] Пройти все тесты четырёх затронутых проектов по очереди после одной
  общей сборки. Повторять только при новых изменениях/ошибках.
- [ ] Собрать диагностический пакет штатным `tools/Publish-Pact.ps1`, прочитав
  его актуальные параметры, в `artifacts/local-diag`. Нативный gate:
  `pwsh -NoProfile -File tools/Invoke-NativeWebViewGate.ps1 -ApplicationPath
  artifacts/local-diag/Pact.App.Avalonia.exe`. Убедиться, что этот путь содержит
  именно свежую сборку, и получить успешный итог gate.
- [ ] Выполнить ручной протокол из нового docs/manual-tests/project-board.md:
  Board ↔ terminal ↔ browser, узкое окно, непрерывная работа фонового терминала,
  два агента/два take_next, комментарий во время редактирования, архив,
  перезапуск и прежний путь данных. Автоматический gate не заменяет проверку
  drawer и перетаскивания; непроверенные ручные пункты назвать явно.
- [ ] Провести финальное независимое ревью изменения, устранить замечания;
  `git diff --check HEAD -- <touched paths>`, проверить только свои изменения.
  Коммитить с итоговыми документами; не публиковать релиз автоматически.

## Команды проверок

Платформа проверена: SDK 10.0.401, NUnit 4.6.1, NUnit3TestAdapter 6.2.0,
Microsoft.NET.Test.Sdk 17.14.1, обычный VSTest-режим. Команды ниже — для этапа
реализации, сейчас они не запускались. Перед тестами собрать актуальные тестовые
assembly; не выдавать старую сборку за результат новых тестов.

```powershell
dotnet restore Pact.slnx --disable-parallel --locked-mode
dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Board" -- NUnit.NumberOfTestWorkers=2
dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Board|FullyQualifiedName~AppPathsTests" -- NUnit.NumberOfTestWorkers=2
dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Board|FullyQualifiedName~SessionToolsTests|FullyQualifiedName~AgentControlDispatcherTests" -- NUnit.NumberOfTestWorkers=2
dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Board|FullyQualifiedName~ProjectScreen|FullyQualifiedName~SoftRestartRestorerTests|FullyQualifiedName~SessionRestorationCheckpointTests" -- NUnit.NumberOfTestWorkers=2
```

На отдельных шагах вместо полной solution build собирать нужный test csproj
с теми же build flags. Финальный прогон тех же четырёх test csproj выполняется
без `--filter`, последовательно. Restore только если требуются отсутствующие
зависимости. При отсутствии PATH использовать установленную toolchain
`artifacts/toolchain`, не устанавливать/обновлять SDK или зависимости без причины.

## Самопроверка плана

Все разделы спеки покрыты Tasks 1–6: модель и очередь, запись, конфликтный
редактор, MCP, UI, lifecycle. Пять Review Focus имеют целевые проверки.
Новых зависимостей и второго владельца состояния нет. Точные типы хранилища,
команд и разделение токенов определены в одном месте; последующие шаги
используют их. Публичный MCP не выдаёт весь BoardState или полный архив.
План не расширяет продукт до автоматического менеджера или интеграции Jira.

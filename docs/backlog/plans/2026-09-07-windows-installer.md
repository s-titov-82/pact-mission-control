# Windows Installer Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox syntax for tracking. This document is a local planning artifact; commit it only together with implementation, following repository policy.

**Goal:** Пользователь устанавливает Pact одним EXE: мастер проверяет зависимости, предлагает доставить недостающие компоненты и устанавливает приложение только после успешной проверки.

**Architecture:** Inno Setup упаковывает готовый framework-dependent win-x64 payload из существующего release pipeline. Сам мастер работает без .NET; зависимости скачиваются с Microsoft и устанавливаются до копирования Pact. ZIP остаётся альтернативным способом распространения.

**Tech Stack:** Inno Setup / Pascal Script, PowerShell build tooling, GitHub Actions, существующий .NET 10 publish.

**Spec:** Пользовательский контракт и границы первой версии определены ниже в этом документе; отдельной утверждённой спецификации нет. Основание — запрос пользователя от 2026-09-07 и текущие скрипты публичного репозитория `D:\Personal\Pact`.

## Границы и пользовательский контракт

- Работа выполняется в изолированном worktree публичного репозитория `D:\Personal\Pact`. Локальный `AgentTerminal` и его незавершённые backlog-изменения не являются источником публичного payload.
- Windows x64; ограничения версии ОС согласовать с поддержкой .NET 10 и существующими native-компонентами. Не расширять обещанную поддержку на ARM64 или x86.
- Установщик: `pact-mission-control-<version>-win-x64-setup.exe`, RU/EN.
- Pact устанавливается для текущего пользователя в `%LOCALAPPDATA%\Programs\Pact Mission Control`. Обычный запуск мастера без повышения прав; UAC появляется только при необходимости установки системной зависимости. Не добавлять переключатель all-users в первой версии.
- Payload приложения и проверенный WebView2 Evergreen bootstrapper встроены в EXE. Интернет на машине пользователя нужен только для отсутствующих зависимостей: .NET installer скачивает мастер, WebView2 Runtime скачивает встроенный bootstrapper. Если зависимости подходят, установка должна работать без сети.
- Экран подготовки показывает найденные и отсутствующие компоненты, необходимость загрузки и возможного UAC. Нажатие «Установить» разрешает перечисленные установки; отказ оставляет Pact неустановленным.
- Проверять семейство, архитектуру и совместимую версию .NET по опубликованному `Pact.App.Avalonia.runtimeconfig.json`. Windows Desktop Runtime — допустимый пакет доставки, но не автоматически обязательное семейство: Avalonia сама по себе не означает зависимость от `Microsoft.WindowsDesktop.App`.
- WebView2 проверять как Runtime, а не как установленный браузер Edge; учитывать per-user и per-machine установки для исходного пользователя.
- После каждого установщика повторять обнаружение. Ошибка или отмена блокирует копирование Pact. Уже установленную общую зависимость не удалять при неудаче последующего шага.
- Перезагрузка только по явному решению пользователя. При требовании перезагрузки приостановить установку Pact, объяснить повторный запуск Setup после reboot; не создавать скрытый автозапуск продолжения.
- Ярлык в меню «Пуск», ярлык рабочего стола по выбору, запись в «Установленных приложениях», удаление приложения.
- Обновление повторным запуском нового Setup; стабильный AppId, один путь и одна запись удаления. Понижение версии блокируется с понятным сообщением; повторная установка той же версии разрешена.
- Установка, обновление и удаление не удаляют `%APPDATA%\Pact`, произвольный `--data-root`, пользовательские репозитории и общие runtimes.
- При работающем Pact попросить закрыть приложение штатно до замены файлов. Не завершать терминалы или агентов принудительно и не перезапускать приложение автоматически после обновления.
- CLI агентов, Git, PowerShell 7 и SDK не доставляются этим установщиком. Автообновление, MSI, Store, winget и offline-bundle не входят в первую версию.
- Лимит 50 MiB сохраняется для распакованного payload Pact; размер установщика и внешних runtime-downloads учитывается отдельно.
- .NET команды: один тяжёлый процесс за раз; restore `--disable-parallel`, build `-m:2 -nr:false -v q -p:BuildInParallel=false`, test `--no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`.

## Что уже есть и что меняется

Текущий `tools/Publish-Pact.ps1` поддерживает `Prepare` и `Finalize`: подготовка payload, подпись собственных бинарников в workflow, генерация SBOM и ZIP, затем SHA256SUMS. `tools/Test-PublicationArtifacts.ps1` сейчас разрешает ровно ZIP, `manifest.spdx.json` и `SHA256SUMS.txt`. Простое добавление EXE в release-каталог нарушит этот контракт.

| Файл | Ответственность / изменение |
| --- | --- |
| `installer/Pact.iss` — новый | Мастер RU/EN, install scope, ярлыки, upgrade/uninstall, вызов prerequisite logic |
| `installer/Prerequisites.iss` — новый | Обнаружение, загрузка, установка и повторная проверка зависимостей |
| `installer/dependencies.lock.json` — новый | Для .NET: версия, Microsoft URL, digest runtime-download; для встроенного WebView2 bootstrapper: версия, Microsoft URL и digest сборочного входа; для Inno Setup: версия, официальный URL, SHA-256 дистрибутива и ожидаемый SHA-256 установленного ISCC.exe |
| `tools/Build-PactInstaller.ps1` — новый | Проверка готового payload и lock, генерация include-конфигурации в artifacts, запуск ISCC |
| `tools/Test-PactInstaller.ps1` — новый | Проверка фактически установленного payload, регистрации, upgrade и удаления в disposable-среде; имя/checksum/подпись релизного EXE проверяет только Test-PublicationArtifacts.ps1 |
| `tests/powershell/PactInstaller.Tests.ps1` — новый | Поведенческие проверки build tooling и отказов на некорректных входных данных |
| `tools/Publish-Pact.ps1` | Финальный состав релиза и вычисление checksums после упаковки и подписей |
| `tools/Test-PublicationArtifacts.ps1` | Новый допустимый EXE, его checksum, версия и подпись |
| `tools/Test-WorkflowContracts.ps1` | Актуализировать существующие обязательные workflow-контракты |
| `.github/workflows/ci.yml`, `.github/workflows/release.yml` | Закреплённый compiler, сборка и проверки Setup, подпись и публикация |
| `README.md`, `README.ru.md`, `docs/guide/en/`, `docs/guide/ru/` | Основная установка через Setup, ZIP-альтернатива, зависимости и обновление |
| `docs/release-verification.md`, `docs/release-verification.ru.md`, `docs/development.md` | Состав релиза, подписи, воспроизводимая сборка установщика |
| `docs/manual-tests/installer-smoke.md` — новый при реализации Task 5 | Постоянный тест-протокол: перенести туда матрицу из этого плана; результаты прогонов хранятся отдельно |

## Task 1: Подтвердить runtime-контракт и закрепить toolchain

**Вход:** текущий tagged/releasable source и `Directory.Build.props`.
**Выход:** `installer/dependencies.lock.json`, записанный runtime-контракт и выбранная версия compiler.

- [ ] Подготовить worktree от актуального публичного `main`; проверить dirty tree и инструкции репозитория.
- [ ] Получить payload штатным `Publish-Pact.ps1 -Phase Prepare`. Прочитать `runtimeconfig.json`: framework/frameworks, версии и roll-forward. Сверить с реальным запуском в VM без SDK; SDK рабочей машины не является доказательством минимальных требований.
- [ ] Зафиксировать правило: если нужен только `Microsoft.NETCore.App`, установленный совместимый x64 .NET Runtime достаточен; если присутствует `Microsoft.WindowsDesktop.App`, требуется и это семейство. .NET 9, только x86, preview или только следующий major не засчитывать автоматически.
- [ ] Закрепить стабильную версию Inno Setup, проверить лицензию и поддержку нужных download/signature API. В lock записать официальный URL дистрибутива, его SHA-256, версию и SHA-256 установленного ISCC.exe. Task 4 устанавливает именно этот дистрибутив; предустановленный compiler runner и плавающий `latest` не используются.
- [ ] Закрепить официальные пакеты зависимостей и контрольные суммы в lock-файле. .NET patch выбрать актуальный обслуживаемый 10.0.x на момент реализации и не ниже требований payload.
- [ ] Для WebView2 скачать на сборочной машине Evergreen bootstrapper по Microsoft URL из lock, проверить закреплённый digest и подпись Microsoft и встроить его в Setup как installer-only ресурс вне publish root. Build-PactInstaller.ps1 выполняет этот шаг; если URL стал отдавать другие байты, сборка останавливается до явного обновления lock. На машине пользователя мастер извлекает и проверяет встроенный bootstrapper, а тот скачивает актуальный Evergreen Runtime. Hash bootstrapper не описывает обновляющийся Runtime; проверка подписи скачанного Runtime принадлежит официальному bootstrapper.
- [ ] Определить источник минимальной WebView2-версии по используемым runtime API; не приравнивать автоматически версию NuGet SDK к обязательной версии Runtime.

**Проверка:** таблица реальных runtime-требований, воспроизводимые URL/hash, compiler запускается без установленного .NET. При расхождении с README исправление требований входит в Task 5.

## Task 2: Собрать устанавливаемый Pact без prerequisite-downloads

**Файлы:** `installer/Pact.iss`, `tools/Build-PactInstaller.ps1`, `tests/powershell/PactInstaller.Tests.ps1`.
**Интерфейс:** `Build-PactInstaller.ps1 -Version <semver> -PublishDirectory <absolute-path> -OutputDirectory <absolute-path> -CompilerPath <absolute-path> -AuthenticodeStatus <Signed|Unsigned> [-SignToolPath <absolute-path> -SigningCertificateThumbprint <thumbprint>]`. В Signed оба параметра подписи обязательны; сертификат с private key предварительно импортирован в CurrentUser/My build identity и доступен signtool. В Unsigned параметры подписи запрещены. Результат — один versioned Setup EXE; в Signed ISCC подписывает uninstaller до встраивания и финальный Setup посредством своего SignTool hook. Скрипт не пересобирает приложение и не меняет publish root.

**Payload:** вход — дерево после однократной генерации SBOM/ZIP, включая `_manifest/spdx_2.2/manifest.spdx.json`; до этого этапа Build-PactInstaller.ps1 отказывает. ZIP entries и встроенные файлы приложения совпадают по относительным путям и SHA-256. Bootstrapper и штатные файлы Inno uninstall — отдельно перечисленные installer-only файлы, не часть ZIP/SBOM приложения.

- [ ] Добавить проверки входа: отсутствующий EXE/runtimeconfig/license/SBOM, несовпадение версии, payload вне поддерживаемой архитектуры, неверный compiler/hash или неполные/несогласованные параметры подписи должны завершаться ошибкой до компиляции. Для Task 2 получить вход существующей однократной Finalize; интеграцию окончательного release-каталога выполнить в Task 4.
- [ ] Создать Pascal Script мастер с `PrivilegesRequired=lowest`, фиксированным AppId, стандартным per-user каталогом, RU/EN и встроенным payload. Не требовать PowerShell 7 или .NET для выполнения самого Setup.
- [ ] Добавить меню «Пуск», опциональный desktop shortcut и uninstaller. Первый запуск только по выбору пользователя и без elevation.
- [ ] Добавить сравнение версий установленного продукта: новая — upgrade, та же — reinstall, более старая — отказ.
- [ ] Для обновления проверить файлы, занятые процессами из целевого каталога, включая ConPTY/WebView. Отключить принудительное закрытие и автоматический restart; дать повторить проверку после штатного выхода.
- [ ] Использовать штатный uninstall log Inno Setup с `UninstallLogMode=append`, стабильными AppId и install path. При upgrade файлы прошлой версии, которых нет в новом payload, допускается оставить до штатного uninstall: он удаляет зарегистрированные файлы прежней и новой установок. Собственный persistent inventory, backup/rollback и общую очистку `{app}` не вводить. Посторонние файлы не регистрировать и не удалять рекурсивным wildcard-delete.
- [ ] Проверить install → reinstall → upgrade → uninstall в disposable VM с уже установленными runtimes. После upgrade каждый файл нового ZIP должен присутствовать с ожидаемым hash и приложение должно запускаться; оставшиеся файлы прежнего payload не считаются ошибкой. После uninstall зарегистрированные файлы обеих версий удалены, контрольный посторонний файл и профиль сохранены.

**Готовность:** мастер корректно устанавливает, обновляет и удаляет Pact; зависимости пока заранее подготовлены в тестовой среде.

## Task 3: Доставлять отсутствующие зависимости

**Файлы:** `installer/Prerequisites.iss`, `installer/Pact.iss`, build-generated include из `dependencies.lock.json`.
**Контракт состояния:** `Detected → Missing → Consent → Download → Verify → Install → Recheck → Ready`; отмена/ошибка не переводит в Ready. Только Ready для всех компонентов разрешает установку Pact.

- [ ] Реализовать обнаружение x64 .NET по машинному install location и списку shared frameworks; не полагаться на случайный `dotnet` в PATH. Сравнивать числовые версии с runtimeconfig, учитывать установленный более новый совместимый patch.
- [ ] Реализовать обнаружение WebView2 по рекомендованным Microsoft per-user/per-machine данным и, где доступно, native version API. Проверять контекст исходного пользователя, даже если установщик зависимости запросил другую учётную запись администратора.
- [ ] Показать RU/EN список недостающих зависимостей и согласие на их загрузку и установку. Не обращаться в сеть, если список пуст.
- [ ] .NET installer загружать во временную директорию мастера; WebView2 bootstrapper извлекать из Setup туда же без повторного скачивания. Для обоих проверить закреплённый digest и действительную подпись Microsoft до запуска. Проверки выполняются нативным механизмом Setup или доступными средствами Windows, не managed helper, которому самому нужен .NET.
- [ ] Устанавливать .NET с `/install /quiet /norestart` через `ShellExec('runas', ..., ewWaitUntilTerminated, ...)`, отдельно проверяя отказ запуска и код завершения дочернего процесса. WebView2 запускать обычным `Exec` с `/silent /install` в контексте per-user Setup. Мастер и запуск Pact остаются без повышения прав; ExecAsOriginalUser не является механизмом этого потока.
- [ ] Обработать отдельно успешный exit code, отказ UAC, отмену, сетевую ошибку, неверную подпись/hash, уже идущую установку и требование reboot. Для .NET `3010` означает требование перезагрузки, а не обычную ошибку.
- [ ] После успеха повторить фактическое обнаружение: нулевой exit code при отсутствующей зависимости — ошибка. При reboot остановиться до установки Pact и показать инструкцию повторного запуска после перезагрузки.
- [ ] Сохранять диагностический setup log без секретов, показать его расположение при ошибке. Не удалять общие runtimes при отмене или uninstall Pact.

**Готовность:** чистая VM получает зависимости и затем Pact; cancel/failure/reboot не оставляют ложный статус успешной установки приложения.

## Task 4: Включить Setup в release pipeline

**Файлы:** publish/artifact/workflow tooling из таблицы выше.
**Выход релиза:** ZIP, Setup EXE, SPDX inventory приложения, SHA256SUMS; подписи и attestations относятся к окончательным байтам.

- [ ] Зафиксировать порядок: Prepare payload → подпись Pact binaries при наличии сертификата → однократная генерация SBOM внутри publish root и ZIP → Build-PactInstaller.ps1 из этого неизменяемого дерева вместе с `_manifest/` (ISCC подписывает uninstaller до встраивания и Setup после компоновки в Signed) → финальные SHA256SUMS → validation → attestations → release upload.
- [ ] Внутри Finalize отделить SBOM/ZIP от финальных checksums; между ними вызвать Build-PactInstaller.ps1. Finalize выполняется один раз: не повторять генерацию SBOM, не удалять существующий SBOM ради обхода guard. После ZIP запретить модификацию publish root и сравнить в памяти список относительных путей и SHA-256 до/после ISCC; отдельный persistent inventory не создавать.
- [ ] Единый состав завершённого релиза для CI, release и локального Publish-Pact.ps1: ZIP + Setup + standalone SBOM + SHA256SUMS. ZIP остаётся отдельным скачиваемым артефактом, но ZIP-only режима сборки/валидации нет. Передать compiler/signing параметры из расширенного Publish-Pact.ps1 в Build-PactInstaller.ps1; обновить локальные команды в docs/development.md и все существующие callers. Промежуточные Prepare/SBOM/ZIP результаты не объявлять завершённым релизом.
- [ ] Расширить artifact validator: EXE обязателен; проверить точное имя, версию, checksum и ожидаемый Authenticode status. Отсутствующий или изменённый EXE должен ломать проверку.
- [ ] Добавить Setup в checksum attestation. SBOM приложения продолжает описывать payload; не объявлять его полным SBOM Inno wrapper или внешних runtime-downloads. В release-verification явно описать эти границы и dependency lock.
- [ ] Signed workflow импортирует существующий PFX secret во временный CurrentUser/My сертификат с private key, передаёт thumbprint и signtool path в Publish/Build-PactInstaller.ps1 и удаляет только импортированный сертификат в finally. ISCC получает именованный SignTool hook с signtool `/sha1 <thumbprint> /s My /fd SHA256 /td SHA256 /tr https://timestamp.digicert.com` и `SignedUninstaller=yes`; hook подписывает и проверяет создаваемые EXE во время компиляции. Пароль PFX не передавать ISCC, не сохранять в include/логах. В Unsigned hook отсутствует, SignedUninstaller=no; подпись Setup проверяет artifact validator, установленного uninstaller — smoke-проверка. Недоступный сертификат/ошибка timestamp/sign/verify блокирует Signed сборку, без fallback к Unsigned.
- [ ] В CI и release перед компиляцией скачать Inno Setup по URL из lock в уникальный каталог runner temp, проверить SHA-256 дистрибутива, установить через `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR=<isolated-tool-directory>`. Проверить версию и SHA-256 полученного ISCC.exe и передать абсолютный CompilerPath; не использовать PATH compiler или предустановленную копию. Те же команды документировать для локальной сборки. Ошибка загрузки/hash/установки блокирует сборку.
- [ ] CI собирает unsigned Setup, проверяет artifact и выполняет изолированный install/uninstall smoke без UI на disposable runner. Не устанавливать зависимости на рабочую машину разработчика ради теста.
- [ ] В ci.yml добавить шаг `id: version`: прочитать единственный VersionPrefix из Directory.Build.props, проверить формат, записать `version` в GITHUB_OUTPUT. Заменить все вхождения `0.1.0` в ci.yml на этот output: Publish -Version, validator -Version/-ReleaseDirectory, upload-artifact name/path и новые installer paths. Release продолжает сверять VersionPrefix с тегом. Поведенческий workflow test использует вторую версию и подтверждает единые пути упаковки/валидации/upload.
- [ ] Обновить существующие workflow contract tests; проверить, что публикация не проходит при ошибках compiler, artifact validation или signature verification.

**Готовность:** CI выдаёт валидированные ZIP и Setup; tagged release публикует оба после всех проверок. Обычная реализация плана не создаёт новый релизный тег сама по себе.

## Task 5: Проверить пользовательские сценарии и обновить RU/EN документацию

**Файлы:** `tools/Test-PactInstaller.ps1`, `docs/manual-tests/installer-smoke.md`, README/guide/release-verification на двух языках.

- [ ] Автоматизировать сравнение установленных файлов приложения с ZIP entries по путям и SHA-256, включая `_manifest/spdx_2.2/manifest.spdx.json`. Для чистой установки разрешённые дополнительные файлы — штатные Inno uninstall EXE/data/language files; bootstrapper после завершения не остаётся в `{app}`. После upgrade требовать все файлы нового ZIP, разрешая остатки прошлой версии и заранее созданные пользовательские файлы. Проверить регистрационную запись, версию, запуск новой версии и штатное удаление файлов обеих версий с сохранением постороннего файла/профиля. Форму релизного EXE валидировать только Test-PublicationArtifacts.ps1; подпись фактически установленного uninstaller проверяет smoke. Не писать тесты, проверяющие только наличие строк в `.iss`.
- [ ] При реализации перенести следующую матрицу в `docs/manual-tests/installer-smoke.md`, заменив таблицу здесь ссылкой на постоянный протокол в том же изменении. Сейчас таблица — единственный исходный текст будущего протокола, не второй поддерживаемый список. Выполнять уже постоянный протокол в VM/snapshots; Windows version, SHA Setup и PASS/FAIL/NOT RUN сохранять отдельно в `artifacts/installer-tests/<version>/<run-id>/results.md`, не в плане или протоколе.

Постоянная матрица перенесена в
`docs/manual-tests/installer-smoke.md`. Её пункты не считаются выполненными без
отдельно сохранённого результата прогона на disposable Windows environment.

- [ ] Для reboot/error paths использовать контролируемые фикстуры в тестовой сборке prerequisite logic; такие подмены не должны включаться в release Setup. Дополнить реальные happy paths в VM.
- [ ] Проверить первый запуск установленного Pact, реальный ConPTY terminal и WebView2 page в чистом интерактивном окружении. Hosted native-gate self-test не заменяет эту проверку.
- [ ] В RU/EN документации Setup становится основной инструкцией; ZIP сохраняет ручные prerequisites. Описать интернет/UAC, сохранение профиля, обновление, удаление и unsigned SmartScreen статус без обещания отсутствия предупреждений.
- [ ] Выполнить markdown links, public-tree, workflow/artifact проверки и `git diff --check HEAD -- <touched paths>`; полный CI на финальном коммите.

**Критерий завершения:** есть опубликованный CI-артефакт Setup с проверенным payload, результаты матрицы и согласованная RU/EN документация. Непройденная чистая Windows проверка явно остаётся ограничением; повседневное использование приложения на машине с SDK её не заменяет.

## Порядок и оценка

Task 1 → Task 2 → Task 3 → Task 4 → Task 5. План сам по себе не коммитить. Реализацию разделить на reviewable изменения: core installer, prerequisite chain, release integration/documentation; каждый коммит включает относящиеся к нему проверки.

Предварительная оценка: 1–2 рабочих дня на реализацию при доступной VM и штатной работе tooling; подтверждение всей матрицы и signed path может потребовать дополнительного времени. Сертификат не блокирует unsigned первую версию. Если чистая VM недоступна, сборку и автоматические проверки можно закончить, но end-to-end готовность установщика не заявлять.

## Официальные источники для реализации

- [Microsoft: установка .NET на Windows, параметры и reboot exit code](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Microsoft: распространение и обнаружение WebView2 Runtime](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/distribution)
- [Inno Setup: per-user privileges](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
- [Inno Setup: загрузка файлов](https://jrsoftware.org/ishelp/topic_filessection.htm)
- [Inno Setup: ShellExec для дочернего запуска с runas](https://jrsoftware.org/ishelp/topic_isxfunc_shellexec.htm)
- [Inno Setup: SignedUninstaller во время компиляции](https://jrsoftware.org/ishelp/topic_setup_signeduninstaller.htm)
- [Inno Setup: SignTool hook для Setup и uninstaller](https://jrsoftware.org/ishelp/topic_setup_signtool.htm)
- [Inno Setup: накопление штатного uninstall log](https://jrsoftware.org/ishelp/topic_setup_uninstalllogmode.htm)

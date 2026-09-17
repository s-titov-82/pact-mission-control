# Проверка релиза

[English](release-verification.md) | [Русский](release-verification.ru.md)

Каждый релиз для Windows x64 содержит четыре файла:

- `pact-mission-control-<version>-win-x64.zip`;
- `pact-mission-control-<version>-win-x64-setup.exe`;
- `manifest.spdx.json`;
- `SHA256SUMS.txt`.

В ZIP находятся приложение, зависящее от установленной среды .NET
(`framework-dependent`), встроенный ConPTY, ресурсы WebView2/xterm, MIT License,
уведомления о сторонних компонентах, тексты их лицензий и манифест SPDX 2.2. В
архив не входят PDB, исходный код, тесты, приватная история проектирования или
компоненты среды выполнения других платформ.

Документ SPDX описывает payload приложения, общий для ZIP и установщика. Это
не полный SBOM оболочки Inno Setup, встроенного загрузчика WebView2 или
скачиваемого позднее установщика .NET. Их закреплённые сборочные входы,
официальные URL и контрольные суммы записаны в
`installer/dependencies.lock.json` соответствующей ревизии исходников.

В lock-файле также указана минимальная версия WebView2 Runtime, которую
принимает установщик. Она соответствует семейству Runtime закреплённого в
репозитории WebView2 SDK; перед установкой PACT более старую среду обновляет
официальный Evergreen-загрузчик Microsoft.

Файл `licenses/runtime-packages.json` внутри ZIP — это читаемый точный список
пакетов конкретной сборки. Наборы пакетов проверяются по документу SPDX и
опубликованному `.deps.json`, а классификация лицензий и подтверждающие данные —
по привязанному к версиям манифесту `third_party/runtime-components.json` из
соответствующей ревизии исходников.

## Проверка контрольных сумм

Поместите четыре файла в один каталог. Сравните результаты PowerShell с
соответствующими строками в `SHA256SUMS.txt`:

```powershell
Get-FileHash .\pact-mission-control-0.1.7-win-x64.zip -Algorithm SHA256
Get-FileHash .\pact-mission-control-0.1.7-win-x64-setup.exe -Algorithm SHA256
Get-FileHash .\manifest.spdx.json -Algorithm SHA256
```

Не запускайте приложение, если контрольная сумма отличается.

## Проверка аттестаций GitHub

Если установлен GitHub CLI, выполните:

```powershell
gh attestation verify .\pact-mission-control-0.1.7-win-x64.zip --repo s-titov-82/pact-mission-control
```

Процесс выпуска публикует подтверждение происхождения сборки для трёх файлов
из списка контрольных сумм и аттестацию SPDX SBOM для ZIP. `SHA256SUMS.txt`
охватывает ZIP, установщик и отдельный документ SPDX.
Аттестация подтверждает, что
артефакт для этого репозитория выпустил GitHub Actions, но не заменяет подпись
кода.

## Authenticode и SmartScreen

Первые релизы `0.1.x` могут не иметь подписи Authenticode. Валидатор релиза
проверяет и бинарные файлы Pact, и установщик и не допускает ложного
утверждения `Signed`. Windows SmartScreen способен предупредить о неподписанном или ещё не
набравшем репутацию файле. Прежде чем решать, запускать ли его, проверьте
контрольную сумму и аттестацию GitHub.

Встроенные `conpty.dll` и `OpenConsole.exe` сохраняют собственные действительные
подписи Microsoft; Pact не подписывает их повторно.

Внутреннее устройство упаковки и локальная проверка описаны для разработчиков
в разделе [Development](development.md#packaging).

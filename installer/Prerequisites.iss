const
  DotNetRegistryKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\{#PactDotNetFramework}';
  WebView2RegistryKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  DownloadPage: TDownloadWizardPage;
  DotNetMissing: Boolean;
  WebView2Missing: Boolean;
  PrerequisitesAccepted: Boolean;

function ParseVersionPart(var Remaining: String; var Value: Word): Boolean;
var
  Delimiter: Integer;
  Part: String;
  Parsed: Integer;
begin
  Delimiter := Pos('.', Remaining);
  if Delimiter = 0 then begin
    Part := Remaining;
    Remaining := '';
  end else begin
    Part := Copy(Remaining, 1, Delimiter - 1);
    Delete(Remaining, 1, Delimiter);
  end;
  Parsed := StrToIntDef(Part, -1);
  Result := (Parsed >= 0) and (Parsed <= 65535);
  if Result then
    Value := Parsed;
end;

function TryPackVersion(const Value: String; var Packed: Int64): Boolean;
var
  Remaining: String;
  Major, Minor, Patch, Build: Word;
begin
  Remaining := Value;
  Major := 0;
  Minor := 0;
  Patch := 0;
  Build := 0;
  Result := ParseVersionPart(Remaining, Major) and
    ParseVersionPart(Remaining, Minor) and
    ParseVersionPart(Remaining, Patch);
  if Result and (Remaining <> '') then
    Result := ParseVersionPart(Remaining, Build) and (Remaining = '');
  if Result then
    Packed := PackVersionComponents(Major, Minor, Patch, Build);
end;

function InitializeSetup: Boolean;
var
  InstalledText: String;
  InstalledVersion, CandidateVersion: Int64;
begin
  Result := True;
  if RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\PactMissionControl_is1',
      'DisplayVersion',
      InstalledText) and
    TryPackVersion(InstalledText, InstalledVersion) and
    TryPackVersion('{#PactVersion}', CandidateVersion) and
    (ComparePackedVersion(InstalledVersion, CandidateVersion) > 0) then begin
    MsgBox(
      FmtMessage(ExpandConstant('{cm:PactDowngradeBlocked}'), [InstalledText, '{#PactVersion}']),
      mbError,
      MB_OK);
    Result := False;
  end;
end;

function IsRequiredDotNetInstalled: Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
  InstalledVersion, RequiredVersion: Int64;
  InstalledVersionText: String;
  InstalledMajor: Word;
begin
  Result := False;
  if not TryPackVersion('{#PactDotNetMinimumVersion}', RequiredVersion) then
    Exit;
  if not RegGetValueNames(HKLM32, DotNetRegistryKey, Versions) then
    Exit;
  for Index := 0 to GetArrayLength(Versions) - 1 do begin
    InstalledVersionText := Versions[Index];
    if ParseVersionPart(InstalledVersionText, InstalledMajor) and
      (InstalledMajor = {#PactDotNetMajorVersion}) and
      TryPackVersion(Versions[Index], InstalledVersion) and
      (ComparePackedVersion(InstalledVersion, RequiredVersion) >= 0) then begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsUsableWebView2Version(const Value: String): Boolean;
var
  InstalledVersion, RequiredVersion: Int64;
begin
  Result := TryPackVersion(Value, InstalledVersion) and
    TryPackVersion('{#PactWebView2MinimumVersion}', RequiredVersion) and
    (ComparePackedVersion(InstalledVersion, RequiredVersion) >= 0);
end;

function IsWebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKCU, WebView2RegistryKey, 'pv', Version) and
      IsUsableWebView2Version(Version)) or
    (RegQueryStringValue(HKLM32, WebView2RegistryKey, 'pv', Version) and
      IsUsableWebView2Version(Version));
end;

procedure RefreshPrerequisiteState;
begin
  DotNetMissing := not IsRequiredDotNetInstalled;
  WebView2Missing := not IsWebView2Installed;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo;
  if MemoGroupInfo <> '' then
    Result := Result + NewLine + MemoGroupInfo;
  if MemoTasksInfo <> '' then
    Result := Result + NewLine + MemoTasksInfo;

  RefreshPrerequisiteState;
  Result := Result + NewLine + NewLine + ExpandConstant('{cm:PactPrerequisitesHeading}');
  if DotNetMissing then
    Result := Result + NewLine + Space + ExpandConstant('{cm:PactDotNetMissing}')
  else
    Result := Result + NewLine + Space + ExpandConstant('{cm:PactDotNetDetected}');
  if WebView2Missing then
    Result := Result + NewLine + Space + ExpandConstant('{cm:PactWebView2Missing}')
  else
    Result := Result + NewLine + Space + ExpandConstant('{cm:PactWebView2Detected}');
end;

function MissingPrerequisitesText: String;
begin
  Result := '';
  if DotNetMissing then
    Result := Result + #13#10 + '  - .NET Runtime {#PactDotNetMinimumVersion} x64';
  if WebView2Missing then
    Result := Result + #13#10 + '  - Microsoft Edge WebView2 Runtime {#PactWebView2MinimumVersion}+';
end;

function HasExpectedMicrosoftSignature(const Filename: String): Boolean;
var
  PowerShellPath: String;
  Parameters: String;
  SafeFilename: String;
  ExitCode: Integer;
begin
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  SafeFilename := Filename;
  StringChangeEx(SafeFilename, '''', '''''', True);
  Parameters := '-NoProfile -NonInteractive -Command ' +
    '"$s = Get-AuthenticodeSignature -LiteralPath ''' +
    SafeFilename +
    '''; if (($s.Status -ne ''Valid'') -or ' +
    '($s.SignerCertificate.Subject -notlike ''*Microsoft Corporation*'')) { exit 1 }"';
  Result := Exec(
    PowerShellPath,
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ExitCode) and (ExitCode = 0);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or PrerequisitesAccepted then
    Exit;
  RefreshPrerequisiteState;
  if not (DotNetMissing or WebView2Missing) then begin
    PrerequisitesAccepted := True;
    Exit;
  end;
  Result := MsgBox(
    ExpandConstant('{cm:PactPrerequisitesConsent}') + MissingPrerequisitesText,
    mbConfirmation, MB_YESNO) = IDYES;
  PrerequisitesAccepted := Result;
end;

function InstallAndCheck(const Filename, Parameters, DisplayName: String;
  UseRunAs: Boolean; var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  Verb: String;
begin
  Result := '';
  if UseRunAs then
    Verb := 'runas'
  else
    Verb := '';
  if not HasExpectedMicrosoftSignature(Filename) then begin
    Result := FmtMessage(ExpandConstant('{cm:PactPrerequisiteSignatureFailed}'), [DisplayName]);
    Exit;
  end;
  if not ShellExec(Verb, Filename, Parameters, '', SW_SHOW, ewWaitUntilTerminated, ExitCode) then begin
    Result := FmtMessage(ExpandConstant('{cm:PactPrerequisiteLaunchFailed}'), [DisplayName]);
    Exit;
  end;
  if ExitCode = 3010 then begin
    NeedsRestart := True;
    Result := ExpandConstant('{cm:PactPrerequisiteRestartRequired}');
    Exit;
  end;
  if ExitCode <> 0 then
    Result := FmtMessage(ExpandConstant('{cm:PactPrerequisiteInstallFailed}'), [DisplayName, IntToStr(ExitCode)]);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  DotNetInstaller: String;
  WebView2Installer: String;
begin
  Result := '';
  RefreshPrerequisiteState;

  if DotNetMissing then begin
    DownloadPage.Clear;
    DownloadPage.Add('{#PactDotNetInstallerUrl}', '{#PactDotNetInstallerFileName}', '{#PactDotNetInstallerSha256}');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        Result := FmtMessage(ExpandConstant('{cm:PactPrerequisiteDownloadFailed}'), [GetExceptionMessage]);
        Exit;
      end;
    finally
      DownloadPage.Hide;
    end;
    DotNetInstaller := ExpandConstant('{tmp}\{#PactDotNetInstallerFileName}');
    Result := InstallAndCheck(DotNetInstaller, '/install /quiet /norestart', '.NET Runtime', True, NeedsRestart);
    if Result <> '' then
      Exit;
  end;

  if WebView2Missing then begin
    ExtractTemporaryFile('{#PactWebView2BootstrapperFileName}');
    WebView2Installer := ExpandConstant('{tmp}\{#PactWebView2BootstrapperFileName}');
    if CompareText(
        GetSHA256OfFile(WebView2Installer),
        '{#PactWebView2BootstrapperSha256}') <> 0 then begin
      Result := ExpandConstant('{cm:PactWebView2HashFailed}');
      Exit;
    end;
    Result := InstallAndCheck(WebView2Installer, '/silent /install', 'Microsoft Edge WebView2 Runtime', False, NeedsRestart);
    if Result <> '' then
      Exit;
  end;

  RefreshPrerequisiteState;
  if DotNetMissing or WebView2Missing then
    Result := ExpandConstant('{cm:PactPrerequisiteStillMissing}');
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

[CustomMessages]
english.PactPrerequisitesConsent=PACT needs to install these prerequisites before continuing:
russian.PactPrerequisitesConsent=Перед продолжением PACT необходимо установить следующие компоненты:
english.PactPrerequisitesHeading=Prerequisites:
russian.PactPrerequisitesHeading=Необходимые компоненты:
english.PactDotNetDetected=.NET Runtime {#PactDotNetMajorVersion} x64: detected
russian.PactDotNetDetected=.NET Runtime {#PactDotNetMajorVersion} x64: найден
english.PactDotNetMissing=.NET Runtime {#PactDotNetMajorVersion} x64: download required; Windows may request administrator approval
russian.PactDotNetMissing=.NET Runtime {#PactDotNetMajorVersion} x64: требуется загрузка; Windows может запросить права администратора
english.PactWebView2Detected=Microsoft Edge WebView2 Runtime {#PactWebView2MinimumVersion} or newer: detected
russian.PactWebView2Detected=Microsoft Edge WebView2 Runtime {#PactWebView2MinimumVersion} или новее: найден
english.PactWebView2Missing=Microsoft Edge WebView2 Runtime {#PactWebView2MinimumVersion} or newer: its embedded bootstrapper will download the current Runtime
russian.PactWebView2Missing=Microsoft Edge WebView2 Runtime {#PactWebView2MinimumVersion} или новее: встроенный загрузчик скачает актуальную версию Runtime
english.PactPrerequisiteLaunchFailed=Could not start the %1 installer.
russian.PactPrerequisiteLaunchFailed=Не удалось запустить установщик %1.
english.PactPrerequisiteSignatureFailed=%1 does not have the expected valid Microsoft signature.
russian.PactPrerequisiteSignatureFailed=У %1 отсутствует ожидаемая действительная подпись Microsoft.
english.PactPrerequisiteInstallFailed=%1 installation failed with exit code %2.
russian.PactPrerequisiteInstallFailed=Установка %1 завершилась с кодом ошибки %2.
english.PactPrerequisiteDownloadFailed=Could not download .NET Runtime: %1
russian.PactPrerequisiteDownloadFailed=Не удалось скачать .NET Runtime: %1
english.PactWebView2HashFailed=The embedded WebView2 bootstrapper failed its SHA-256 verification.
russian.PactWebView2HashFailed=Встроенный загрузчик WebView2 не прошёл проверку SHA-256.
english.PactPrerequisiteRestartRequired=Windows must be restarted before PACT can be installed. Restart, then run Setup again.
russian.PactPrerequisiteRestartRequired=Перед установкой PACT необходимо перезапустить Windows. После перезапуска снова запустите установщик.
english.PactPrerequisiteStillMissing=A prerequisite is still unavailable after its installer completed. PACT was not installed.
russian.PactPrerequisiteStillMissing=После завершения установки необходимый компонент всё ещё недоступен. PACT не был установлен.
english.PactDowngradeBlocked=PACT %1 is already installed. Setup %2 cannot replace it with an older version.
russian.PactDowngradeBlocked=Уже установлен PACT %1. Установщик %2 не может заменить его более старой версией.

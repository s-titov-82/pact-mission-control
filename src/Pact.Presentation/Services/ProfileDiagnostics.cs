using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Sessions;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Terminal;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Services;

/// <summary>Checks a configured launch command without starting an interactive session.</summary>
public sealed class ProfileDiagnostics(AppPaths paths, IExecutableLocator locator)
{
	/// <summary>Reports command discovery, prerequisites, skill roots and observed process or MCP activity.</summary>
	public async Task<string> CheckAsync(AgentProfileRecord profile, IEnumerable<SessionViewModel> sessions,
		Func<string, bool> initialized, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(initialized);
		var lines = new List<string>();
		var matching = sessions.Where(session =>
			session.Record.LaunchProfileId == profile.Id && session.Record.Kind == profile.Kind
			&& session.Record.LaunchCommand == profile.CommandTemplate
			&& string.Equals(session.Record.NativeConfigDirectory, profile.NativeConfigDirectory, StringComparison.OrdinalIgnoreCase)).ToList();
		lines.Add(matching.Any(s => s.LastStartedMode.HasValue)
			? "Launch: terminal process started." : "Launch: Not tested. Open a test terminal in ROOT.");
		lines.Add(matching.Any(s => s.LastStartedMode == TerminalStartMode.Resume)
			? "Restore: started the command with a captured conversation ID."
			: matching.Any(s => s.LastStartedMode == TerminalStartMode.ResumeSelection)
				? "Restore: started the conversation picker. Conversation selection is unverified."
				: "Restore: Not tested.");
		lines.Add(matching.Any(s => initialized(s.Record.Id))
			? "MCP: authenticated initialization observed." : "MCP: Not tested.");

		var shell = locator.FindOnPath(profile.DefaultShell);
		lines.Add(shell is null ? $"Configured shell: '{profile.DefaultShell}' missing. Set the executable path or install the shell."
			: $"Configured shell: {shell}");
		try
		{
			ConptyLibrary.EnsureBundleAvailable(AppContext.BaseDirectory);
			lines.Add("Bundled ConPTY files: present.");
		}
		catch (InvalidOperationException ex)
		{
			lines.Add(ex.Message);
		}

		if (profile.Kind is AgentKind.Codex or AgentKind.Claude)
		{
			if (profile.NativeConfigDirectory is { } root && !Path.IsPathFullyQualified(root))
			{
				lines.Add("Skill directories: choose an absolute native configuration directory.");
			}
			else
			{
				try
				{
					foreach (var directory in new SkillActivationService(paths).SkillDirectories(profile, null))
					{
						lines.Add($"Skills ({(Directory.Exists(directory) ? "present" : "absent")}): {directory}");
					}
				}
				catch (InvalidOperationException ex)
				{
					lines.Add($"Skill directories: {ex.Message}");
				}
			}
		}
		var command = profile.CommandTemplate.Trim();
		if (!TerminalCommandResolver.TrySplitCommandLine(command, out var executable, out _)
			|| executable.IndexOfAny(['$', '&', ';', '|', '`', '(', ')', '\'', '*', '?', '<', '>']) >= 0
			|| (!Path.IsPathFullyQualified(executable) && executable.IndexOfAny(['/', '\\']) >= 0))
		{
			lines.Add("CLI: command depends on the project directory or shell expression. Check it in a visible test terminal.");
		}
		else
		{
			lines.Add(await ProbeExecutableAsync(executable, profile.Kind, cancellationToken));
		}
		return string.Join(Environment.NewLine, lines);
	}

	private static async Task<string> ProbeExecutableAsync(string executable, AgentKind kind, CancellationToken cancellationToken)
	{
		// Resolve profile functions and aliases in the same PowerShell environment as a bare launch.
		// The configured token travels as data. Diagnostic execution never evaluates its arguments.
		const string script = """
			$ErrorActionPreference = 'Stop'
			$c = Get-Command -Name ([System.Management.Automation.WildcardPattern]::Escape($env:PACT_CHECK_CLI)) -ErrorAction SilentlyContinue | Select-Object -First 1
			if (!$c) { exit 3 }
			if ($c.CommandType -eq 'Alias') { $c = $c.ResolvedCommand }
			$label = if ($c.Path) { $c.Path } else { "$($c.CommandType) $($c.Name)" }
			[Console]::WriteLine("PACT-CLI $label")
			if ($env:PACT_CHECK_VERSION -eq 'yes') {
				$v = & $c --version 2>$null | Select-Object -First 1
				if (!$? -or $LASTEXITCODE -or !$v) { exit 4 }
				[Console]::WriteLine("PACT-VERSION $v")
			}
			""";
		var options = new ProcessStartInfo("pwsh")
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardInput = true,
			StandardOutputEncoding = Encoding.UTF8
		};
		foreach (var arg in new[] { "-NoLogo", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
		{
			options.ArgumentList.Add(arg);
		}
		options.Environment["PACT_CHECK_CLI"] = executable;
		var shellCommand = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant() is "cmd" or "powershell" or "pwsh";
		options.Environment["PACT_CHECK_VERSION"] = (kind is AgentKind.Codex or AgentKind.Claude) && !shellCommand ? "yes" : "no";
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(10));
		using var process = new Process { StartInfo = options };
		try
		{
			process.Start();
			process.StandardInput.Close();
			string? found = null;
			string? version = null;
			while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
			{
				if (line.StartsWith("PACT-CLI ", StringComparison.Ordinal))
				{
					found = line[9..];
				}
				if (line.StartsWith("PACT-VERSION ", StringComparison.Ordinal))
				{
					version = line[13..];
				}
			}
			await process.WaitForExitAsync(timeout.Token);
			if (found is null)
			{
				return $"CLI: '{executable}' could not be resolved. Check PATH, the PowerShell profile or the executable path.";
			}
			return $"CLI: {found}{Environment.NewLine}Executable version: {version ?? "Not tested. Run the CLI version command in the test terminal."}";
		}
		catch (OperationCanceledException)
		{
			try
			{
				if (!process.HasExited)
				{
					process.Kill(entireProcessTree: true);
				}
			}
			catch (InvalidOperationException)
			{
				// The child exited between the state check and Kill.
			}
			cancellationToken.ThrowIfCancellationRequested();
			return "CLI check timed out. Check the PowerShell profile and run the CLI version command in the test terminal.";
		}
		catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
		{
			return $"CLI check failed. Verify that pwsh is available on PATH. {ex.Message}";
		}
	}
}

using System.ComponentModel;
using System.Diagnostics;
using Pact.Core.Platform;

namespace Pact.Infrastructure.Orchestrator;

/// <summary>Creates profiles through the installed Hermes command-line interface.</summary>
public sealed class HermesCli : IHermesCli
{
	private readonly IExecutableLocator _executableLocator;

	/// <summary>Creates a Hermes command boundary using the ambient executable locator.</summary>
	public HermesCli(IExecutableLocator executableLocator)
	{
		ArgumentNullException.ThrowIfNull(executableLocator);
		_executableLocator = executableLocator;
	}

	/// <inheritdoc />
	public bool IsInstalled() => _executableLocator.FindOnPath("hermes") is not null;

	/// <inheritdoc />
	public async Task<HermesCliResult> CreateProfileAsync(
		string profileName,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

		var executable = _executableLocator.FindOnPath("hermes");
		if (executable is null)
		{
			return new HermesCliResult(
				Succeeded: false,
				string.Empty,
				"Hermes is not installed.");
		}

		ProcessStartInfo startInfo = new(executable)
		{
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false
		};
		startInfo.ArgumentList.Add("profile");
		startInfo.ArgumentList.Add("create");
		startInfo.ArgumentList.Add(profileName);

		try
		{
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return new HermesCliResult(false, string.Empty, "Hermes did not start.");
			}

			var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
			var output = await outputTask.ConfigureAwait(false);
			var error = await errorTask.ConfigureAwait(false);
			var combined = string.Join(
				Environment.NewLine,
				new[] { output.Trim(), error.Trim() }
					.Where(value => value.Length > 0));
			var profilePath = HermesHome.ProfileDirectory(
				HermesHome.ResolveRoot(),
				profileName);
			return new HermesCliResult(process.ExitCode == 0, profilePath, combined);
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
		{
			return new HermesCliResult(false, string.Empty, ex.Message);
		}
	}
}

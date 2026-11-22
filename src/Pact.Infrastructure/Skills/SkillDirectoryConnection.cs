using System.Diagnostics;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Skills;

internal static class SkillDirectoryConnection
{
	internal static void Validate(SkillLinkEdit link)
	{
		if (link.ProjectionRoot is not null)
		{
			SkillActivationService.ValidateProjection(link);
		}
		if (!link.Create)
		{
			if (!File.Exists(link.ReceiptPath) || File.ReadAllText(link.ReceiptPath) != link.Target
				|| new DirectoryInfo(link.Path).LinkTarget == null || !PointsToTarget(link))
			{
				throw new IOException($"The skill connection at {link.Path} changed after preview.");
			}
			return;
		}

		if (!string.Equals(SkillActivationService.ResolveDirectory(link.Target), Path.GetFullPath(link.Target), StringComparison.OrdinalIgnoreCase)
			|| File.Exists(link.ReceiptPath) || Path.Exists(link.Path)
			|| new DirectoryInfo(link.Path).LinkTarget != null || !File.Exists(Path.Combine(link.Target, "SKILL.md")))
		{
			throw new IOException($"The skill connection at {link.Path} changed after preview.");
		}
	}

	internal static async Task ApplyAsync(SkillLinkEdit link, CancellationToken token)
	{
		Validate(link);
		token.ThrowIfCancellationRequested();
		if (!link.Create)
		{
			Directory.Delete(link.Path);
			try
			{ File.Delete(link.ReceiptPath); }
			catch
			{
				await CreateLinkAsync(link);
				throw;
			}
			return;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(link.Path)!);
		await CreateLinkAsync(link);
		try
		{
			await AtomicFileWriter.WriteTextAsync(link.ReceiptPath, link.Target, token);
		}
		catch
		{
			if (PointsToTarget(link))
			{
				Directory.Delete(link.Path);
			}

			throw;
		}
	}

	private static bool PointsToTarget(SkillLinkEdit link) => string.Equals(
		SkillActivationService.ResolveDirectory(link.Path), SkillActivationService.ResolveDirectory(link.Target),
		StringComparison.OrdinalIgnoreCase);

	internal static async Task CreateLinkAsync(SkillLinkEdit link)
	{
		if (!OperatingSystem.IsWindows())
		{
			Directory.CreateSymbolicLink(link.Path, link.Target);
			return;
		}
		// Junctions work without Developer Mode. Never interpolate user paths into PowerShell source.
		var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
			"WindowsPowerShell", "v1.0", "powershell.exe"))
		{
			CreateNoWindow = true,
			UseShellExecute = false,
			RedirectStandardError = true
		};
		foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command",
			"$ErrorActionPreference='Stop'; New-Item -Path $env:PACT_LINK_PATH -ItemType Junction -Target $env:PACT_LINK_TARGET | Out-Null" })
		{
			start.ArgumentList.Add(arg);
		}

		start.Environment["PACT_LINK_PATH"] = link.Path;
		start.Environment["PACT_LINK_TARGET"] = link.Target;
		using var process = Process.Start(start) ?? throw new IOException("Could not create the skill junction.");
		var error = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		if (process.ExitCode != 0 || !PointsToTarget(link))
		{
			throw new IOException($"Could not connect {link.Path} to {link.Target}. {error.Trim()}");
		}
	}
}

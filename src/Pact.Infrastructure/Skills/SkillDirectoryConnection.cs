using System.Diagnostics;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Skills;

internal static partial class SkillDirectoryConnection
{
	internal static void Validate(SkillLinkEdit link)
	{
		if (link.ProjectionRoot is not null)
		{
			SkillActivationService.ValidateProjection(link);
		}
		if (!link.Create)
		{
			var receipt = ReadReceipt(link.ReceiptPath);
			if (receipt != (link.ConnectionReceipt ?? FormatReceipt(link.Target, link.Path)) ||
				ReadTarget(receipt, link.Path) != link.Target
				|| new DirectoryInfo(link.Path).LinkTarget == null || !PointsToTarget(link))
			{
				throw new IOException($"The skill connection at {link.Path} changed after preview.");
			}
			return;
		}

		if (!string.Equals(SkillActivationService.ResolveDirectory(link.Target), Path.GetFullPath(link.Target), StringComparison.OrdinalIgnoreCase)
			|| ReadReceipt(link.ReceiptPath) != link.PreviousReceipt || Path.Exists(link.Path)
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
			{
				if (link.PreviousReceipt is null)
				{
					File.Delete(link.ReceiptPath);
				}
				else
				{
					await AtomicFileWriter.WriteTextAsync(link.ReceiptPath, link.PreviousReceipt, token);
				}
			}
			catch
			{
				await CreateLinkAsync(link, CancellationToken.None);
				throw;
			}
			return;
		}
		Directory.CreateDirectory(Path.GetDirectoryName(link.Path)!);
		await CreateLinkAsync(link, token);
		try
		{
			await AtomicFileWriter.WriteTextAsync(link.ReceiptPath, link.ConnectionReceipt ?? FormatReceipt(link.Target, link.Path), token);
		}
		catch (Exception failure)
		{
			try
			{
				RemoveCreatedLink(link);
			}
			catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
			{
				throw new AggregateException($"Could not roll back the skill connection at {link.Path}.", failure, cleanup);
			}

			throw;
		}
	}

	internal static void RemoveCreatedLink(SkillLinkEdit link)
	{
		if (new DirectoryInfo(link.Path).LinkTarget is not null)
		{
			if (PointsToTarget(link))
			{
				Directory.Delete(link.Path);
				return;
			}
		}
		else
		{
			try
			{
				_ = File.GetAttributes(link.Path);
			}
			catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
			{
				return;
			}
		}
		throw new IOException($"The new skill connection at {link.Path} changed during rollback.");
	}

	private static bool PointsToTarget(SkillLinkEdit link) => string.Equals(
		SkillActivationService.ResolveDirectory(link.Path), SkillActivationService.ResolveDirectory(link.Target),
		StringComparison.OrdinalIgnoreCase);

	private static string? ReadReceipt(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

	internal static string FormatReceipt(string target, string path) => target + "\n" + path;

	internal static string? RecordedPath(string receipt)
	{
		var separator = receipt.IndexOf('\n', StringComparison.Ordinal);
		return separator < 0 ? null : receipt[(separator + 1)..];
	}

	internal static string? ReadTarget(string? receipt, string? expectedPath = null)
	{
		if (receipt is null)
		{
			return null;
		}
		var separator = receipt.IndexOf('\n', StringComparison.Ordinal);
		return separator < 0 ? receipt :
			expectedPath is null || string.Equals(receipt[(separator + 1)..], expectedPath, StringComparison.OrdinalIgnoreCase)
				? receipt[..separator] : null;
	}

	internal static Task CreateLinkAsync(SkillLinkEdit link, CancellationToken token = default)
	{
		token.ThrowIfCancellationRequested();
		if (!OperatingSystem.IsWindows())
		{
			Directory.CreateSymbolicLink(link.Path, link.Target);
			return Task.CompletedTask;
		}
		// Stage on the destination volume. The parent of a verified projection lies outside
		// the live --add-dir tree, so a leftover stage cannot invalidate that projection.
		var parent = Path.GetDirectoryName(link.ProjectionRoot ?? link.Path)!;
		var staging = Directory.CreateDirectory(Path.Combine(parent, ".pact-skill-" + Path.GetRandomFileName())).FullName;
		var staged = link with { Path = Path.Combine(staging, "j") };
		try
		{
			CreateJunction(staged.Path, staged.Target);
			if (new DirectoryInfo(staged.Path).LinkTarget is null || !PointsToTarget(staged))
			{
				throw new IOException($"Could not verify the staged skill connection to {link.Target} at {staged.Path}.");
			}
			// A failed removal restores its old link even if the package has since changed.
			if (link.Create)
			{
				VerifyManifestThroughJunction(staged);
			}

			token.ThrowIfCancellationRequested();
			Directory.Move(staged.Path, link.Path);
		}
		catch (Exception failure)
		{
			try
			{
				var info = new DirectoryInfo(staged.Path);
				if (info.LinkTarget is null && info.Exists && !info.EnumerateFileSystemInfos().Any())
				{
					Directory.Delete(staged.Path);
				}
				else
				{
					RemoveCreatedLink(staged);
				}
				Directory.Delete(staging);
			}
			catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
			{
				throw new AggregateException($"Could not clean up the skill connection staging path {staging}.", failure, cleanup);
			}
			throw;
		}
		try
		{
			Directory.Delete(staging);
		}
		catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
		{
			Trace.TraceWarning($"The skill connection at {link.Path} was created. Cleanup is pending at {staging}: {cleanup.Message}");
		}
		return Task.CompletedTask;
	}

	private static void VerifyManifestThroughJunction(SkillLinkEdit staged)
	{
		// Reparse metadata can match even when Windows cannot traverse the junction.
		try
		{
			using var manifest = File.OpenRead(Path.Combine(staged.Path, "SKILL.md"));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			throw new IOException($"Could not read SKILL.md through the staged skill junction at {staged.Path} to {staged.Target}.", ex);
		}
	}
}

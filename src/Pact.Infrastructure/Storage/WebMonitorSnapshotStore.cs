using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pact.Core.Web.Monitoring;

namespace Pact.Infrastructure.Storage;

/// <summary>Stores retained monitoring snapshots in one validated JSON file per saved web page.</summary>
public sealed class WebMonitorSnapshotStore
{
	private const string HashedFileNamePrefix = "pact-web-monitor-";
	private const int MaximumDirectWebPageIdLength = 128;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly AppPaths _paths;

	/// <summary>Creates a store rooted at the supplied Pact data paths.</summary>
	public WebMonitorSnapshotStore(AppPaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		_paths = paths;
	}

	/// <summary>Loads the retained snapshot for a valid web-page identifier, or <see langword="null"/> when none exists.</summary>
	public async Task<WebMonitorSnapshot?> LoadAsync(string webPageId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var path = SnapshotPath(webPageId);
		WebMonitorSnapshot? snapshot;
		try
		{
			await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			snapshot = await JsonSerializer.DeserializeAsync<WebMonitorSnapshot>(stream, JsonOptions, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
		catch (System.Security.SecurityException)
		{
			return null;
		}
		catch (JsonException)
		{
			DeleteFileBestEffort(path);
			return null;
		}

		if (!IsValidSnapshot(snapshot))
		{
			DeleteFileBestEffort(path);
			return null;
		}

		var validSnapshot = snapshot!;
		if (!string.Equals(validSnapshot.WebPageId, webPageId, StringComparison.Ordinal))
		{
			DeleteFileBestEffort(path);
			return null;
		}

		return validSnapshot;
	}

	/// <summary>Saves a snapshot atomically using the disposable session staging directory.</summary>
	public Task SaveAsync(WebMonitorSnapshot snapshot, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		var path = SnapshotPath(snapshot.WebPageId);
		var json = JsonSerializer.Serialize(snapshot, JsonOptions);
		return AtomicFileWriter.WriteTextAsync(path, json, _paths.AtomicTempDirectory, cancellationToken);
	}

	/// <summary>Deletes the retained snapshot for a valid web-page identifier when it exists.</summary>
	public Task DeleteAsync(string webPageId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var path = SnapshotPath(webPageId);
		if (File.Exists(path))
		{
			File.Delete(path);
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// Deletes malformed, orphaned, and filename-mismatched retained snapshots, preserving only supplied web-page IDs.
	/// </summary>
	public async Task SweepAsync(IReadOnlySet<string> existingWebPageIds, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(existingWebPageIds);

		HashSet<string> validIds = new(StringComparer.Ordinal);
		foreach (var webPageId in existingWebPageIds)
		{
			validIds.Add(ValidateWebPageId(webPageId));
		}

		if (!Directory.Exists(_paths.WebMonitorSnapshotsDirectory))
		{
			return;
		}

		foreach (var path in Directory.EnumerateFiles(_paths.WebMonitorSnapshotsDirectory, "*.json"))
		{
			cancellationToken.ThrowIfCancellationRequested();
			WebMonitorSnapshot? snapshot;
			try
			{
				await using var stream = File.OpenRead(path);
				snapshot = await JsonSerializer.DeserializeAsync<WebMonitorSnapshot>(stream, JsonOptions, cancellationToken)
					.ConfigureAwait(false);
			}
			catch (JsonException)
			{
				DeleteFileBestEffort(path);
				continue;
			}

			if (!IsValidSnapshot(snapshot))
			{
				DeleteFileBestEffort(path);
				continue;
			}

			var validSnapshot = snapshot!;
			if (
				!string.Equals(
					Path.GetFileName(path),
					SnapshotFileName(validSnapshot.WebPageId),
					StringComparison.Ordinal) ||
				!validIds.Contains(validSnapshot.WebPageId))
			{
				DeleteFileBestEffort(path);
			}
		}
	}

	private string SnapshotPath(string webPageId) => Path.Combine(
		_paths.WebMonitorSnapshotsDirectory,
		SnapshotFileName(webPageId));

	private static string SnapshotFileName(string webPageId)
	{
		webPageId = ValidateWebPageId(webPageId);
		return IsCanonicalDirectWebPageId(webPageId)
			? webPageId + ".json"
			: HashedFileNamePrefix + Convert.ToHexString(
				SHA256.HashData(Encoding.UTF8.GetBytes(webPageId))).ToLowerInvariant() + ".json";
	}

	private static string ValidateWebPageId(string webPageId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);

		if (webPageId.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
			webPageId.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
		{
			throw new ArgumentException("The web-page ID must not contain a directory separator.", nameof(webPageId));
		}

		return webPageId;
	}

	private static bool IsCanonicalDirectWebPageId(string webPageId)
	{
		if (webPageId.Length is 0 or > MaximumDirectWebPageIdLength ||
			webPageId.StartsWith(HashedFileNamePrefix, StringComparison.Ordinal) ||
			IsReservedWindowsFileName(webPageId) ||
			!IsLowercaseAsciiLetterOrDigit(webPageId[0]))
		{
			return false;
		}

		return webPageId.All(character =>
			IsLowercaseAsciiLetterOrDigit(character) || character is '-' or '_');
	}

	private static bool IsReservedWindowsFileName(string webPageId)
	{
		return webPageId is "con" or "prn" or "aux" or "nul" ||
			   (webPageId.Length == 4 &&
				(webPageId.StartsWith("com", StringComparison.Ordinal) ||
				 webPageId.StartsWith("lpt", StringComparison.Ordinal)) &&
				webPageId[3] is >= '1' and <= '9');
	}

	private static bool IsLowercaseAsciiLetterOrDigit(char character) =>
		character is >= 'a' and <= 'z' or >= '0' and <= '9';

	private static bool IsValidSnapshot(WebMonitorSnapshot? snapshot)
	{
		if (snapshot is null ||
			string.IsNullOrWhiteSpace(snapshot.WebPageId) ||
			string.IsNullOrWhiteSpace(snapshot.Url) ||
			string.IsNullOrWhiteSpace(snapshot.RuleId) ||
			string.IsNullOrWhiteSpace(snapshot.RuleFingerprint) ||
			!IsValidWebPageId(snapshot.WebPageId) ||
			snapshot.ObservedAt == default ||
			!Uri.TryCreate(snapshot.Url, UriKind.Absolute, out var uri))
		{
			return false;
		}

		return uri is not null && string.IsNullOrEmpty(uri.Fragment);
	}

	private static bool IsValidWebPageId(string webPageId) =>
		!webPageId.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
		!webPageId.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

	private static void DeleteFileBestEffort(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
			// Invalid retained data must not block startup if another process owns the file.
		}
		catch (UnauthorizedAccessException)
		{
			// Invalid retained data will be retried by the next load or startup sweep.
		}
	}
}
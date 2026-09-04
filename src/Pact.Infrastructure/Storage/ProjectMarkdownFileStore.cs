using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Pact.Core.Projects;

namespace Pact.Infrastructure.Storage;

/// <summary>
/// Persists project Markdown files atomically and rejects writes based on stale revisions.
/// </summary>
public sealed class ProjectMarkdownFileStore : IProjectMarkdownFileStore
{
	private const string MissingRevision = "missing";
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
		new(StringComparer.OrdinalIgnoreCase);

	/// <inheritdoc />
	public async Task<ProjectMarkdownFileSnapshot> LoadAsync(
		string path,
		CancellationToken cancellationToken)
	{
		var fullPath = NormalizePath(path);
		var gate = GetLock(fullPath);
		await gate.WaitAsync(cancellationToken);
		try
		{
			return await LoadUnlockedAsync(fullPath, cancellationToken);
		}
		finally
		{
			gate.Release();
		}
	}

	/// <inheritdoc />
	public async Task<ProjectMarkdownSaveResult> TrySaveAsync(
		string path,
		string text,
		string expectedRevision,
		CancellationToken cancellationToken)
	{
		var fullPath = NormalizePath(path);
		var gate = GetLock(fullPath);
		await gate.WaitAsync(cancellationToken);
		try
		{
			var current = await LoadUnlockedAsync(fullPath, cancellationToken);
			if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
			{
				return new ProjectMarkdownSaveResult(false, current);
			}

			await AtomicFileWriter.WriteTextAsync(fullPath, text, cancellationToken);
			return new ProjectMarkdownSaveResult(
				true,
				new ProjectMarkdownFileSnapshot(true, text, RevisionFor(text)));
		}
		finally
		{
			gate.Release();
		}
	}

	/// <inheritdoc />
	public async Task<ProjectMarkdownFileSnapshot> OverwriteAsync(
		string path,
		string text,
		CancellationToken cancellationToken)
	{
		var fullPath = NormalizePath(path);
		var gate = GetLock(fullPath);
		await gate.WaitAsync(cancellationToken);
		try
		{
			await AtomicFileWriter.WriteTextAsync(fullPath, text, cancellationToken);
			return new ProjectMarkdownFileSnapshot(true, text, RevisionFor(text));
		}
		finally
		{
			gate.Release();
		}
	}

	private static async Task<ProjectMarkdownFileSnapshot> LoadUnlockedAsync(
		string path,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(path))
		{
			return new ProjectMarkdownFileSnapshot(false, string.Empty, MissingRevision);
		}

		var text = await File.ReadAllTextAsync(path, cancellationToken);
		return new ProjectMarkdownFileSnapshot(true, text, RevisionFor(text));
	}

	private static string RevisionFor(string text) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

	private static string NormalizePath(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		return Path.GetFullPath(path);
	}

	private static SemaphoreSlim GetLock(string path) =>
		Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
}
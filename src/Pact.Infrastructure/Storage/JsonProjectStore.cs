using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Projects;

namespace Pact.Infrastructure.Storage;

/// <summary>
/// Stores <see cref="ProjectsDocument"/> as camel-cased JSON in <c>Settings/projects.json</c>.
/// Writes are atomic and serialized through a per-path lock, so concurrent callers cannot
/// interleave read-modify-write cycles and lose each other's changes.
/// </summary>
public sealed class JsonProjectStore : IProjectStore
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private readonly AppPaths _paths;

	/// <summary>
	/// Creates a store over the layout derived from <paramref name="rootDirectory"/>.
	/// </summary>
	public JsonProjectStore(string rootDirectory)
		: this(new AppPaths(rootDirectory))
	{
	}

	/// <summary>
	/// Creates a store over an existing path layout.
	/// </summary>
	public JsonProjectStore(AppPaths paths)
	{
		_paths = paths;
	}

	/// <inheritdoc />
	public async Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken)
	{
		return await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(document);

		await WithProjectLockAsync(
			async () =>
			{
				await SaveUnlockedAsync(document, cancellationToken).ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public async Task<ProjectsDocument> UpdateAsync(
		Func<ProjectsDocument, ProjectsDocument> update,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(update);

		return await WithProjectLockAsync(
			async () =>
			{
				var document = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
				var updated = update(document);
				await SaveUnlockedAsync(updated, cancellationToken).ConfigureAwait(false);
				return updated;
			},
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<ProjectsDocument> LoadUnlockedAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_paths.ProjectsPath))
		{
			return ProjectsDocument.CreateDefault();
		}

		await using var stream = File.OpenRead(_paths.ProjectsPath);
		var document = await JsonSerializer.DeserializeAsync<ProjectsDocument>(
			stream,
			JsonOptions,
			cancellationToken);

		return document ?? ProjectsDocument.CreateDefault();
	}

	private async Task SaveUnlockedAsync(ProjectsDocument document, CancellationToken cancellationToken)
	{
		var json = JsonSerializer.Serialize(document, JsonOptions);
		await AtomicFileWriter.WriteTextAsync(
			_paths.ProjectsPath,
			json,
			_paths.AtomicTempDirectory,
			cancellationToken);
	}

	private async Task<T> WithProjectLockAsync<T>(
		Func<Task<T>> action,
		CancellationToken cancellationToken)
	{
		var projectsPath = Path.GetFullPath(_paths.ProjectsPath);
		var processLock = ProcessLocks.GetOrAdd(
			projectsPath,
			static _ => new SemaphoreSlim(1, 1));

		await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		ProjectSemaphoreHandle? semaphoreHandle = null;
		try
		{
			semaphoreHandle = await ProjectSemaphoreHandle.WaitAsync(projectsPath, cancellationToken)
				.ConfigureAwait(false);
			return await action().ConfigureAwait(false);
		}
		finally
		{
			try
			{
				semaphoreHandle?.Dispose();
			}
			finally
			{
				processLock.Release();
			}
		}
	}

	private sealed class ProjectSemaphoreHandle : IDisposable
	{
		private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

		private readonly Semaphore? _semaphore;
		private readonly bool _ownsSemaphore;

		private ProjectSemaphoreHandle(Semaphore? semaphore, bool ownsSemaphore)
		{
			_semaphore = semaphore;
			_ownsSemaphore = ownsSemaphore;
		}

		public static async Task<ProjectSemaphoreHandle> WaitAsync(
			string projectsPath,
			CancellationToken cancellationToken)
		{
			Semaphore? semaphore = null;
			try
			{
				semaphore = new Semaphore(1, 1, CreateSemaphoreName(projectsPath));
				await WaitUntilEnteredAsync(semaphore, cancellationToken).ConfigureAwait(false);
				return new ProjectSemaphoreHandle(semaphore, ownsSemaphore: true);
			}
			catch (PlatformNotSupportedException)
			{
				semaphore?.Dispose();
				return new ProjectSemaphoreHandle(null, ownsSemaphore: false);
			}
			catch (UnauthorizedAccessException)
			{
				semaphore?.Dispose();
				return new ProjectSemaphoreHandle(null, ownsSemaphore: false);
			}
			catch
			{
				semaphore?.Dispose();
				throw;
			}
		}

		public void Dispose()
		{
			if (_ownsSemaphore)
			{
				_semaphore?.Release();
			}

			_semaphore?.Dispose();
		}

		private static async Task WaitUntilEnteredAsync(Semaphore semaphore, CancellationToken cancellationToken)
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (semaphore.WaitOne(PollInterval))
				{
					return;
				}

				await Task.Yield();
			}
		}

		private static string CreateSemaphoreName(string projectsPath)
		{
			var pathBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(projectsPath).ToUpperInvariant());
			var pathHash = Convert.ToHexString(SHA256.HashData(pathBytes));
			return $"Pact.Projects.{pathHash}";
		}
	}
}
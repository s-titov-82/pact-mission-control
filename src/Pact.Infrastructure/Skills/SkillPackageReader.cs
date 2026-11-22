using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Pact.Infrastructure.Git;

namespace Pact.Infrastructure.Skills;

internal sealed class SkillPackageReader(string temporaryDirectory, HttpClient http)
{
	private const int MaxBytes = 128 * 1024 * 1024;
	private const int MaxFiles = 32_768;
	private readonly GitCliRunner _git = new();

	internal async Task<IReadOnlyList<CatalogSkill>> ReadAsync(string source, bool native, string? reference, CancellationToken token)
	{
		if (!Path.IsPathFullyQualified(source))
		{
			return await ReadGitHubAsync(source, reference, token).ConfigureAwait(false);
		}

		source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
		if (!Directory.Exists(source))
		{
			if (native)
			{
				return [];
			}

			throw new DirectoryNotFoundException(source);
		}

		if (!native && (Directory.Exists(Path.Combine(source, ".git")) || File.Exists(Path.Combine(source, ".git"))))
		{
			return await ReadGitAsync(source, reference, token).ConfigureAwait(false);
		}

		var packages = new List<CatalogSkill>();
		var pending = new Stack<string>();
		var budget = new ReadBudget();
		pending.Push(source);
		while (pending.TryPop(out var directory))
		{
			token.ThrowIfCancellationRequested();
			if (File.Exists(Path.Combine(directory, "SKILL.md")))
			{
				Dictionary<string, byte[]> content;
				string? warning = null;
				try
				{
					var actualDirectory = native && IsLink(directory)
						? Directory.ResolveLinkTarget(directory, returnFinalTarget: true)?.FullName ?? throw new IOException($"Broken skill link: {directory}")
						: directory;
					content = ReadDirectory(actualDirectory, budget, token, skipGitMetadata: native);
				}
				catch (Exception ex) when (native && ex is IOException or UnauthorizedAccessException)
				{
					warning = ex.Message;
					content = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
					var manifest = Path.Combine(directory, "SKILL.md");
					try
					{
						if (!IsLink(manifest) && new FileInfo(manifest).Length <= 1024 * 1024)
						{
							content["SKILL.md"] = await File.ReadAllBytesAsync(manifest, token).ConfigureAwait(false);
						}
					}
					catch (Exception readError) when (readError is IOException or UnauthorizedAccessException)
					{
						warning = readError.Message;
					}
				}

				packages.Add(new CatalogSkill(Path.GetFileName(directory), source, Path.GetRelativePath(source, directory).Replace('\\', '/'),
					null, null, Notes(content), content, native ? Path.Combine(directory, "SKILL.md") : null, warning));
				continue;
			}

			try
			{
				foreach (var child in Directory.EnumerateDirectories(directory))
				{
					if (!Ignored(child) && (!IsLink(child) || native && File.Exists(Path.Combine(child, "SKILL.md"))))
					{
						pending.Push(child);
					}
				}
			}
			catch (Exception ex) when (native && directory != source && ex is IOException or UnauthorizedAccessException)
			{
				// Unreadable grouping directories have no manifest to show. Other groups remain browsable.
			}
		}

		return packages;
	}

	internal static Dictionary<string, byte[]> ReadDirectory(string directory, CancellationToken token) =>
		ReadDirectory(directory, new ReadBudget(), token);

	private static Dictionary<string, byte[]> ReadDirectory(string root, ReadBudget budget, CancellationToken token, bool skipGitMetadata = false)
	{
		var content = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.TryPop(out var directory))
		{
			token.ThrowIfCancellationRequested();
			if (IsLink(directory))
			{
				throw new IOException($"Linked package path: {directory}");
			}

			foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
			{
				var gitMetadata = Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase);
				if (skipGitMetadata && gitMetadata)
				{
					continue;
				}

				if (IsLink(entry) || gitMetadata)
				{
					throw new IOException($"Unsupported package path: {entry}");
				}

				if (Directory.Exists(entry))
				{
					pending.Push(entry);
					continue;
				}

				var path = Path.GetRelativePath(root, entry).Replace('\\', '/');
				ValidatePath(path);
				using var stream = File.OpenRead(entry);
				budget.Charge(stream.Length);
				var bytes = new byte[(int)stream.Length];
				stream.ReadExactly(bytes);
				content.Add(path, bytes);
			}
		}

		return content;
	}

	private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

	internal static string? FindNameCollision(string root, string destination, string name)
	{
		if (!Directory.Exists(root))
		{
			return null;
		}

		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.TryPop(out var directory))
		{
			if (string.Equals(directory, destination, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var manifest = Path.Combine(directory, "SKILL.md");
			if (File.Exists(manifest))
			{
				using var reader = File.OpenText(manifest);
				if (string.Equals(CatalogSkill.ManifestName(reader, Path.GetFileName(directory)), name, StringComparison.OrdinalIgnoreCase))
				{
					return manifest;
				}

				continue;
			}

			foreach (var child in Directory.EnumerateDirectories(directory))
			{
				if (!Ignored(child) && (!IsLink(child) || File.Exists(Path.Combine(child, "SKILL.md"))))
				{
					pending.Push(child);
				}
			}
		}

		return null;
	}

	private static bool Ignored(string path) =>
		Path.GetFileName(path).Equals(".git", StringComparison.OrdinalIgnoreCase) ||
		Path.GetFileName(path).StartsWith(".pact-skill-", StringComparison.OrdinalIgnoreCase);

	private async Task<IReadOnlyList<CatalogSkill>> ReadGitAsync(string source, string? reference, CancellationToken token)
	{
		if (reference is not null)
		{
			var upstream = await GitAsync(source,
				["for-each-ref", "--format=%(upstream:remotename)%00%(upstream:remoteref)", "refs/heads/" + reference], token).ConfigureAwait(false);
			var fields = upstream.Split('\0');
			if (fields.Length == 2 && fields.All(field => field.Length != 0))
			{
				if (fields[0] == ".")
				{
					var localRevision = await GitAsync(source, ["rev-parse", "--verify", "--end-of-options", fields[1] + "^{commit}"], token).ConfigureAwait(false);
					return await ReadCommitAsync(source, source, reference, localRevision, token).ConfigureAwait(false);
				}

				return await FetchUpstreamAsync(source, reference, fields[0], fields[1], token).ConfigureAwait(false);
			}
		}

		if (reference is null)
		{
			var branch = await _git.RunAsync(source, ["symbolic-ref", "--quiet", "--short", "HEAD"], null, token).ConfigureAwait(false);
			reference = branch.Succeeded ? branch.StandardOutput.Trim() : null;
		}

		var revision = await GitAsync(source, ["rev-parse", "--verify", "--end-of-options", (reference ?? "HEAD") + "^{commit}"], token).ConfigureAwait(false);
		return await ReadCommitAsync(source, source, reference ?? revision, revision, token).ConfigureAwait(false);
	}

	private async Task<IReadOnlyList<CatalogSkill>> FetchUpstreamAsync(string source, string reference, string remote, string upstreamRef, CancellationToken token)
	{
		var address = await GitAsync(source, ["remote", "get-url", "--", remote], token).ConfigureAwait(false);
		if (!Path.IsPathFullyQualified(address) && !address.Contains(':', StringComparison.Ordinal))
		{
			address = Path.GetFullPath(address, source);
		}

		var scratch = Directory.CreateDirectory(Path.Combine(temporaryDirectory, "fetch-" + Guid.NewGuid().ToString("N"))).FullName;
		try
		{
			await GitAsync(scratch, ["init", "--bare", "--template="], token).ConfigureAwait(false);
			await GitAsync(scratch, ["-c", "credential.interactive=never", "-c", "protocol.allow=never",
				"-c", "protocol.file.allow=always", "-c", "protocol.https.allow=always", "-c", "protocol.http.allow=always",
				"-c", "protocol.ssh.allow=always", "-c", "protocol.git.allow=always", "fetch", "--no-tags",
				"--depth=1", "--", address, upstreamRef], token).ConfigureAwait(false);
			var revision = await GitAsync(scratch, ["rev-parse", "--verify", "FETCH_HEAD^{commit}"], token).ConfigureAwait(false);
			return await ReadCommitAsync(scratch, source, reference, revision, token).ConfigureAwait(false);
		}
		finally
		{
			foreach (var file in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories))
			{
				var attributes = File.GetAttributes(file);
				if ((attributes & FileAttributes.ReadOnly) != 0)
				{
					File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
				}
			}

			Directory.Delete(scratch, recursive: true);
		}
	}

	private async Task<IReadOnlyList<CatalogSkill>> ReadCommitAsync(string repository, string source, string reference, string revision, CancellationToken token)
	{
		Directory.CreateDirectory(temporaryDirectory);
		var archivePath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".zip");
		try
		{
			await GitAsync(repository, ["archive", "--format=zip", "--output", archivePath, revision], token).ConfigureAwait(false);
			using var archive = ZipFile.OpenRead(archivePath);
			return FromArchive(archive, source, Path.GetFileName(source), reference, revision, null, string.Empty, string.Empty, token);
		}
		finally
		{
			File.Delete(archivePath);
		}
	}

	private async Task<string> GitAsync(string source, string[] arguments, CancellationToken token)
	{
		var result = await _git.RunAsync(source, arguments, null, token).ConfigureAwait(false);
		return result.Succeeded ? result.StandardOutput.Trim() : throw new IOException(result.StandardError.Trim());
	}

	private async Task<IReadOnlyList<CatalogSkill>> ReadGitHubAsync(string source, string? reference, CancellationToken token)
	{
		if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
			uri.Host != "github.com" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
		{
			throw new ArgumentException("Choose an absolute directory or a public HTTPS GitHub repository URL.", nameof(source));
		}

		var parts = uri.AbsolutePath.Trim('/').Split('/');
		var latestRelease = parts.Length == 4 && parts[2] == "releases" && parts[3] == "latest";
		var releaseTag = parts.Length >= 5 && parts[2] == "releases" && parts[3] == "tag";
		if (parts.Length < 2 || (parts.Length != 2 && (parts.Length < 4 || parts[2] != "tree") && !latestRelease && !releaseTag))
		{
			throw new ArgumentException("Choose a repository, a /tree/branch/directory, or a release URL.", nameof(source));
		}

		var repository = parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1];
		var api = $"https://api.github.com/repos/{parts[0]}/{repository}";
		var subdirectory = string.Empty;
		string? sha = null;
		string? notes = null;
		if (latestRelease)
		{
			using var latest = await JsonAsync(api + "/releases/latest", token).ConfigureAwait(false) ?? throw new IOException("No published release was found.");
			reference = latest.RootElement.GetProperty("tag_name").GetString()!;
			notes = latest.RootElement.TryGetProperty("body", out var body) ? body.GetString() : null;
		}
		else if (releaseTag)
		{
			reference = Uri.UnescapeDataString(string.Join('/', parts[4..]));
		}
		else if (parts.Length > 2)
		{
			// Branch names can themselves contain slashes. Resolve the longest matching prefix.
			for (var count = parts.Length; count >= 4; count--)
			{
				var candidate = Uri.UnescapeDataString(string.Join('/', parts[3..count]));
				if (reference is not null && !string.Equals(candidate, reference, StringComparison.Ordinal))
				{
					continue;
				}

				using var match = await JsonAsync(api + "/commits/" + Uri.EscapeDataString(candidate), token).ConfigureAwait(false);
				if (match is not null)
				{
					subdirectory = Uri.UnescapeDataString(string.Join('/', parts[count..]));
					reference = candidate;
					sha = match.RootElement.GetProperty("sha").GetString();
					break;
				}

				if (count == 4)
				{
					throw new IOException($"GitHub could not resolve the repository reference in {source}.");
				}
			}
		}

		if (reference is null)
		{
			using var repo = await JsonAsync(api, token).ConfigureAwait(false) ?? throw new IOException($"Repository unavailable: {source}");
			reference = repo.RootElement.GetProperty("default_branch").GetString()!;
		}

		if (sha is null)
		{
			using var commit = await JsonAsync(api + "/commits/" + Uri.EscapeDataString(reference), token).ConfigureAwait(false)
				?? throw new IOException($"GitHub reference unavailable: {reference}");
			sha = commit.RootElement.GetProperty("sha").GetString()!;
		}

		if (notes is null)
		{
			using var release = await JsonAsync(api + "/releases/tags/" + Uri.EscapeDataString(reference), token).ConfigureAwait(false);
			notes = release?.RootElement.GetProperty("body").GetString();
		}

		var archiveBytes = await http.GetByteArrayAsync(new Uri(api + "/zipball/" + sha), token).ConfigureAwait(false);
		using var buffer = new MemoryStream(archiveBytes, writable: false);
		using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);
		var prefix = archive.Entries.FirstOrDefault()?.FullName.Split('/')[0] + "/";
		return FromArchive(archive, source, repository, reference, sha, notes, prefix, subdirectory, token);
	}

	private async Task<JsonDocument?> JsonAsync(string address, CancellationToken token)
	{
		using var response = await http.GetAsync(new Uri(address), token).ConfigureAwait(false);
		if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
		{
			return null;
		}

		response.EnsureSuccessStatusCode();
		using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
	}

	private static List<CatalogSkill> FromArchive(ZipArchive archive, string source, string repository,
		string reference, string revision, string? releaseNotes, string prefix, string subdirectory, CancellationToken token)
	{
		var packages = new List<CatalogSkill>();
		var budget = new ReadBudget();
		var entries = archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.Name.Length != 0)
			.ToDictionary(e => e.FullName[prefix.Length..], StringComparer.Ordinal);
		foreach (var path in entries.Keys.Where(path => Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
		{
			token.ThrowIfCancellationRequested();
			var directory = path[..^"SKILL.md".Length].TrimEnd('/');
			if (subdirectory.Length != 0 && directory != subdirectory && !directory.StartsWith(subdirectory + "/", StringComparison.Ordinal))
			{
				continue;
			}

			var packagePrefix = directory.Length == 0 ? string.Empty : directory + "/";
			var folder = directory.Length == 0 ? repository : directory.Split('/')[^1];
			ValidatePath(folder);
			var content = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
			foreach (var (fullPath, entry) in entries.Where(pair => pair.Key.StartsWith(packagePrefix, StringComparison.Ordinal)))
			{
				var relative = fullPath[packagePrefix.Length..];
				ValidatePath(relative);
				if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
				{
					throw new IOException($"Linked package path: {fullPath}");
				}

				budget.Charge(entry.Length);
				using var stream = entry.Open();
				var bytes = new byte[(int)entry.Length];
				stream.ReadExactly(bytes);
				content.Add(relative, bytes);
			}

			packages.Add(new CatalogSkill(folder, source, directory.Length == 0 ? "." : directory,
				reference, revision, releaseNotes ?? Notes(content), content, null));
		}

		return packages;
	}

	private static string? Notes(Dictionary<string, byte[]> content) =>
		content.TryGetValue("CHANGELOG.md", out var notes) || content.TryGetValue("RELEASE_NOTES.md", out notes)
			? System.Text.Encoding.UTF8.GetString(notes) : null;

	internal static void ValidatePath(string path)
	{
		foreach (var component in path.Split('/'))
		{
			var stem = component.Split('.')[0].ToUpperInvariant();
			if (component.Length == 0 || component.EndsWith('.') || component.EndsWith(' ') ||
				component.Contains('\\', StringComparison.Ordinal) || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
				component.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
				stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
				stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
				"0123456789¹²³".Contains(stem[3], StringComparison.Ordinal))
			{
				throw new IOException($"Unsupported package path: {path}");
			}
		}
	}

	private sealed class ReadBudget
	{
		private long _bytes;
		private int _files;

		internal void Charge(long bytes)
		{
			if (bytes > MaxBytes - _bytes || ++_files > MaxFiles)
			{
				throw new IOException("The catalog exceeds the 128 MiB or 32,768-file preview limit.");
			}

			_bytes += bytes;
		}
	}
}
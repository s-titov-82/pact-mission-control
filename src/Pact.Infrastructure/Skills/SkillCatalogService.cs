using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Skills;

/// <summary>Previews local and public skill packages and installs only a confirmed snapshot.</summary>
public sealed partial class SkillCatalogService
{
	private static readonly HttpClient PublicGitHub = CreateHttpClient();
	private readonly string _registry;
	private readonly string _skillsDirectory;
	private readonly SkillPackageReader _reader;
	private readonly string _userHome;

	/// <summary>Uses Pact's settings directory for source bindings and the supplied native skills directory for installations.</summary>
	public SkillCatalogService(string settingsDirectory, string userSkillsDirectory) : this(settingsDirectory, userSkillsDirectory, null)
	{
	}

	/// <summary>Resolves default Codex and Claude skill directories beneath the supplied home, or the current user profile.</summary>
	public SkillCatalogService(string settingsDirectory, string userSkillsDirectory, string? userHome)
	{
		_userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		_registry = Path.Combine(Path.GetFullPath(settingsDirectory), "skill-installations.json");
		_skillsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userSkillsDirectory));
		var dataRoot = Path.GetDirectoryName(Path.GetFullPath(settingsDirectory))!;
		_reader = new SkillPackageReader(Path.Combine(dataRoot, "Temp", "SkillCatalog"), PublicGitHub);
	}

	/// <summary>Reads complete package snapshots without changing installed skills or their bindings.</summary>
	public Task<IReadOnlyList<CatalogSkill>> BrowseAsync(string source, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(source);
		var native = Path.IsPathFullyQualified(source) && string.Equals(_skillsDirectory,
			Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)), StringComparison.OrdinalIgnoreCase);
		return Task.Run(() => _reader.ReadAsync(source, native, null, cancellationToken), cancellationToken);
	}

	/// <summary>Returns the native folder names that have confirmed update sources.</summary>
	public IReadOnlyList<string> GetManagedNames() => LoadRegistry().Select(pair => pair.Key).ToArray();

	/// <summary>Returns saved source bindings keyed by native installation folder.</summary>
	public IReadOnlyDictionary<string, SkillSourceBinding> GetBindings()
	{
		var bindings = new Dictionary<string, SkillSourceBinding>(StringComparer.OrdinalIgnoreCase);
		foreach (var (folder, binding) in LoadRegistry())
		{
			bindings.Add(folder, binding?.Deserialize<SkillSourceBinding>()
				?? throw new InvalidDataException($"Invalid skill binding for {folder} in {_registry}."));
		}
		return bindings;
	}

	/// <summary>Compares the snapshot with the exact native destination, reporting user changes and source conflicts.</summary>
	public SkillInstallPreview Preview(CatalogSkill skill)
	{
		ArgumentNullException.ThrowIfNull(skill);
		if (skill.InspectionWarning is { } warning)
		{
			throw new IOException($"Cannot install an incomplete package. {warning}");
		}
		SkillPackageReader.ValidatePath(skill.FolderName);
		var destination = Path.Combine(_skillsDirectory, skill.FolderName);
		var registry = LoadRegistry();
		var installed = registry[BindingKey(registry, skill.FolderName)];
		var expected = skill.Hashes();
		var actual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		var exists = Directory.Exists(destination);
		string? conflict = null;
		try
		{
			if (exists)
			{
				actual = InstalledHashes(destination);
			}
			else if (File.Exists(destination))
			{
				conflict = $"A file occupies {destination}.";
			}
		}
		catch (IOException ex)
		{
			conflict = ex.Message;
		}

		var changes = Differences(actual, expected);
		if (installed is not null)
		{
			var baseline = installed["Files"]?.Deserialize<Dictionary<string, string>>()
				?? throw new InvalidDataException("The skill binding has no installed file manifest.");
			if (!string.Equals((string?)installed["Source"], skill.Source, StringComparison.Ordinal) ||
				!string.Equals((string?)installed["RelativePath"], skill.RelativePath, StringComparison.Ordinal))
			{
				conflict = $"{destination} is bound to {installed["Source"]} ({installed["RelativePath"]}). Selected source: {skill.Source} ({skill.RelativePath}).";
			}
			else if (!exists || Differences(actual, baseline).Count != 0)
			{
				conflict = $"Local changes at {destination} must be resolved before updating from {skill.Source} ({skill.RelativePath}).";
			}
		}
		else if (exists && changes.Count != 0)
		{
			conflict = $"Existing files at {destination} differ from {skill.Source} ({skill.RelativePath}).";
		}

		if (conflict is null && skill.InstalledFilePath is null &&
			SkillPackageReader.FindNameCollision(_skillsDirectory, destination, skill.Name) is { } other)
		{
			conflict = $"The skill '{skill.Name}' already exists at {other}. Selected source: {skill.Source} ({skill.RelativePath}).";
		}

		return new SkillInstallPreview(skill, destination, conflict, changes, installed is not null, (string?)installed?["Revision"]);
	}

	/// <summary>Resolves the installed source and reference again while retaining the current local package.</summary>
	public async Task<SkillInstallPreview> CheckUpdateAsync(string installedSkillName, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(installedSkillName);
		var registry = LoadRegistry();
		var folder = BindingKey(registry, installedSkillName);
		var binding = registry[folder] ?? throw new InvalidOperationException("This installation has no bound update source.");
		var source = (string?)binding["Source"] ?? throw new InvalidDataException("Missing update source.");
		var packages = await Task.Run(() => _reader.ReadAsync(source, false, (string?)binding["Reference"], cancellationToken), cancellationToken).ConfigureAwait(false);
		var package = packages.SingleOrDefault(item => string.Equals(item.RelativePath, (string?)binding["RelativePath"], StringComparison.Ordinal))
			?? throw new IOException($"The bound package {binding["RelativePath"]} no longer exists in {source}.");
		return Preview(package.ForInstallationFolder(folder));
	}

	/// <summary>Compares a candidate with the selected manual installation, including when their directory names differ.</summary>
	public SkillInstallPreview PreviewBinding(CatalogSkill candidate, CatalogSkill installed)
	{
		ArgumentNullException.ThrowIfNull(candidate);
		ArgumentNullException.ThrowIfNull(installed);
		var manifest = Path.Combine(_skillsDirectory, installed.FolderName, "SKILL.md");
		if (candidate.InstalledFilePath is not null || !string.Equals(installed.InstalledFilePath, manifest, StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(candidate.Name, installed.Name, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Choose a source package matching the selected installed skill.");
		}
		if (Path.IsPathFullyQualified(candidate.Source) && string.Equals(ResolveDirectory(Path.Combine(candidate.Source, candidate.RelativePath)),
			ResolveDirectory(Path.GetDirectoryName(manifest)!), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("Choose the upstream package. An installation cannot be its own update source.");
		}
		var preview = Preview(candidate.ForInstallationFolder(installed.FolderName));
		if (!File.Exists(manifest) || preview.IsUpdate || preview.Conflict is null && preview.Changes.Count != 0)
		{
			throw new IOException($"The manual installation at {manifest} changed. Reopen the catalog.");
		}
		return preview;
	}

	/// <summary>Installs exactly the previewed bytes after rechecking the destination and recording the confirmed source.</summary>
	public void Apply(SkillInstallPreview preview)
	{
		ArgumentNullException.ThrowIfNull(preview);
		WithInstallLock(() =>
		{
			var current = Preview(preview.Skill);
			if (preview.Conflict is not null || current.Conflict is not null)
			{
				throw new IOException(current.Conflict ?? preview.Conflict);
			}

			if (preview.Destination != current.Destination || preview.IsUpdate != current.IsUpdate ||
				preview.InstalledRevision != current.InstalledRevision || !preview.Changes.SequenceEqual(current.Changes))
			{
				throw new IOException("The installation changed after preview. Open a fresh preview.");
			}

			Commit(current);
			preview.CleanupWarning = current.CleanupWarning;
		});
	}

	private void WithInstallLock(Action action)
	{
		var mutexKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_skillsDirectory.ToUpperInvariant())));
		using var mutex = new Mutex(false, "Pact.SkillInstall." + mutexKey);
		try
		{
			if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
			{
				throw new IOException("Another skill installation is in progress.");
			}
		}
		catch (AbandonedMutexException)
		{
			// Ownership transferred to this caller. The destination is inspected again below.
		}

		try
		{
			action();
		}
		finally
		{
			mutex.ReleaseMutex();
		}
	}

	private void Commit(SkillInstallPreview preview)
	{
		var registry = LoadRegistry();
		var skill = preview.Skill;
		if (skill.InstalledFilePath is not null)
		{
			throw new IOException("Select the package at its update source to bind an existing installation.");
		}

		var bindingKey = BindingKey(registry, skill.FolderName);
		var binding = registry[bindingKey]?.AsObject() ?? new JsonObject();
		binding["Source"] = skill.Source;
		binding["RelativePath"] = skill.RelativePath;
		binding["Reference"] = skill.Reference;
		binding["Revision"] = skill.Revision;
		binding["Files"] = JsonSerializer.SerializeToNode(skill.Hashes());
		registry[bindingKey] = binding;
		Directory.CreateDirectory(_skillsDirectory);
		// Agent CLIs discover subdirectories of their native skills root while Pact is running.
		// Keep unpublished bytes and backups on the same volume but outside their search path.
		var stage = Path.Combine(Path.GetDirectoryName(_skillsDirectory)!, ".pact-skill-" + Guid.NewGuid().ToString("N"));
		var backup = stage + ".backup";
		var moved = false;
		var published = false;
		try
		{
			if (preview.Changes.Count != 0)
			{
				foreach (var (relative, bytes) in skill.Content)
				{
					var file = Path.Combine(stage, relative);
					Directory.CreateDirectory(Path.GetDirectoryName(file)!);
					File.WriteAllBytes(file, bytes);
				}

				if (Directory.Exists(preview.Destination))
				{
					if (!preview.IsUpdate)
					{
						throw new IOException($"The installation appeared at {preview.Destination}. Open a fresh preview.");
					}

					Directory.Move(preview.Destination, backup);
					moved = true;
					var original = LoadRegistry()[bindingKey]!["Files"]!.Deserialize<Dictionary<string, string>>()!;
					if (Differences(InstalledHashes(backup), original).Count != 0)
					{
						throw new IOException($"The installation changed at {preview.Destination}. Open a fresh preview.");
					}
				}

				Directory.Move(stage, preview.Destination);
				published = true;
			}
			else if (Differences(InstalledHashes(preview.Destination), skill.Hashes()).Count != 0)
			{
				throw new IOException("The installation changed after preview.");
			}

			AtomicFileWriter.WriteTextAsync(_registry, registry.ToJsonString(), CancellationToken.None).GetAwaiter().GetResult();
		}
		catch (Exception failure)
		{
			try
			{
				if (published)
				{
					Directory.Move(preview.Destination, stage);
				}
				if (moved)
				{
					Directory.Move(backup, preview.Destination);
				}
				if (!TryDeleteWorkDirectory(stage))
				{
					throw new IOException($"Could not remove the staged files at {stage}.");
				}
			}
			catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
			{
				throw new AggregateException($"Installation failed and rollback needs attention. Staging: {stage}. Backup: {backup}.", failure, rollback);
			}

			throw;
		}
		var leftovers = new[] { stage, backup }.Where(path => !TryDeleteWorkDirectory(path)).ToArray();
		if (leftovers.Length != 0)
		{
			preview.CleanupWarning = "The installation was applied. Cleanup is pending at " + string.Join(", ", leftovers);
		}
	}

	private static bool TryDeleteWorkDirectory(string path)
	{
		try
		{
			if (!Directory.Exists(path))
			{
				return !Path.Exists(path) && new DirectoryInfo(path).LinkTarget is null;
			}
			if (new DirectoryInfo(path).LinkTarget is not null)
			{
				return false;
			}
			Directory.Delete(path, recursive: true);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private JsonObject LoadRegistry() => File.Exists(_registry)
		? ParseRegistry(File.ReadAllText(_registry)) : [];

	private JsonObject ParseRegistry(string original)
	{
		using var document = JsonDocument.Parse(original);
		if (document.RootElement.ValueKind != JsonValueKind.Object)
		{
			throw new IOException($"Expected a skill installation registry object in {_registry}.");
		}
		var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var binding in document.RootElement.EnumerateObject())
		{
			if (names.TryGetValue(binding.Name, out var previous))
			{
				throw new IOException($"Duplicate update binding keys '{previous}' and '{binding.Name}' in {_registry}. Resolve those entries before continuing.");
			}
			names.Add(binding.Name, binding.Name);
		}
		return JsonNode.Parse(original)!.AsObject();
	}

	private static string BindingKey(JsonObject registry, string folder) =>
		registry.Select(pair => pair.Key).FirstOrDefault(key => Same(key, folder)) ?? folder;

	private static Dictionary<string, string> InstalledHashes(string directory, bool skipGitMetadata = false) =>
		SkillPackageReader.ReadDirectory(directory, CancellationToken.None, skipGitMetadata).ToDictionary(entry => entry.Key,
			entry => Convert.ToHexString(SHA256.HashData(entry.Value)), StringComparer.OrdinalIgnoreCase);

	private static List<string> Differences(Dictionary<string, string> before, Dictionary<string, string> after)
	{
		var changes = new List<string>();
		foreach (var path in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
		{
			if (!before.TryGetValue(path, out var previous))
			{
				changes.Add("Added " + path);
			}
			else if (!after.TryGetValue(path, out var next))
			{
				changes.Add("Removed " + path);
			}
			else if (previous != next)
			{
				changes.Add("Modified " + path);
			}
		}

		return changes;
	}

	private static HttpClient CreateHttpClient()
	{
		var client = new HttpClient { MaxResponseContentBufferSize = 128 * 1024 * 1024 };
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Pact-Mission-Control");
		return client;
	}
}

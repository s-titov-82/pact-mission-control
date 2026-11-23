using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;
using Tomlyn;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed partial class SkillCatalogWindow : Window, IDisposable
{
	private sealed record Row(CatalogSkill Skill, NativeSkillInstallation? Installation = null)
	{
		internal SkillCatalogSource SourceGroup { get; set; } = null!;
		internal string Label
		{
			get
			{
				if (Installation is null)
				{
					return Skill.Name;
				}
				var folder = Directory.GetParent(Installation.ResolvedManifest)!.Name;
				return string.Equals(folder, Skill.Name, StringComparison.Ordinal) ? folder : $"{folder} ({Skill.Name})";
			}
		}
		public override string ToString() => Label;
	}

	private Row[] _visibleRows = [];
	private string? _selectManifestAfterApply;
	private CatalogSkill[] _bindingPackages = [];

	private readonly string _installed;
	private readonly CancellationTokenSource _lifetime = new();
	private readonly AppPaths _paths;
	private readonly SkillCatalogService _catalog;
	private readonly SkillActivationService _activation;
	private readonly Func<string, CancellationToken, Task<IReadOnlyList<CatalogSkill>>> _browseSavedSource;
	private readonly AgentProfileRecord[] _profiles;
	private readonly string? _selectedProfileId;
	private string[] _projects;
	private IReadOnlyDictionary<string, SkillSourceBinding> _bindings = new Dictionary<string, SkillSourceBinding>();
	private readonly List<Row> _rows = [];
	private CatalogSkill? _previewContent;
	private JsonObject? _settings;
	private string? _settingsRevision;
	private readonly List<string> _loadWarnings = [];
	private IReadOnlyList<string> _nativeLoadWarnings = [];
	private bool _refreshingSkills;
	private bool _remoteRowsPending;
	private bool _remoteRowsRefreshQueued;
	private bool _loadingRemoteSources;
	private Func<Task<string?>>? _pending;
	private bool _busy;
	private bool _disposing;

	public SkillCatalogWindow() : this(new AppPaths(Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pact")), [], [], null)
	{
	}

	internal SkillCatalogWindow(AppPaths paths, AgentProfileRecord[] profiles, string[] projects, string? selectedProject,
		string? installedDirectory = null, string? selectedProfileId = null, bool openCatalog = false,
		Func<string, CancellationToken, Task<IReadOnlyList<CatalogSkill>>>? browseSavedSource = null)
	{
		InitializeComponent();
		RenameInput.PropertyChanged += OnRenameInputChanged;
		_paths = paths;
		_installed = installedDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
		_catalog = new SkillCatalogService(paths.SettingsDirectory, _installed,
			installedDirectory is null ? null : Path.GetDirectoryName(installedDirectory));
		_browseSavedSource = browseSavedSource ?? _catalog.BrowseAsync;
		_activation = new SkillActivationService(paths, _installed);
		_profiles = profiles;
		_selectedProfileId = selectedProfileId;
		_projects = projects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		InitializeMatrix(selectedProject);
		Opened += async (_, _) =>
		{
			string[] remoteSources = [];
			await RunAsync(async () =>
			{
				try
				{
					remoteSources = await LoadAsync();
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					_loadWarnings.Add($"Could not load the skill catalog. {ex.Message}");
					ShowLoadWarnings();
				}
			});
			if (!openCatalog && (selectedProfileId is not null || selectedProject is not null))
			{
				Details.SelectedItem = AvailabilityTab;
			}
			if (!_disposing && remoteSources.Length > 0)
			{
				await LoadRemoteSourcesAsync(remoteSources);
			}
		};
		Closed += (_, _) => Dispose();
		Details.SelectionChanged += (_, e) =>
		{
			if (ReferenceEquals(e.Source, Details))
			{
				ClearPreview();
			}
		};
	}

	private string SourceSettings => Path.Combine(_paths.SettingsDirectory, "skill-sources.json");
	private Row? SelectedRow => (Skills.SelectedItem as TreeViewItem)?.Tag as Row;
	private CatalogSkill? Selected => SelectedRow?.Skill;

	private string SelectedOrigin()
	{
		if (SelectedRow is not { } row)
		{
			return "";
		}

		var skill = row.Skill;
		if (row.Installation is not { } installation)
		{
			return string.Join(" · ", new[] { skill.Source, skill.RelativePath, skill.Reference, skill.Revision }.Where(s => !string.IsNullOrEmpty(s)));
		}

		var lines = new List<string> { installation.ResolvedManifest };
		if (IsInstallDestination(skill) && _bindings.TryGetValue(skill.FolderName, out var binding))
		{
			lines.Add(string.Join(" · ", new[] { binding.Source, binding.RelativePath, binding.Reference, binding.Revision }.Where(s => !string.IsNullOrEmpty(s))));
		}

		foreach (var connection in installation.Connections)
		{
			var profiles = string.Join(", ", connection.Profiles.Select(p => p.DisplayName));
			var label = string.Join(", ", connection.Agents);
			lines.Add($"{label} · {connection.ManifestPath}" + (profiles.Length == 0 ? "" : $" · Profiles: {profiles}"));
		}
		return string.Join(Environment.NewLine, lines);
	}

	private bool IsInstallDestination(CatalogSkill skill) => string.Equals(skill.InstalledFilePath,
		Path.Combine(_installed, skill.FolderName, "SKILL.md"), StringComparison.OrdinalIgnoreCase);

	public void Dispose()
	{
		if (_disposing)
		{
			return;
		}
		_disposing = true;
		_lifetime.Cancel();
		_matrixRead?.Dispose();
		_matrixRead = null;
		if (!_busy)
		{
			_lifetime.Dispose();
		}
	}

	private async Task RunAsync(Func<Task> operation)
	{
		if (_busy || _disposing)
		{
			return;
		}

		_busy = true;
		IsEnabled = false;
		try
		{ await operation(); }
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
		catch (Exception ex) { ClearPreview(); Status.Text = ex.Message; }
		finally
		{
			_busy = false;
			if (_disposing)
			{
				_lifetime.Dispose();
			}
			else
			{
				ScheduleRemoteRowsRefresh();
			}
			IsEnabled = true;
		}
	}

	private void ClearPreview(bool keepRenameEditor = false)
	{
		_pending = null;
		CancelRename.IsVisible = true;
		if (!keepRenameEditor)
		{
			RenameEditor.IsVisible = false;
		}
		if (_previewContent is not null)
		{
			_previewContent = null;
			Files.ItemsSource = Selected?.Files;
			Files.SelectedItem = "SKILL.md";
			DisplayFile();
		}
		Apply.IsVisible = false;
		CancelPreview.IsVisible = false;
		Status.Text = "";
	}

	private void OnCancelPreview(object? sender, RoutedEventArgs e) => ClearPreview();

	private async Task<string[]> LoadAsync()
	{
		try
		{
			_settingsRevision = await ReadSourceSettingsAsync();
			var settings = _settingsRevision is null ? new JsonObject() : JsonNode.Parse(_settingsRevision)?.AsObject()
				?? throw new InvalidDataException("Expected a JSON object.");
			foreach (var key in new[] { "sources", "mappingFolders", "mappingProjects" })
			{
				if (settings[key] is { } value
					&& (value is not JsonArray array || array.Any(item => item is not JsonValue entry || !entry.TryGetValue<string>(out _))))
				{
					throw new InvalidDataException($"Expected an array of strings for {key}.");
				}
			}
			_settings = settings;
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
		{
			_loadWarnings.Add($"Could not read {SourceSettings}. Source and mapping directory changes are disabled. {ex.Message}");
		}
		SourceEditor.IsEnabled = _matrixPickFolder.IsEnabled = _settings is not null;
		try
		{
			await LoadMatrixFoldersAsync(_loadWarnings);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_loadWarnings.Add($"Could not load mapping directories. {ex.Message}");
		}
		try
		{
			await RefreshNativeRowsAsync();
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_nativeLoadWarnings = [$"Could not load installed skills. {ex.Message}"];
		}
		var sources = (_settings?["sources"]?.AsArray() ?? []).Select(source => source!.GetValue<string>()).ToArray();
		foreach (var source in sources.Where(Path.IsPathFullyQualified))
		{
			try
			{
				var skills = await _catalog.BrowseAsync(source, _lifetime.Token);
				_rows.AddRange(skills.Select(skill => new Row(skill)));
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				_loadWarnings.Add($"{source}: {ex.Message}");
			}
		}
		ShowLoadWarnings();
		RefreshList();
		return sources.Where(source => !Path.IsPathFullyQualified(source)).ToArray();
	}

	private async Task LoadRemoteSourcesAsync(string[] sources)
	{
		var token = _lifetime.Token;
		_loadingRemoteSources = true;
		ShowLoadWarnings();
		try
		{
			await Parallel.ForEachAsync(sources, new ParallelOptions
			{
				MaxDegreeOfParallelism = 4,
				CancellationToken = token
			}, async (source, cancellationToken) =>
			{
				IReadOnlyList<CatalogSkill> skills;
				try
				{
					skills = await _browseSavedSource(source, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
				{
					await Dispatcher.UIThread.InvokeAsync(() =>
					{
						_loadWarnings.Add($"{source}: {ex.Message}");
						ShowLoadWarnings();
					}, DispatcherPriority.Background, cancellationToken);
					return;
				}
				if (skills.Count == 0)
				{
					return;
				}
				await Dispatcher.UIThread.InvokeAsync(() =>
				{
					_rows.AddRange(skills.Select(skill => new Row(skill)));
					_remoteRowsPending = true;
					ScheduleRemoteRowsRefresh();
				}, DispatcherPriority.Background, cancellationToken);
			});
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		catch (Exception ex)
		{
			_loadWarnings.Add($"Could not refresh saved GitHub sources. {ex.Message}");
		}
		finally
		{
			_loadingRemoteSources = false;
			if (!_disposing)
			{
				ShowLoadWarnings();
			}
		}
	}

	private void ScheduleRemoteRowsRefresh()
	{
		if (_busy || _disposing || !_remoteRowsPending || _remoteRowsRefreshQueued)
		{
			return;
		}
		_remoteRowsRefreshQueued = true;
		Dispatcher.UIThread.Post(() =>
		{
			_remoteRowsRefreshQueued = false;
			if (_busy || _disposing || !_remoteRowsPending)
			{
				return;
			}
			try
			{
				RefreshList(preserveDetails: true);
			}
			catch (Exception ex)
			{
				_loadWarnings.Add($"Could not refresh saved GitHub sources. {ex.Message}");
				ShowLoadWarnings();
			}
		}, DispatcherPriority.ContextIdle);
	}

	private void ShowLoadWarnings()
	{
		var messages = _loadWarnings.Concat(_nativeLoadWarnings);
		if (_matrixPruneNotice is { } notice)
		{
			messages = messages.Append(notice);
		}
		if (_loadingRemoteSources)
		{
			messages = messages.Append("Reading saved GitHub sources…");
		}
		var status = string.Join(Environment.NewLine, messages.Distinct(StringComparer.Ordinal));
		LoadStatus.Text = status;
		LoadStatusPanel.IsVisible = status.Length > 0;
	}

	private async Task<string?> ReadSourceSettingsAsync()
	{
		try
		{
			return await File.ReadAllTextAsync(SourceSettings, _lifetime.Token);
		}
		catch (FileNotFoundException)
		{
			return null;
		}
		catch (DirectoryNotFoundException)
		{
			return null;
		}
	}

	private async Task SaveSourceSettingsAsync(JsonObject next)
	{
		if (_settings is null || !string.Equals(_settingsRevision, await ReadSourceSettingsAsync(), StringComparison.Ordinal))
		{
			throw new InvalidOperationException($"Reopen Skills to read the current settings before editing {SourceSettings}.");
		}
		var updated = next.ToJsonString();
		await AtomicFileWriter.WriteTextAsync(SourceSettings, updated, _paths.AtomicTempDirectory, _lifetime.Token);
		_settings = next;
		_settingsRevision = updated;
	}

	private async Task<IReadOnlyList<NativeSkillInstallation>> RefreshNativeRowsAsync()
	{
		var bindings = await Task.Run(_catalog.GetBindings, _lifetime.Token);
		var discovered = await _catalog.DiscoverInstalledAsync(_profiles, _activation, _lifetime.Token);
		_bindings = bindings;
		_duplicateGroups = [];
		_rows.RemoveAll(row => row.Installation is not null);
		_rows.AddRange(discovered.Installations.Select(installation => new Row(installation.Skill, installation)));
		_nativeLoadWarnings = discovered.Warnings;
		ShowLoadWarnings();
		return discovered.Installations;
	}

	private void RefreshList(bool preserveDetails = false)
	{
		_remoteRowsPending = false;
		var previousSource = (Skills.SelectedItem as TreeViewItem)?.Tag as string;
		var previousRow = SelectedRow;
		var previous = previousRow?.Skill;
		var previousLocations = SelectedRow?.Installation?.Connections.Select(connection => connection.ManifestPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var installed = _rows.Where(r => IsInstallDestination(r.Skill) && _bindings.ContainsKey(r.Skill.FolderName))
			.Select(r => _bindings[r.Skill.FolderName]).Select(binding => (binding.Source, binding.RelativePath)).ToHashSet();
		var rows = _rows.Where(r => r.Installation is not null
			|| r.Skill.InstalledFilePath is null && !installed.Contains((r.Skill.Source, r.Skill.RelativePath)))
			.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
			.ThenBy(r => r.Skill.RelativePath, StringComparer.Ordinal).ToArray();
		if (preserveDetails && rows.SequenceEqual(_visibleRows, ReferenceEqualityComparer.Instance))
		{
			return;
		}
		foreach (var row in rows)
		{
			_bindings.TryGetValue(row.Skill.FolderName, out var binding);
			row.SourceGroup ??= _catalog.ClassifySource(row.Skill, row.Installation, IsInstallDestination(row.Skill) ? binding : null);
		}
		_visibleRows = rows;
		PruneMatrixDrafts(preserveDetails);
		var existingRoots = Skills.Items.OfType<TreeViewItem>().ToDictionary(node => (string)node.Tag!, StringComparer.OrdinalIgnoreCase);
		var roots = new List<TreeViewItem>();
		TreeViewItem? chosen = null;
		TreeViewItem? retained = null;
		foreach (var group in rows.GroupBy(row => row.SourceGroup.Location, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.First().SourceGroup.Label, StringComparer.OrdinalIgnoreCase)
			.ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
		{
			var source = group.First().SourceGroup;
			var existing = existingRoots.GetValueOrDefault(source.Location);
			var wasExpanded = existing?.IsExpanded == true;
			var root = preserveDetails && existing is not null ? existing : new TreeViewItem { Tag = source.Location, IsExpanded = wasExpanded };
			root.Header = $"{source.Label} ({group.Count()})";
			if (preserveDetails && string.Equals(source.Location, previousSource, StringComparison.OrdinalIgnoreCase))
			{
				retained = root;
			}
			ToolTip.SetTip(root, source.Location);
			var children = root.Items.OfType<TreeViewItem>()
				.ToDictionary(node => (Row)node.Tag!, (IEqualityComparer<Row>)ReferenceEqualityComparer.Instance);
			var index = 0;
			foreach (var row in group)
			{
				if (!children.TryGetValue(row, out var child))
				{
					child = new TreeViewItem { Header = row.Label, Tag = row };
					root.Items.Insert(index, child);
				}
				index++;
				if (preserveDetails && ReferenceEquals(row, previousRow))
				{
					retained = child;
				}
				if (_selectManifestAfterApply is { } target ? string.Equals(row.Installation?.ResolvedManifest, target, StringComparison.OrdinalIgnoreCase)
					: previous is not null && (row.Installation?.Connections.Any(connection => previousLocations?.Contains(connection.ManifestPath) == true) == true
					|| row.Skill.Source == previous.Source && row.Skill.RelativePath == previous.RelativePath
					|| IsInstallDestination(row.Skill) && _bindings.TryGetValue(row.Skill.FolderName, out var binding)
					&& binding.Source == previous.Source && binding.RelativePath == previous.RelativePath))
				{
					chosen = child;
					root.IsExpanded = true;
				}
			}
			roots.Add(root);
		}
		chosen = retained ?? chosen;
		if (chosen == null && roots.Count > 0)
		{
			roots[0].IsExpanded = true;
			chosen = roots[0].Items.OfType<TreeViewItem>().First();
		}
		_refreshingSkills = true;
		try
		{
			if (!preserveDetails)
			{
				Skills.Items.Clear();
			}
			for (var i = 0; i < roots.Count; i++)
			{
				if (i >= Skills.Items.Count || !ReferenceEquals(Skills.Items[i], roots[i]))
				{
					Skills.Items.Insert(i, roots[i]);
				}
			}
			if (!ReferenceEquals(Skills.SelectedItem, chosen))
			{
				Skills.SelectedItem = chosen;
			}
			if (retained is null)
			{
				RefreshSelection(preserveOperation: preserveDetails);
			}
		}
		finally
		{
			_refreshingSkills = false;
		}
		_selectManifestAfterApply = null;
		RefreshMatrix(keepRows: preserveDetails);
	}

	private async void OnBrowse(object? sender, RoutedEventArgs e)
	{
		await RunAsync(async () =>
		{
			var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select skill source" });
			if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
			{
				SourceInput.Text = path;
			}
		});
	}

	private async void OnAddSource(object? sender, RoutedEventArgs e)
	{
		await RunAsync(async () =>
		{
			var source = SourceInput.Text?.Trim();
			if (string.IsNullOrEmpty(source))
			{
				return;
			}

			var settings = _settings ?? throw new InvalidOperationException($"Could not read {SourceSettings}.");
			var sources = settings["sources"]?.AsArray() ?? new JsonArray();
			if (sources.Any(v => string.Equals((string?)v, source, StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}

			var skills = await _catalog.BrowseAsync(source, _lifetime.Token);
			if (skills.Count == 0)
			{
				throw new InvalidDataException("No SKILL.md found in this source.");
			}

			var next = settings.DeepClone().AsObject();
			var updated = sources.DeepClone().AsArray();
			updated.Add(source);
			next["sources"] = updated;
			await SaveSourceSettingsAsync(next);
			_rows.AddRange(skills.Select(s => new Row(s)));
			RefreshList();
			SourceInput.Text = "";
			SourceEditor.IsExpanded = false;
		});
	}

	private void OnSkillSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (!_refreshingSkills)
		{
			RefreshSelection();
		}
	}

	private void RefreshSelection(bool preserveOperation = false)
	{
		if (!preserveOperation)
		{
			ClearPreview();
		}
		var skill = Selected;
		RefreshMaintenanceLocations();
		BindingEditor.IsVisible = false;
		BindingCandidates.IsVisible = false;
		_bindingPackages = [];
		SkillTitle.Text = SelectedRow?.Label ?? (Skills.SelectedItem as TreeViewItem)?.Header?.ToString() ?? "Add a source to browse skills";
		Origin.Text = SelectedOrigin();
		if (DuplicateHint(SelectedRow?.Installation) is { } hint)
		{
			Origin.Text += Environment.NewLine + hint;
		}

		if (_previewContent is null)
		{
			Files.ItemsSource = skill?.Files;
			Files.SelectedItem = skill?.Files.FirstOrDefault(f => f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
		}
		if (skill?.InspectionWarning is { } warning)
		{
			Origin.Text += Environment.NewLine + warning;
		}
		Install.IsVisible = skill is { InstalledFilePath: null };
		Update.IsVisible = skill is not null && IsInstallDestination(skill) && _bindings.ContainsKey(skill.FolderName);
		LinkSource.IsVisible = skill is not null && IsInstallDestination(skill) && !Update.IsVisible;
		if (SelectedRow is null && Skills.SelectedItem is TreeViewItem source)
		{
			Origin.Text = source.Tag as string;
		}
		Files.IsVisible = Contents.IsVisible = skill is not null;
	}

	private void OnFileSelected(object? sender, SelectionChangedEventArgs e) => DisplayFile();

	private void DisplayFile()
	{
		try
		{ Contents.Text = Files.SelectedItem is string file ? (_previewContent ?? Selected)?.ReadFile(file) : ""; }
		catch (Exception ex) { Contents.Text = ex.Message; }
	}

	private async void OnInstall(object? sender, RoutedEventArgs e) =>
		await RunAsync(async () =>
		{
			if (Selected is { } skill)
			{
				await ShowInstallAsync(await Task.Run(() => _catalog.Preview(skill), _lifetime.Token));
			}
		});

	private async void OnUpdate(object? sender, RoutedEventArgs e) =>
		await RunAsync(async () =>
		{
			if (Selected is { } skill)
			{
				await ShowInstallAsync(await Task.Run(() => _catalog.CheckUpdateAsync(skill.FolderName, _lifetime.Token), _lifetime.Token));
			}
		});

	private async Task ShowInstallAsync(SkillInstallPreview change)
	{
		ClearPreview();
		_previewContent = change.Skill;
		Files.ItemsSource = change.Skill.Files;
		Files.SelectedItem = "SKILL.md";
		DisplayFile();
		var selected = SelectedRow;
		var (impact, binding) = await Task.Run(() => InspectInstallImpact(change, selected), _lifetime.Token);
		Status.Text = string.Join(Environment.NewLine, new[] { $"Destination: {change.Destination}",
			change.InstalledRevision is { } old ? $"Installed revision: {old}" : null,
			change.Skill.Revision is { } next ? $"Source revision: {next}" : null,
			change.ReleaseNotes, string.Join(Environment.NewLine, change.Changes), "Profile and project availability:",
			string.Join(Environment.NewLine, impact), change.Conflict }
			.Where(s => !string.IsNullOrEmpty(s)));
		if (change.Conflict is not null)
		{
			return;
		}

		if (change.IsUpdate && change.Changes.Count == 0 && change.InstalledRevision == change.Skill.Revision)
		{
			Status.Text = "Already up to date.\n" + Status.Text;
			return;
		}
		_pending = async () =>
		{
			await Task.Run(() => _catalog.Apply(change), _lifetime.Token);
			return change.CleanupWarning;
		};
		Apply.Content = binding ? "Bind update source" : change.IsUpdate ? "Apply update" : "Apply installation";
		Apply.IsVisible = true;
		CancelPreview.IsVisible = true;
	}

	private (List<string> Impact, bool Binding) InspectInstallImpact(SkillInstallPreview change, Row? selected)
	{
		var impact = new List<string>();
		var file = selected?.Skill.InstalledFilePath ?? Path.Combine(change.Destination, "SKILL.md");
		var skillName = selected?.Skill.Name ?? change.Skill.Name;
		foreach (var profile in _profiles)
		{
			foreach (var project in new string?[] { null }.Concat(_projects))
			{
				_lifetime.Token.ThrowIfCancellationRequested();
				try
				{
					var manifest = selected?.Installation?.ManifestFor(profile) ?? file;
					var wasAvailable = File.Exists(manifest) && _activation.Inspect(profile, skillName, manifest, project).EffectiveEnabled;
					var available = _activation.InspectAfterInstallation(profile, change.Skill.Name, manifest, project).EffectiveEnabled;
					if (wasAvailable || available)
					{
						var scope = project ?? "profile default";
						impact.Add($"{profile.DisplayName} ({profile.Id}) · {scope}" + (available ? "" : " · becomes unavailable"));
					}
				}
				catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException or TomlException)
				{
					impact.Add($"{profile.DisplayName} · {project ?? "profile default"}: {ex.Message}");
				}
			}
		}
		if (impact.Count == 0)
		{
			impact.Add("No configured profile or project currently discovers this installation.");
		}
		var binding = !change.IsUpdate && change.Changes.Count == 0 && File.Exists(file);
		if (binding)
		{
			impact.Add($"Bind this installation to {change.Skill.Source} ({change.Skill.RelativePath}) for updates.");
		}
		return (impact, binding);
	}

	private async void OnApply(object? sender, RoutedEventArgs e)
	{
		await RunAsync(async () =>
		{
			if (_pending is not { } apply)
			{
				return;
			}

			ClearPreview();
			var cleanupWarning = await apply();
			var messages = new List<string> { "Applied." };
			if (cleanupWarning is not null)
			{
				messages.Add(cleanupWarning);
			}
			try
			{
				var installations = await RefreshNativeRowsAsync();
				RefreshList();
				if (DuplicatePanel.IsVisible)
				{
					await RefreshDuplicateReportAsync(installations);
				}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				messages.Add($"Could not refresh the catalog: {ex.Message}");
			}
			Status.Text = string.Join(Environment.NewLine, messages);
		});
	}
}

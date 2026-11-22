using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

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
	private readonly AgentProfileRecord[] _profiles;
	private string[] _projects;
	private IReadOnlyDictionary<string, SkillSourceBinding> _bindings = new Dictionary<string, SkillSourceBinding>();
	private readonly List<Row> _rows = [];
	private CatalogSkill? _previewContent;
	private JsonObject _settings = new();
	private Func<Task<string?>>? _pending;
	private bool _busy;
	private bool _disposing;

	public SkillCatalogWindow() : this(new AppPaths(Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pact")), [], [], null)
	{
	}

	internal SkillCatalogWindow(AppPaths paths, AgentProfileRecord[] profiles, string[] projects, string? selectedProject,
		string? installedDirectory = null, string? selectedProfileId = null, bool openCatalog = false)
	{
		InitializeComponent();
		RenameInput.PropertyChanged += OnRenameInputChanged;
		_paths = paths;
		_installed = installedDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills");
		_catalog = new SkillCatalogService(paths.SettingsDirectory, _installed,
			installedDirectory is null ? null : Path.GetDirectoryName(installedDirectory));
		_activation = new SkillActivationService(paths, _installed);
		_profiles = profiles;
		_projects = projects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		InitializeMatrix(selectedProject);
		Opened += async (_, _) =>
		{
			await RunAsync(LoadAsync);
			if (!openCatalog && (selectedProfileId is not null || selectedProject is not null))
			{
				Details.SelectedItem = AvailabilityTab;
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

	private string? SelectedManifest(AgentProfileRecord profile) => SelectedRow?.Installation?.ManifestFor(profile);

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
			IsEnabled = true;
			_busy = false;
			if (_disposing)
			{
				_lifetime.Dispose();
			}
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

	private async Task LoadAsync()
	{
		if (File.Exists(SourceSettings))
		{
			_settings = JsonNode.Parse(await File.ReadAllTextAsync(SourceSettings, _lifetime.Token))?.AsObject()
				?? throw new InvalidDataException("Invalid skill sources file.");
		}

		var warnings = new List<string>();
		await LoadMatrixFoldersAsync(warnings);
		_rows.Clear();
		_bindings = _catalog.GetBindings();
		warnings.AddRange(await RefreshNativeRowsAsync());

		foreach (var source in _settings["sources"]?.AsArray() ?? [])
		{
			try
			{ await AddRowsAsync(source!.GetValue<string>()); }
			catch (Exception ex) when (ex is not OperationCanceledException) { warnings.Add($"{source}: {ex.Message}"); }
		}
		RefreshList();
		Status.Text = string.Join(Environment.NewLine, warnings);
	}

	private async Task AddRowsAsync(string source)
	{
		var skills = await _catalog.BrowseAsync(source, _lifetime.Token);
		_rows.AddRange(skills.Select(skill => new Row(skill)));
	}

	private async Task<IReadOnlyList<string>> RefreshNativeRowsAsync()
	{
		_duplicateGroups = [];
		var discovered = await _catalog.DiscoverInstalledAsync(_profiles, _activation, _lifetime.Token);
		_rows.RemoveAll(row => row.Installation is not null);
		_rows.AddRange(discovered.Installations.Select(installation => new Row(installation.Skill, installation)));
		return discovered.Warnings;
	}

	private void RefreshList()
	{
		var previous = Selected;
		var previousLocations = SelectedRow?.Installation?.Connections.Select(connection => connection.ManifestPath)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var installed = _rows.Where(r => IsInstallDestination(r.Skill) && _bindings.ContainsKey(r.Skill.FolderName))
			.Select(r => _bindings[r.Skill.FolderName]).Select(binding => (binding.Source, binding.RelativePath)).ToHashSet();
		var rows = _rows.Where(r => r.Installation is not null
			|| r.Skill.InstalledFilePath is null && !installed.Contains((r.Skill.Source, r.Skill.RelativePath)))
			.OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToArray();
		foreach (var row in rows)
		{
			_bindings.TryGetValue(row.Skill.FolderName, out var binding);
			row.SourceGroup = _catalog.ClassifySource(row.Skill, row.Installation, IsInstallDestination(row.Skill) ? binding : null);
		}
		_visibleRows = rows;
		var expanded = Skills.Items.OfType<TreeViewItem>().Where(node => node.IsExpanded).Select(node => node.Tag).ToHashSet();
		var roots = new List<TreeViewItem>();
		TreeViewItem? chosen = null;
		foreach (var group in rows.GroupBy(row => row.SourceGroup.Location, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.First().SourceGroup.Label, StringComparer.OrdinalIgnoreCase))
		{
			var source = group.First().SourceGroup;
			var root = new TreeViewItem { Header = $"{source.Label} ({group.Count()})", Tag = source.Location, IsExpanded = expanded.Contains(source.Location) };
			ToolTip.SetTip(root, source.Location);
			foreach (var row in group)
			{
				var child = new TreeViewItem { Header = row.Label, Tag = row };
				root.Items.Add(child);
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
		Skills.ItemsSource = roots;
		_selectManifestAfterApply = null;
		if (chosen == null && roots.Count > 0)
		{
			roots[0].IsExpanded = true;
			chosen = roots[0].Items.OfType<TreeViewItem>().First();
		}
		Skills.SelectedItem = chosen;
		RefreshSelection();
		RefreshMatrix();
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

			var sources = _settings["sources"]?.AsArray() ?? new JsonArray();
			if (sources.Any(v => string.Equals((string?)v, source, StringComparison.OrdinalIgnoreCase)))
			{
				return;
			}

			var skills = await _catalog.BrowseAsync(source, _lifetime.Token);
			if (skills.Count == 0)
			{
				throw new InvalidDataException("No SKILL.md found in this source.");
			}

			var next = _settings.DeepClone().AsObject();
			var updated = sources.DeepClone().AsArray();
			updated.Add(source);
			next["sources"] = updated;
			await AtomicFileWriter.WriteTextAsync(SourceSettings, next.ToJsonString(), _paths.AtomicTempDirectory, _lifetime.Token);
			_settings = next;
			_rows.AddRange(skills.Select(s => new Row(s)));
			RefreshList();
			SourceInput.Text = "";
			SourceEditor.IsExpanded = false;
		});
	}

	private void OnSkillSelected(object? sender, SelectionChangedEventArgs e) => RefreshSelection();

	private void RefreshSelection()
	{
		ClearPreview();
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

		Files.ItemsSource = skill?.Files;
		if (skill?.InspectionWarning is { } warning)
		{
			Origin.Text += Environment.NewLine + warning;
		}
		Files.SelectedItem = skill?.Files.FirstOrDefault(f => f.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
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
		await RunAsync(async () => { if (Selected is { } s) { ShowInstall(await Task.Run(() => _catalog.Preview(s), _lifetime.Token)); } });

	private async void OnUpdate(object? sender, RoutedEventArgs e) =>
		await RunAsync(async () => { if (Selected is { } s) { ShowInstall(await _catalog.CheckUpdateAsync(s.FolderName, _lifetime.Token)); } });

	private void ShowInstall(SkillInstallPreview change)
	{
		ClearPreview();
		_previewContent = change.Skill;
		Files.ItemsSource = change.Skill.Files;
		Files.SelectedItem = "SKILL.md";
		DisplayFile();
		var impact = new List<string>();
		var file = Selected?.InstalledFilePath ?? Path.Combine(change.Destination, "SKILL.md");
		var skillName = Selected?.Name ?? change.Skill.Name;
		foreach (var profile in _profiles)
		{
			foreach (var project in new string?[] { null }.Concat(_projects))
			{
				try
				{
					var manifest = SelectedManifest(profile) ?? file;
					var wasAvailable = File.Exists(manifest) && _activation.Inspect(profile, skillName, manifest, project).EffectiveEnabled;
					var available = _activation.InspectAfterInstallation(profile, change.Skill.Name, manifest, project).EffectiveEnabled;
					if (wasAvailable || available)
					{
						var scope = project ?? "profile default";
						impact.Add($"{profile.DisplayName} ({profile.Id}) · {scope}" + (available ? "" : " · becomes unavailable"));
					}
				}
				catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
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
			return null;
		};
		Apply.Content = binding ? "Bind update source" : change.IsUpdate ? "Apply update" : "Apply installation";
		Apply.IsVisible = true;
		CancelPreview.IsVisible = true;
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
				_bindings = _catalog.GetBindings();
				messages.AddRange(await RefreshNativeRowsAsync());
				RefreshList();
				if (DuplicatePanel.IsVisible)
				{
					await RefreshDuplicateReportAsync(_rows.Where(row => row.Installation is not null).Select(row => row.Installation!).ToArray());
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

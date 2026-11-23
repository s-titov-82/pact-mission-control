using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed partial class SkillCatalogWindow
{
	private sealed record MappingScope(string? Directory, bool IsFolder = false)
	{
		public override string ToString() => Directory is null ? "Default" : $"{(IsFolder ? "Folder" : "Project")} · {Directory}";
	}
	private sealed record MatrixKey(string ProfileId, string Manifest, MappingScope Scope)
	{
		internal string? Project => Scope.Directory;
	}
	private sealed record SourceKey(string ProfileId, string Location, MappingScope Scope);
	private sealed record MatrixCell(Row Row, SkillCatalogSource Source, AgentProfileRecord Profile, MatrixKey Key,
		SkillActivationState? State, string? Error, SkillMode? SourceMode);

	private readonly TreeView _matrixScope = new() { Name = "MatrixScope" };
	private MappingScope CurrentScope => (_matrixScope.SelectedItem as TreeViewItem)?.Tag as MappingScope ?? new(null);
	private readonly StackPanel _matrixRows = new() { Spacing = 4 };
	private readonly SelectableTextBlock _matrixSummary = new() { Name = "MatrixSummary", TextWrapping = TextWrapping.Wrap };
	private readonly Button _matrixApply = new() { Name = "MatrixApply", Content = "Apply", IsVisible = false };
	private readonly Button _matrixPickFolder = new() { Name = "MatrixPickFolder", Content = "Add directory…" };
	private readonly Dictionary<string, TreeViewItem> _folderNodes = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, TreeViewItem> _projectNodes = new(StringComparer.OrdinalIgnoreCase);
	private readonly Button _matrixDiscard = new() { Name = "MatrixDiscard", Content = "Discard edits" };
	private readonly Dictionary<MatrixKey, SkillMatrixEdit> _matrixDraft = [];
	private readonly Dictionary<SourceKey, SkillSourceMatrixEdit> _sourceDraft = [];
	private readonly HashSet<string> _expandedSources = new(StringComparer.OrdinalIgnoreCase);
	private List<MatrixCell> _matrixCells = [];
	private SkillActivationBatchPreview? _matrixPreview;
	private CancellationTokenSource? _matrixRead;
	private int _matrixRevision;
	private int _impactRevision;
	private bool _selectedProfileShown;
	private string? _matrixNotice;
	private string? _matrixPruneNotice;
	private string? _matrixRefreshError;
	internal Task MatrixImpactReady { get; private set; } = Task.CompletedTask;

	// Refreshes may originate from catalog loading or navigation. Only the newest snapshot is presented.
	internal Task MatrixReady { get; private set; } = Task.CompletedTask;

	private void InitializeMatrix(string? selectedProject)
	{
		var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("230,5,*"), ColumnSpacing = 4 };
		layout.ColumnDefinitions[0].MinWidth = 145;
		layout.ColumnDefinitions[2].MinWidth = 350;
		var panel = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 8 };
		var scope = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
		scope.Children.Add(new TextBlock { Text = "Changes apply to", FontWeight = FontWeight.SemiBold });
		AutomationProperties.SetName(_matrixScope, "Default, project or folder");
		var defaults = new TreeViewItem { Header = "Default", Tag = new MappingScope(null) };
		_matrixScope.Items.Add(defaults);
		foreach (var project in _projects.Order(StringComparer.OrdinalIgnoreCase))
		{
			EnsureScopeNode(project, false);
		}
		_matrixScope.SelectedItem = selectedProject is null ? defaults : EnsureScopeNode(selectedProject, false);
		AddMatrixControl(scope, _matrixScope, 1);
		var choices = new MenuFlyout();
		foreach (var isFolder in new[] { false, true })
		{
			var title = isFolder ? "Folder and descendants…" : "Single project…";
			var option = new MenuItem { Header = title };
			option.Click += async (_, _) => await RunAsync(async () =>
			{
				var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
				if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } directory)
				{
					await AddMappingDirectoryAsync(directory, isFolder);
				}
			});
			choices.Items.Add(option);
		}
		_matrixPickFolder.Flyout = choices;
		AddMatrixControl(scope, _matrixPickFolder, 2);
		AddCell(layout, scope, 0);
		AddCell(layout, new GridSplitter { ResizeDirection = GridResizeDirection.Columns, HorizontalAlignment = HorizontalAlignment.Stretch }, 1);
		AddCell(layout, panel, 2);
		AddMatrixControl(panel, new ScrollViewer
		{
			Content = _matrixRows,
			HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		}, 0);
		AddMatrixControl(panel, new ScrollViewer { Content = _matrixSummary, MaxHeight = 145 }, 1);
		var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
		actions.Children.Add(_matrixApply);
		actions.Children.Add(_matrixDiscard);
		AddMatrixControl(panel, actions, 2);
		MatrixHost.Content = layout;
		_matrixScope.SelectionChanged += (_, _) => RefreshMatrix();
		Details.SelectionChanged += (_, e) =>
		{
			if (ReferenceEquals(e.Source, Details))
			{
				RefreshMatrix();
			}
		};
		_matrixApply.Click += async (_, _) => await RunAsync(ApplyMatrixAsync);
		_matrixDiscard.Click += (_, _) =>
		{
			_matrixDraft.Clear();
			_sourceDraft.Clear();
			ClearMatrixPruneNotice();
			ClearMatrixReview();
			RenderMatrixRows();
		};
		ClearMatrixReview();
	}

	private TreeViewItem EnsureScopeNode(string directory, bool isFolder)
	{
		var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		var nodes = isFolder ? _folderNodes : _projectNodes;
		if (nodes.TryGetValue(path, out var existing))
		{
			return existing;
		}
		var parent = Directory.GetParent(path);
		var sameFolder = _folderNodes.GetValueOrDefault(path);
		var parentNode = !isFolder && sameFolder is not null ? sameFolder
			: parent is null ? null : EnsureScopeNode(parent.FullName, true);
		var name = Path.GetFileName(path);
		var scope = new MappingScope(path, isFolder);
		var label = name.Length == 0 ? path : name;
		var node = new TreeViewItem { Header = isFolder ? label : parentNode == sameFolder && sameFolder is not null ? "This project" : $"{label} · Project", Tag = scope, IsExpanded = true };
		ToolTip.SetTip(node, scope.ToString());
		AutomationProperties.SetName(node, scope.ToString());
		var siblings = parentNode?.Items ?? _matrixScope.Items;
		var index = 0;
		while (index < siblings.Count && siblings[index] is TreeViewItem { Tag: MappingScope sibling }
			&& (sibling.Directory is null || StringComparer.OrdinalIgnoreCase.Compare(sibling.Directory, path) < 0))
		{
			index++;
		}
		siblings.Insert(index, node);
		nodes.Add(path, node);
		if (isFolder && _projectNodes.TryGetValue(path, out var project))
		{
			var selected = ReferenceEquals(_matrixScope.SelectedItem, project);
			siblings.Remove(project);
			project.Header = "This project";
			node.Items.Add(project);
			if (selected)
			{
				_matrixScope.SelectedItem = project;
			}
		}
		return node;
	}

	internal async Task AddMappingDirectoryAsync(string directory, bool isFolder)
	{
		var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		var next = (_settings ?? throw new InvalidOperationException($"Could not read {SourceSettings}.")).DeepClone().AsObject();
		var key = isFolder ? "mappingFolders" : "mappingProjects";
		var saved = next[key]?.AsArray() ?? new JsonArray();
		if (!saved.Any(item => string.Equals(item?.GetValue<string>(), path, StringComparison.OrdinalIgnoreCase)))
		{
			saved.Add(path);
			next[key] = saved;
			await SaveSourceSettingsAsync(next);
		}
		var node = EnsureScopeNode(path, isFolder);
		if (!isFolder && !_projects.Contains(path, StringComparer.OrdinalIgnoreCase))
		{
			_projects = _projectNodes.Keys.ToArray();
		}
		for (var parent = node.Parent as TreeViewItem; parent is not null; parent = parent.Parent as TreeViewItem)
		{
			parent.IsExpanded = true;
		}
		_matrixScope.SelectedItem = node;
		node.BringIntoView();
	}

	private async Task LoadMatrixFoldersAsync(List<string> warnings)
	{
		var paths = await Task.Run(() => _activation.ListFolderScopes(warnings.Add), _lifetime.Token);
		foreach (var path in paths)
		{
			EnsureScopeNode(path, true);
		}
		foreach (var isFolder in new[] { false, true })
		{
			foreach (var item in _settings?[isFolder ? "mappingFolders" : "mappingProjects"]?.AsArray() ?? [])
			{
				if (item?.GetValue<string>() is { } path && Path.IsPathFullyQualified(path))
				{
					EnsureScopeNode(path, isFolder);
				}
			}
		}
		_projects = _projectNodes.Keys.ToArray();
	}

	private static void AddMatrixControl(Grid grid, Control control, int row)
	{
		Grid.SetRow(control, row);
		grid.Children.Add(control);
	}

	private void RefreshMatrix(bool keepRows = false)
	{
		var revision = ++_matrixRevision;
		_matrixRead?.Cancel();
		ClearMatrixReview();
		if (!ReferenceEquals(Details.SelectedItem, AvailabilityTab) || _disposing)
		{
			return;
		}
		if (_matrixRefreshError is { } error)
		{
			_matrixSummary.Text = error;
			return;
		}
		var scope = CurrentScope;
		var rows = MatrixMembers();
		if (!keepRows || _matrixCells.Count == 0)
		{
			_matrixCells = [];
			_matrixRows.Children.Clear();
			_matrixRows.Children.Add(new TextBlock { Text = "Reading availability…" });
		}
		_matrixRead = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
		MatrixReady = ReadMatrixAsync(rows, scope, revision, _matrixRead);
	}

	private (Row Row, SkillCatalogSource Source)[] MatrixMembers() =>
		_visibleRows.GroupBy(r => r.SourceGroup.Location, StringComparer.OrdinalIgnoreCase)
			.SelectMany(group => group.Select(r => (r, group.First().SourceGroup))).ToArray();

	private void PruneMatrixDrafts(bool preserveNotice)
	{
		var originalCount = _matrixDraft.Count + _sourceDraft.Count;
		if (originalCount == 0)
		{
			if (!preserveNotice)
			{
				ClearMatrixPruneNotice();
			}
			return;
		}
		var members = MatrixMembers();
		foreach (var (key, edit) in _matrixDraft.ToArray())
		{
			if (!members.Any(member => member.Row.Installation is { } installation
				&& string.Equals(installation.ManifestFor(edit.Profile), key.Manifest, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(member.Row.Skill.Name, edit.SkillName, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(member.Source.Location, edit.Source?.Location, StringComparison.OrdinalIgnoreCase)))
			{
				_matrixDraft.Remove(key);
			}
		}
		var locations = members.Select(member => member.Source.Location).ToHashSet(StringComparer.OrdinalIgnoreCase);
		foreach (var key in _sourceDraft.Keys.Where(key => !locations.Contains(key.Location)).ToArray())
		{
			_sourceDraft.Remove(key);
		}
		var removed = originalCount - _matrixDraft.Count - _sourceDraft.Count;
		if (removed > 0)
		{
			_matrixPruneNotice = $"Removed {removed} pending mapping edit(s) because the skill or source changed. Review the current mapping before applying more changes.";
			ShowLoadWarnings();
		}
		else if (!preserveNotice)
		{
			ClearMatrixPruneNotice();
		}
	}

	private void ClearMatrixPruneNotice()
	{
		if (_matrixPruneNotice is not null)
		{
			_matrixPruneNotice = null;
			ShowLoadWarnings();
		}
	}

	private async Task ReadMatrixAsync((Row Row, SkillCatalogSource Source)[] rows, MappingScope scope, int revision, CancellationTokenSource read)
	{
		var token = read.Token;
		var project = scope.Directory;
		try
		{
			var cells = await Task.Run(() =>
			{
				var result = new List<MatrixCell>();
				foreach (var (row, source) in rows)
				{
					foreach (var profile in _profiles)
					{
						token.ThrowIfCancellationRequested();
						var file = row.Installation?.ManifestFor(profile);
						var key = new MatrixKey(profile.Id, file ?? "", scope);
						try
						{
							if (file != null && !_activation.CanConfigureMember(profile, row.SourceGroup, file, scope.IsFolder ? null : project, _profiles))
							{
								result.Add(new(row, source, profile, key, null, SkillActivationService.PrivateSkillMessage, null));
								continue;
							}
							var state = file is null ? null : _activation.InspectMatrix(profile, source, row.Skill.Name, file, project, scope.IsFolder);
							result.Add(new(row, source, profile, key, state, null, state?.SourceMode ?? _activation.InspectSource(profile, source, project, scope.IsFolder)));
						}
						catch (Exception ex) when (ex is not OperationCanceledException)
						{
							result.Add(new(row, source, profile, key, null, ex.Message, null));
						}
					}
				}
				return result;
			}, token);
			if (revision == _matrixRevision && !token.IsCancellationRequested)
			{
				_matrixCells = cells;
				RenderMatrixRows();
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		finally
		{
			if (ReferenceEquals(_matrixRead, read))
			{
				_matrixRead = null;
			}
			read.Dispose();
		}
	}

	private void RenderMatrixRows()
	{
		_matrixRows.Children.Clear();
		if (_matrixCells.Count == 0)
		{
			_matrixRows.Children.Add(new TextBlock
			{
				Text = _profiles.Length == 0
				? "Add a Codex or Claude launch profile to configure availability."
				: "Add a source in Catalog to configure availability."
			});
			return;
		}
		if (CurrentScope.IsFolder)
		{
			_matrixRows.Children.Add(new TextBlock
			{
				Text = "Folder mappings apply to sessions launched through Pact in this folder and its subfolders.",
				TextWrapping = TextWrapping.Wrap
			});
		}
		else if (CurrentScope.Directory is not null && _profiles.Any(p => p.Kind == AgentKind.Codex))
		{
			_matrixRows.Children.Add(new TextBlock
			{
				Text = "Codex project visibility overrides apply when launching through Pact. Direct CLI launches use native skill discovery.",
				TextWrapping = TextWrapping.Wrap
			});
		}
		var header = MatrixLine();
		AddCell(header, new TextBlock { Text = "Skill group / skill" }, 0);
		for (var i = 0; i < _profiles.Length; i++)
		{
			var profile = _profiles[i];
			var heading = new TextBlock { Text = $"{profile.DisplayName} ({profile.Id})", TextWrapping = TextWrapping.Wrap };
			AddCell(header, heading, i + 1);
			if (string.Equals(profile.Id, _selectedProfileId, StringComparison.OrdinalIgnoreCase))
			{
				heading.FontWeight = FontWeight.SemiBold;
				if (!_selectedProfileShown)
				{
					Dispatcher.UIThread.Post(() =>
					{
						if (heading.IsLoaded)
						{
							heading.BringIntoView();
							_selectedProfileShown = true;
						}
					}, DispatcherPriority.Loaded);
				}
			}
		}
		foreach (var source in _matrixCells.GroupBy(cell => cell.Source)
			.OrderBy(group => group.Key.Label, StringComparer.OrdinalIgnoreCase)
			.ThenBy(group => group.Key.Location, StringComparer.OrdinalIgnoreCase))
		{
			var line = MatrixLine();
			var expanded = _expandedSources.Contains(source.Key.Location);
			var toggle = new Button { Content = $"{(expanded ? "▾" : "▸")} {source.Key.Label}", HorizontalAlignment = HorizontalAlignment.Stretch };
			AutomationProperties.SetName(toggle, $"Expand or collapse {source.Key.Label}");
			ToolTip.SetTip(toggle, source.Key.Location);
			toggle.Click += (_, _) =>
			{
				if (!_expandedSources.Remove(source.Key.Location))
				{
					_expandedSources.Add(source.Key.Location);
				}
				RenderMatrixRows();
			};
			AddCell(line, toggle, 0);
			for (var i = 0; i < _profiles.Length; i++)
			{
				var profile = _profiles[i];
				AddCell(line, MatrixChoice(source.Where(c => c.Profile.Id == profile.Id).ToArray(),
					$"All skills in {source.Key.Label} for {profile.Id}", group: true), i + 1);
			}
			if (!expanded)
			{
				continue;
			}
			foreach (var skill in source.GroupBy(c => c.Row))
			{
				var child = MatrixLine();
				var label = new TextBlock
				{
					Text = skill.Key.Label,
					Margin = new Thickness(18, 4, 0, 0),
					TextWrapping = TextWrapping.Wrap
				};
				ToolTip.SetTip(label, skill.Key.Installation?.ResolvedManifest ?? skill.Key.Skill.RelativePath);
				AddCell(child, label, 0);
				if (skill.Key.Installation is null)
				{
					var prompt = new TextBlock { Text = "Install from Catalog", VerticalAlignment = VerticalAlignment.Center };
					Grid.SetColumnSpan(prompt, _profiles.Length);
					AddCell(child, prompt, 1);
					continue;
				}
				var column = 1;
				foreach (var cell in skill)
				{
					if (cell.Error != null)
					{
						AddCell(child, MatrixChoice([cell], $"{cell.Row.Label} for {cell.Profile.Id}", group: false), column++);
						continue;
					}
					var stack = new StackPanel { Spacing = 2 };
					stack.Children.Add(MatrixChoice([cell], $"{cell.Row.Label} for {cell.Profile.Id}", group: false));
					var state = MatrixEffectiveText(cell);
					var status = new TextBlock { Text = state, FontSize = 12, TextWrapping = TextWrapping.Wrap };
					ToolTip.SetTip(status, cell.State!.Reason);
					stack.Children.Add(status);
					AddCell(child, stack, column++);
				}
			}
		}
	}

	private Grid MatrixLine()
	{
		var grid = new Grid { ColumnSpacing = 8 };
		grid.ColumnDefinitions.Add(new ColumnDefinition(240, GridUnitType.Pixel));
		foreach (var _ in _profiles)
		{
			grid.ColumnDefinitions.Add(new ColumnDefinition(195, GridUnitType.Pixel));
		}
		_matrixRows.Children.Add(grid);
		return grid;
	}

	private static void AddCell(Grid grid, Control control, int column)
	{
		Grid.SetColumn(control, column);
		grid.Children.Add(control);
	}

	private static SourceKey GroupKey(MatrixCell cell) => new(cell.Profile.Id, cell.Source.Location.ToUpperInvariant(), cell.Key.Scope);

	private string MatrixEffectiveText(MatrixCell cell)
	{
		var state = cell.State!;
		var childChanged = _matrixDraft.TryGetValue(cell.Key, out var child);
		var sourceChanged = _sourceDraft.TryGetValue(GroupKey(cell), out var source);
		if (childChanged || sourceChanged)
		{
			var childMode = child?.Mode ?? state.ExplicitMode;
			var sourceMode = source?.Mode ?? state.SourceMode;
			if (childMode != SkillMode.Inherit)
			{
				return $"{ModeLabel(childMode)} · individual rule";
			}
			if (sourceMode != SkillMode.Inherit)
			{
				return $"{ModeLabel(sourceMode)} · from group";
			}
			return $"{(state.EffectiveEnabled ? "On" : "Off")} now · inherit after Apply";
		}
		if (state.ExplicitMode == SkillMode.Inherit && state.SourceMode != SkillMode.Inherit)
		{
			if (state.EffectiveEnabled == (state.SourceMode == SkillMode.Enabled))
			{
				return $"{(state.EffectiveEnabled ? "On" : "Off")} · from group";
			}
		}
		var origin = state.OriginDirectory is { } folder && !state.Reason.Contains(folder, StringComparison.OrdinalIgnoreCase) ? $" · {folder}" : "";
		return $"{(state.EffectiveEnabled ? "On" : "Off")} · {state.Reason}{origin}";
	}

	private static string ModeLabel(SkillMode mode) => mode switch
	{
		SkillMode.Enabled => "On",
		SkillMode.Disabled => "Off",
		_ => "Inherit"
	};

	private Control MatrixChoice(MatrixCell[] cells, string label, bool group)
	{
		var privateOnly = group && cells.All(c => c.State == null)
			? cells.FirstOrDefault(c => c.Error == SkillActivationService.PrivateSkillMessage) : null;
		var cell = privateOnly ?? (group ? cells.FirstOrDefault(c => c.SourceMode is not null) ?? cells[0] : cells[0]);
		var key = GroupKey(cell);
		if (cell.Error is not null)
		{
			if (cell.Error == SkillActivationService.PrivateSkillMessage)
			{
				var unavailable = new TextBlock { Text = "Profile-specific", VerticalAlignment = VerticalAlignment.Center };
				AutomationProperties.SetName(unavailable, $"{label}: {cell.Error}");
				ToolTip.SetTip(unavailable, cell.Error);
				return unavailable;
			}
			return new TextBlock { Text = cell.Error, TextWrapping = TextWrapping.Wrap };
		}
		var selected = group
			? (_sourceDraft.TryGetValue(key, out var policy) ? policy.Mode : cell.SourceMode)
			: (_matrixDraft.TryGetValue(cell.Key, out var edit) ? edit.Mode : cell.State!.ExplicitMode);
		var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
		foreach (var mode in new[] { SkillMode.Enabled, SkillMode.Disabled, SkillMode.Inherit })
		{
			var value = ModeLabel(mode);
			var button = new ToggleButton { Content = value, IsChecked = selected == mode, Padding = new Thickness(8, 3) };
			AutomationProperties.SetName(button, $"{value}: {label}");
			ToolTip.SetTip(button, group ? $"Set the source default for {cell.Profile.DisplayName}. Individual choices take precedence." : label);
			button.Click += (_, _) =>
			{
				_matrixNotice = null;
				ClearMatrixPruneNotice();
				if (group)
				{
					if (mode == cell.SourceMode)
					{
						_sourceDraft.Remove(key);
					}
					else
					{
						_sourceDraft[key] = new(cell.Profile, cell.Source, cell.Key.Project, mode, cell.Key.Scope.IsFolder);
					}
				}
				else if (mode == cell.State!.ExplicitMode)
				{
					_matrixDraft.Remove(cell.Key);
				}
				else
				{
					_matrixDraft[cell.Key] = new(cell.Profile, cell.Row.Skill.Name, cell.Key.Manifest, cell.Key.Project, mode, cell.Source, cell.Key.Scope.IsFolder);
				}
				ClearMatrixReview();
				RenderMatrixRows();
			};
			choices.Children.Add(button);
		}
		return choices;
	}

	private void ClearMatrixReview()
	{
		_impactRevision++;
		_matrixPreview = null;
		var count = _matrixDraft.Count + _sourceDraft.Count;
		_matrixDiscard.IsVisible = count > 0;
		_matrixApply.IsVisible = false;
		_matrixSummary.Text = count == 0 ? _matrixNotice ?? "" : "Checking affected files…";
		if (count > 0 && _matrixRefreshError is null && MatrixImpactReady.IsCompleted && ReferenceEquals(Details.SelectedItem, AvailabilityTab) && !_disposing)
		{
			MatrixImpactReady = UpdateMatrixImpactAsync();
		}
	}

	private async Task UpdateMatrixImpactAsync()
	{
		while (!_disposing && ReferenceEquals(Details.SelectedItem, AvailabilityTab) && _matrixDraft.Count + _sourceDraft.Count > 0)
		{
			var revision = _impactRevision;
			var edits = _matrixDraft.Values.ToArray();
			var sources = _sourceDraft.Values.ToArray();
			var members = MatrixMembers().Where(pair => pair.Row.Installation is not null).SelectMany(pair => _profiles.Select(p =>
				new SkillMatrixMember(pair.Row.SourceGroup, pair.Row.Skill.Name, pair.Row.Installation!.ManifestFor(p), p))).ToArray();
			try
			{
				var preview = await Task.Run(() => _activation.PreviewBatch(edits, _profiles, sources, members), _lifetime.Token);
				if (revision != _impactRevision || _disposing)
				{
					continue;
				}
				var settings = Path.TrimEndingDirectorySeparator(_paths.SettingsDirectory) + Path.DirectorySeparatorChar;
				var files = preview.AffectedFiles.OrderBy(path => path.StartsWith(settings, StringComparison.OrdinalIgnoreCase)).ToArray();
				var scopes = _matrixDraft.Keys.Select(key => key.Scope).Concat(_sourceDraft.Keys.Select(key => key.Scope)).Distinct();
				_matrixSummary.Text = files.Length == 0 ? "No files need to change."
					: $"{string.Join(", ", scopes)} · Profiles: {string.Join(", ", preview.AffectedProfiles)}\n{string.Join(Environment.NewLine, files)}";
				_matrixPreview = preview;
				_matrixApply.IsVisible = files.Length > 0;
			}
			catch (OperationCanceledException) when (_disposing) { }
			catch (Exception ex)
			{
				if (revision != _impactRevision)
				{
					continue;
				}
				_matrixSummary.Text = ex.Message;
			}
			return;
		}
	}

	private async Task ApplyMatrixAsync()
	{
		if (_matrixPreview is not { } preview)
		{
			return;
		}
		_matrixPreview = null;
		_matrixApply.IsVisible = false;
		try
		{
			await Task.Run(() => _activation.ApplyAsync(preview, _lifetime.Token), _lifetime.Token);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			ClearMatrixReview();
			await MatrixImpactReady;
			_matrixSummary.Text = $"{ex.Message}{Environment.NewLine}{_matrixSummary.Text}";
			return;
		}

		_matrixDraft.Clear();
		_sourceDraft.Clear();
		const string applied = "Changes applied. Restart affected sessions to use the new availability.";
		try
		{
			await RefreshNativeRowsAsync();
			RefreshList();
			await MatrixReady;
			_matrixSummary.Text = _matrixNotice = applied;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			_matrixRefreshError = $"{applied}{Environment.NewLine}Could not refresh availability. Reopen Skills to reload it. {ex.Message}";
			_matrixRevision++;
			_matrixRead?.Cancel();
			_matrixCells = [];
			_matrixRows.Children.Clear();
			ClearMatrixReview();
			_matrixSummary.Text = _matrixRefreshError;
		}
	}
}

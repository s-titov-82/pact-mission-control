using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed partial class McpCatalogWindow
{
	private sealed record MappingScope(string? Directory, bool IsFolder = false)
	{
		public override string ToString() => Directory is null ? "Default" : $"{(IsFolder ? "Folder" : "Project")} · {Directory}";
	}
	private sealed record MappingTarget(string Id, McpCatalogServer? Definition)
	{
		internal bool IsNative => Definition is null;
	}
	private sealed record MappingKey(string ServerId, bool IsNative, ProfileChoice Profile, MappingScope Scope);

	private readonly Dictionary<MappingKey, bool?> _mappingDraft = [];
	private readonly Dictionary<(string Directory, bool IsFolder), TreeViewItem> _scopeNodes = [];
	private MappingScope CurrentScope => (Scopes.SelectedItem as TreeViewItem)?.Tag as MappingScope ?? new(null);

	private TreeViewItem EnsureScope(string directory, bool isFolder)
	{
		var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
		var key = (path.ToUpperInvariant(), isFolder);
		if (_scopeNodes.TryGetValue(key, out var existing))
		{
			return existing;
		}
		var scope = new MappingScope(path, isFolder);
		var parent = isFolder ? Directory.GetParent(path)?.FullName : path;
		var label = Path.GetFileName(path);
		var node = new TreeViewItem { Header = isFolder ? (label.Length > 0 ? label : path) : "This project", Tag = scope, IsExpanded = true };
		AutomationProperties.SetName(node, scope.ToString());
		ToolTip.SetTip(node, scope.ToString());
		var siblings = parent is null ? Scopes.Items : EnsureScope(parent, isFolder: true).Items;
		siblings.Add(node);
		_scopeNodes.Add(key, node);
		return node;
	}

	private async void OnAddScope(object? sender, RoutedEventArgs e)
	{
		var isFolder = (sender as MenuItem)?.Tag as string == "Folder";
		var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
		{
			Title = isFolder ? "Folder and descendants" : "Single project"
		});
		if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } directory)
		{
			Scopes.SelectedItem = EnsureScope(directory, isFolder);
		}
	}

	private static IEnumerable<string> ProjectDirectories(TreeViewItem branch)
	{
		foreach (var child in branch.Items.OfType<TreeViewItem>())
		{
			if (child.Tag is MappingScope { IsFolder: false, Directory: { } project })
			{
				yield return project;
			}
			foreach (var descendant in ProjectDirectories(child))
			{
				yield return descendant;
			}
		}
	}

	private void RenderMapping()
	{
		MappingRows.Children.Clear();
		if (_snapshot is not { } snapshot)
		{
			UpdateMappingActions();
			return;
		}

		var registered = snapshot.Settings.Servers;
		var scope = CurrentScope;
		var native = _native.Where(entry => McpCatalogService.SupportsNativeManagement(entry.Profile.Profile)).ToLookup(entry => entry.Profile, entry => entry.Server);
		var savedNativeNames = snapshot.Settings.Selections.Where(rule => rule.IsNative &&
			_profiles.Any(profile => McpCatalogService.Matches(rule, profile.Profile, scope.Directory, scope.IsFolder))).Select(rule => rule.ServerId);
		var shared = _profiles.ToDictionary(profile => profile, profile => registered.Where(server =>
			(!scope.IsFolder || !native[profile].Any(item => item.Name == server.NativeOrigin?.Name && item.ProjectDirectory is not null)) &&
			McpCatalogService.UsesNativeOrigin(server, profile.Profile, native[profile].Where(item => item.IsProjectConfigurationTrusted)))
			.Select(server => server.NativeOrigin!.Name).ToHashSet(StringComparer.Ordinal));
		var servers = registered.Select(server => new MappingTarget(server.Id, server)).Concat(_profiles
			.SelectMany(profile => native[profile].Select(server => server.Name).Where(name => McpCatalogService.IsConfigurableNativeName(name) && !shared[profile].Contains(name)))
			.Concat(savedNativeNames)
			.Distinct().Select(name => new MappingTarget(name, null)))
			.OrderBy(server => server.IsNative).ThenBy(server => server.Id, StringComparer.OrdinalIgnoreCase).ToArray();
		var rules = snapshot.Settings.Selections.ToLookup(rule => (rule.IsNative, rule.ServerId));
		foreach (var key in _mappingDraft.Keys.Where(key => (!key.IsNative && !registered.Any(server => server.Id == key.ServerId)) ||
			_mappingDraft[key] == SavedChoice(key, rules[(key.IsNative, key.ServerId)])).ToArray())
		{
			_mappingDraft.Remove(key);
		}
		UpdateMappingActions();
		if (servers.Length == 0 || _profiles.Length == 0)
		{
			MappingRows.Children.Add(new TextBlock
			{
				Text = servers.Length == 0 ? "Add a server in Catalog to configure it here." : "No compatible launch profiles."
			});
			return;
		}

		var inheritedRules = ApplyMappingDraft(snapshot.Settings.Selections).ToLookup(rule => (rule.IsNative, rule.ServerId));
		var header = AddMappingRow("Server", FontWeight.SemiBold);
		for (var i = 0; i < _profiles.Length; i++)
		{
			var profile = _profiles[i];
			var title = new TextBlock { Text = profile.ToString(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
			Grid.SetColumn(title, i + 1);
			header.Children.Add(title);
		}
		foreach (var server in servers)
		{
			var row = AddMappingRow(server.IsNative ? $"{server.Id} · Native" : server.Id, FontWeight.Normal);
			for (var i = 0; i < _profiles.Length; i++)
			{
				var profile = _profiles[i];
				var key = new MappingKey(server.Id, server.IsNative, profile, scope);
				var saved = SavedChoice(key, rules[(key.IsNative, key.ServerId)]);
				var missingDeclaration = server.IsNative && native[profile].All(item => item.Name != server.Id);
				if (server.IsNative && (shared[profile].Contains(server.Id) || missingDeclaration && saved is null))
				{
					var missing = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center };
					Grid.SetColumn(missing, i + 1);
					row.Children.Add(missing);
					continue;
				}
				var current = _mappingDraft.GetValueOrDefault(key, saved);
				var ownsOrigin = server.Definition?.NativeOrigin is { } origin && shared[profile].Contains(origin.Name) &&
					McpCatalogService.UsesNativeOrigin(server.Definition, profile.Profile, native[profile].Where(item => item.IsProjectConfigurationTrusted));
				var inheritedChoices = inheritedRules[(key.IsNative, key.ServerId)].Where(rule => !MatchesScope(rule, key));
				if (ownsOrigin)
				{
					inheritedChoices = inheritedChoices.Concat(inheritedRules[(true, server.Definition!.NativeOrigin!.Name)]);
				}
				var inherited = McpCatalogService.EffectiveSelection(inheritedChoices, profile.Profile, CurrentProject, scope.Directory)?.Enabled;
				var inheritedLabel = inherited switch { true => "On", false => "Off", _ => server.IsNative || ownsOrigin ? "Agent setting" : "Off" };
				var label = $"{(server.IsNative ? server.Id + " (native)" : server.Id)} — {profile.Profile.DisplayName}";
				var nativeName = server.IsNative ? server.Id : ownsOrigin ? server.Definition!.NativeOrigin!.Name : null;
				var matching = native[profile].Where(item => item.Name == nativeName && (!scope.IsFolder || item.ProjectDirectory is null))
					.OrderByDescending(item => item.ProjectDirectory is not null)
					.ThenByDescending(item => Path.GetFileName(item.SourcePath) == ".claude.json").FirstOrDefault();
				var nativeBlocker = profile.Profile.Kind == AgentKind.Claude && matching?.IsEnabled == false ? matching.BlockingSettings : null;
				var untrustedProject = server.IsNative && scope.Directory is not null && !scope.IsFolder && profile.Profile.Kind == AgentKind.Codex &&
					!missingDeclaration && !native[profile].Any(item => item.Name == server.Id && item.IsProjectConfigurationTrusted);
				var choice = CreateMappingChoice(key, saved, current, label, inheritedLabel, missingDeclaration, nativeBlocker, untrustedProject);
				Grid.SetColumn(choice, i + 1);
				row.Children.Add(choice);
			}
		}
	}

	private StackPanel CreateMappingChoice(MappingKey key, bool? saved, bool? current, string label, string inherited,
		bool missingDeclaration, string? nativeBlocker, bool untrustedProject)
	{
		var cell = new StackPanel { Spacing = 2 };
		var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
		var inheritedStatus = new TextBlock { Text = $"Inherited: {inherited}", IsVisible = current is null };
		cell.Children.Add(choices);
		cell.Children.Add(inheritedStatus);
		if (missingDeclaration || nativeBlocker is not null || untrustedProject)
		{
			var notice = new TextBlock
			{
				Text = missingDeclaration ? "Missing declaration" : untrustedProject ? "Project not trusted in Codex" : "Blocked in Claude",
				TextWrapping = TextWrapping.Wrap
			};
			ToolTip.SetTip(notice, missingDeclaration ? "Choose Inherit to clear the saved rule." : nativeBlocker);
			cell.Children.Add(notice);
		}
		foreach (var mode in new bool?[] { true, false, null })
		{
			var caption = MappingLabel(mode);
			var available = mode is null || !missingDeclaration && !untrustedProject && (mode != true || nativeBlocker is null);
			var button = new ToggleButton { Content = caption, IsChecked = mode == current, IsEnabled = available, Padding = new Thickness(8, 3) };
			AutomationProperties.SetName(button, $"{caption}: {label}");
			ToolTip.SetTip(button, mode is null ? $"Inherit: {inherited}" : label);
			button.Click += (_, _) =>
			{
				if (!available)
				{
					return;
				}
				if (mode == saved)
				{
					_mappingDraft.Remove(key);
				}
				else
				{
					_mappingDraft[key] = mode;
				}
				foreach (var option in choices.Children.OfType<ToggleButton>())
				{
					option.IsChecked = ReferenceEquals(option, button);
				}
				inheritedStatus.IsVisible = mode is null;
				UpdateMappingActions();
			};
			choices.Children.Add(button);
		}
		return cell;
	}

	private Grid AddMappingRow(string label, FontWeight weight)
	{
		var row = new Grid { ColumnSpacing = 8 };
		row.ColumnDefinitions.Add(new ColumnDefinition(225, GridUnitType.Pixel));
		foreach (var _ in _profiles)
		{
			row.ColumnDefinitions.Add(new ColumnDefinition(185, GridUnitType.Pixel));
		}
		row.Children.Add(new TextBlock
		{
			Text = label,
			FontWeight = weight,
			TextWrapping = TextWrapping.Wrap,
			VerticalAlignment = VerticalAlignment.Center
		});
		MappingRows.Children.Add(row);
		return row;
	}

	private static bool? SavedChoice(MappingKey key, IEnumerable<McpCatalogSelection> rules) =>
		rules.SingleOrDefault(rule => MatchesScope(rule, key))?.Enabled;

	private static string MappingLabel(bool? mode) => mode switch { true => "On", false => "Off", _ => "Inherit" };

	private static bool MatchesScope(McpCatalogSelection rule, MappingKey key) =>
		McpCatalogService.Matches(rule, key.Profile.Profile, key.Scope.Directory, key.Scope.IsFolder);

	private void UpdateMappingActions()
	{
		ReviewAvailability.IsEnabled = _snapshot is not null && _mappingDraft.Count > 0;
		DiscardMapping.IsEnabled = _mappingDraft.Count > 0;
	}

	private void OnReviewMapping(object? sender, RoutedEventArgs e)
	{
		if (_snapshot is not { } snapshot || _mappingDraft.Count == 0)
		{
			return;
		}
		var changes = _mappingDraft.Select(pair =>
		{
			var (key, enabled) = pair;
			return $"{key.ServerId}{(key.IsNative ? " (native)" : "")} · {key.Profile} · {MappingLabel(enabled)}\n{key.Scope}";
		});
		ShowPreview(snapshot.Settings with { Selections = ApplyMappingDraft(snapshot.Settings.Selections) }, string.Join("\n\n", changes),
			_mappingDraft.Keys.Select(key => key.Profile.ToString()).Distinct().ToArray());
	}

	private McpCatalogSelection[] ApplyMappingDraft(McpCatalogSelection[] saved)
	{
		var selections = saved.ToList();
		foreach (var (key, enabled) in _mappingDraft)
		{
			var profile = key.Profile.Profile;
			var index = selections.FindIndex(rule => rule.IsNative == key.IsNative && rule.ServerId == key.ServerId && MatchesScope(rule, key));
			if (enabled is { } value)
			{
				if (index < 0)
				{
					selections.Add(new(profile.Id, profile.Kind, key.Scope.Directory, key.ServerId, value) { IsFolder = key.Scope.IsFolder, IsNative = key.IsNative });
				}
				else
				{
					selections[index] = selections[index] with { Enabled = value };
				}
			}
			else if (index >= 0)
			{
				selections.RemoveAt(index);
			}
		}
		return selections.ToArray();
	}

	private void OnDiscardMapping(object? sender, RoutedEventArgs e)
	{
		_mappingDraft.Clear();
		Status.Text = "";
		RenderMapping();
	}
}

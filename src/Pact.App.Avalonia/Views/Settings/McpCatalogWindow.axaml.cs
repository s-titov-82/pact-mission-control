using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed partial class McpCatalogWindow : Window
{
	private sealed record ProfileChoice(AgentProfileRecord Profile)
	{
		public override string ToString() => $"{Profile.DisplayName} ({Profile.Kind})";
	}

	private sealed record ServerChoice(string Name, string Transport, string Source, McpCatalogServer? Definition,
		NativeMcpServer? Native = null, AgentKind? Kind = null)
	{
		public override string ToString() => $"{Name} · {Transport}{(Native?.ProjectDirectory is null ? "" : " · Project")}" +
			(Native is null ? "" : $" · {NativeSwitchLabel(Native.IsEnabled)}");
	}

	private static string NativeSwitchLabel(bool? enabled) => enabled switch { true => "Enabled", false => "Disabled", _ => "Unknown" };

	private readonly McpCatalogService _catalog;
	private readonly ProfileChoice[] _profiles;
	private McpCatalogSnapshot? _snapshot;
	private McpCatalogSettings? _pending;
	private McpNativeEditPreview? _nativePending;
	private (ProfileChoice Profile, NativeMcpServer Server)[] _native = [];
	private McpCatalogServer? _editing;
	private bool _addingServer;
	private McpNativeDefinitionDraft? _nativeDraft;
	private (ProfileChoice Owner, NativeMcpServer Source)? _sharing;
	private bool _refreshing;
	private int _scopeRead;

	/// <summary>Creates the catalog window for Avalonia's XAML loader.</summary>
	public McpCatalogWindow() : this(new AppPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pact")), [], [])
	{
	}

	internal McpCatalogWindow(AppPaths paths, IEnumerable<AgentProfileRecord> profiles, IEnumerable<string> projects,
		string? selectedProject = null, string? selectedProfileId = null)
	{
		_catalog = new(paths);
		_profiles = profiles.Where(McpCatalogService.Supports).OrderByDescending(profile => profile.Id == selectedProfileId)
			.Select(profile => new ProfileChoice(profile)).ToArray();
		InitializeComponent();
		NativeDefinitionContent.PropertyChanged += (_, args) =>
		{
			if (args.Property == TextBox.TextProperty && _nativeDraft is { } draft)
			{
				var dirty = NativeDefinitionContent.Text != draft.Content;
				SetCatalogEditing(dirty);
				NativeActions.IsEnabled = !dirty;
			}
		};
		_refreshing = true;
		var defaults = new TreeViewItem { Header = "Default", Tag = new MappingScope(null) };
		Scopes.Items.Add(defaults);
		foreach (var path in projects.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
		{
			EnsureScope(path, isFolder: false);
		}
		Scopes.SelectedItem = selectedProject is null ? defaults : EnsureScope(selectedProject, isFolder: false);
		_refreshing = false;
		Reload();
		Opened += async (_, _) =>
		{
			if (selectedProject is not null)
			{
				Catalog.SelectedItem = MappingTab;
			}
			await LoadNativeAsync();
		};
		Closed += (_, _) => _scopeRead++;
	}

	private string? CurrentProject => CurrentScope.IsFolder ? null : CurrentScope.Directory;
	private ServerChoice? SelectedServer => (Servers.SelectedItem as TreeViewItem)?.Tag as ServerChoice;
	private McpCatalogServer? CurrentServer => SelectedServer?.Definition;

	private void Reload(string? selectedId = null, bool refreshEditor = true)
	{
		try
		{
			_snapshot = _catalog.Read();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
		{
			_snapshot = null;
			Status.Text = $"Could not read the catalog in {_catalog.SettingsPath}.";
		}
		AddServer.IsEnabled = _snapshot is not null;
		foreach (var scope in _snapshot?.Settings.Selections.Select(rule => (rule.ProjectDirectory, rule.IsFolder)).Distinct() ?? [])
		{
			if (scope.ProjectDirectory is { } directory)
			{
				EnsureScope(directory, scope.IsFolder);
			}
		}
		RefreshServers(selectedId, refreshEditor);
		RenderMapping();
	}

	private async Task LoadNativeAsync(bool refreshEditor = false)
	{
		var read = ++_scopeRead;
		var isFolder = CurrentScope.IsFolder;
		string?[] projects = isFolder && Scopes.SelectedItem is TreeViewItem folder
			? [null, .. ProjectDirectories(folder)] : [CurrentProject];
		NativeStatus.Text = "";
		var (servers, diagnostics) = await Task.Run(() =>
		{
			List<string> messages = [];
			var declarations = _profiles.SelectMany(profile => projects.SelectMany(project => McpCatalogService.ReadNative(profile.Profile, project, messages.Add)
				.Where(server => !isFolder || project is null || server.ProjectDirectory is not null))
				.Distinct().Select(server => (profile, server))).ToArray();
			return (declarations, string.Join(Environment.NewLine, messages.Distinct()));
		});
		if (read == _scopeRead)
		{
			_native = servers;
			RefreshServers(refreshEditor: refreshEditor);
			RenderMapping();
			NativeStatus.Text = diagnostics;
		}
	}

	private void RefreshServers(string? selectedId = null, bool refreshEditor = false)
	{
		var previous = SelectedServer;
		var rows = (_snapshot?.Settings.Servers ?? []).Select(server => new ServerChoice(server.Id,
			server.Url is not null ? "HTTP" : "stdio", _catalog.SettingsPath, server))
			.Concat(_native.GroupBy(entry => (entry.Server.Name, entry.Server.Transport, entry.Server.SourcePath, entry.Server.ProjectDirectory, entry.Profile.Profile.Kind))
				.Select(group =>
				{
					var states = group.Select(entry => entry.Server with { IsProjectConfigurationTrusted = true }).Distinct().ToArray();
					var native = states.Length == 1 ? states[0] : states[0] with { IsEnabled = null, CanEnable = false, BlockingSettings = null };
					return new ServerChoice(native.Name, native.Transport, native.SourcePath, null, native, group.Key.Kind);
				}))
			.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase);
		_refreshing = true;
		Servers.Items.Clear();
		TreeViewItem? first = null;
		TreeViewItem? selected = null;
		foreach (var source in rows.GroupBy(row => row.Source, StringComparer.OrdinalIgnoreCase)
			.OrderBy(group => group.Key != _catalog.SettingsPath).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
		{
			var registered = source.Key == _catalog.SettingsPath;
			var group = new TreeViewItem { Header = registered ? "Registered in Pact" : Path.GetFileName(source.Key), Tag = source.Key, IsExpanded = true };
			ToolTip.SetTip(group, source.Key);
			Servers.Items.Add(group);
			foreach (var row in source)
			{
				var item = new TreeViewItem { Header = row.ToString(), Tag = row };
				group.Items.Add(item);
				first ??= item;
				if (selectedId is not null ? row.Definition?.Id == selectedId :
					previous is not null && (row.Native?.MatchesDeclaration(previous.Native) ??
						(previous.Native is null && row.Name == previous.Name && row.Source == previous.Source)))
				{
					selected = item;
				}
			}
		}
		Servers.SelectedItem = selected ?? first;
		_refreshing = false;
		if (!_addingServer && (refreshEditor || !NativeShareEditor.IsVisible && selected is null))
		{
			ShowServer();
		}
		else if (!_addingServer && !NativeShareEditor.IsVisible)
		{
			UpdateNativeState(SelectedServer);
		}
	}

	private void ShowServer()
	{
		_addingServer = false;
		_editing = null;
		DefinitionEditor.IsVisible = false;
		SetCatalogEditing(false);
		CloseNativeDefinition();
		NativeShareEditor.IsVisible = false;
		_sharing = null;
		var row = SelectedServer;
		var server = row?.Definition;
		var origin = row is { Native: { } native, Kind: { } kind } ? new McpNativeServerOrigin(kind, native.Name, native.SourcePath, native.ProjectDirectory) : null;
		var shared = origin is null ? null : _snapshot?.Settings.Servers.FirstOrDefault(item => item.NativeOrigin == origin);
		var owner = FindNativeOwner(row);
		NativeActions.IsVisible = owner is not null;
		UpdateNativeState(row);
		DefinitionActions.IsVisible = server is not null;
		var source = Servers.SelectedItem as TreeViewItem;
		ServerTitle.Text = row?.Name ?? source?.Header?.ToString() ?? "No servers found";
		ServerDetails.Text = row is null ? source?.Tag as string ?? "Add a server to make it available to Pact launch profiles." :
			$"{row.Transport} · {row.Source}\n" + (server is null
			? (shared is not null ? $"Shared as {shared.Id}." : owner is not null ? "MCP → Agent mapping applies to launches through Pact." :
				"This declaration is controlled by the agent or its launch arguments.")
			: $"{(server.NativeOrigin is null ? "Agent name" : "Agent name on other profiles")}: {server.AgentName}");
		if (server is not null)
		{
			FillDefinition(server);
		}
		else if (owner is not null && row?.Native is { } installed)
		{
			ShowNativeDefinition(owner.Profile, installed);
		}
	}

	private void UpdateNativeState(ServerChoice? row)
	{
		var enabled = row?.Native?.IsEnabled;
		NativeState.IsVisible = row?.Native is not null;
		NativeState.Text = $"Native configuration: {NativeSwitchLabel(enabled)}";
		var blocks = row?.Native?.BlockingSettings;
		NativeBlocker.IsVisible = blocks is not null;
		NativeBlocker.Text = blocks is null ? null : $"Blocked by {blocks}";
		EnableNative.IsVisible = enabled == false && row?.Native?.CanEnable == true;
		DisableNative.IsVisible = enabled == true;
	}

	private async void OnScopeChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (!_refreshing)
		{
			Status.Text = "";
			_native = [];
			RefreshServers();
			RenderMapping();
			await LoadNativeAsync();
		}
	}

	private void OnServerSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (!_refreshing)
		{
			Status.Text = "";
			ShowServer();
		}
	}

	private void OnAddServer(object? sender, RoutedEventArgs e)
	{
		_addingServer = true;
		CloseNativeDefinition();
		ServerTitle.Text = "Add server";
		ServerDetails.Text = "";
		NativeActions.IsVisible = DefinitionActions.IsVisible = false;
		NativeState.IsVisible = NativeBlocker.IsVisible = false;
		SetCatalogEditing(true);
		FillDefinition(null);
	}
	private void OnCancelDefinition(object? sender, RoutedEventArgs e) => ShowServer();

	private ProfileChoice? FindNativeOwner(ServerChoice? row) => row is { Native: { } native, Kind: { } kind } &&
		McpCatalogService.IsConfigurableNativeName(native.Name) ? _native.FirstOrDefault(entry => entry.Server.MatchesDeclaration(native) &&
		entry.Profile.Profile.Kind == kind).Profile : null;

	private void OnShareServer(object? sender, RoutedEventArgs e)
	{
		var row = SelectedServer;
		if (row?.Native is not { } native || FindNativeOwner(row) is not { } owner)
		{
			return;
		}
		_sharing = (owner, native);
		CloseNativeDefinition();
		NativeActions.IsVisible = false;
		NativeState.IsVisible = NativeBlocker.IsVisible = false;
		NativeShareEditor.IsVisible = true;
		ServerTitle.Text = $"Make {native.Name} shared";
		ServerDetails.Text = native.SourcePath;
		SetCatalogEditing(true);
		NativeShareTargets.ItemsSource = _profiles.Where(profile => NativeConfigurationPath(profile) is { } path &&
			!StringComparer.OrdinalIgnoreCase.Equals(path, native.SourcePath)).ToArray();
	}

	private static string? NativeConfigurationPath(ProfileChoice profile)
	{
		try
		{
			return McpCatalogService.NativeUserConfigurationPath(profile.Profile);
		}
		catch (ArgumentException)
		{
			return null;
		}
	}

	private void OnReviewNativeShare(object? sender, RoutedEventArgs e)
	{
		if (_sharing is not { } sharing)
		{
			return;
		}
		var targets = NativeShareTargets.SelectedItems?.OfType<ProfileChoice>().ToArray() ?? [];
		if (targets.Length == 0)
		{
			Status.Text = "Select at least one destination profile.";
			return;
		}
		try
		{
			var preview = McpCatalogService.PreviewNativeShare(sharing.Owner.Profile, sharing.Source, targets.Select(target => target.Profile).ToArray());
			ShowNativePreview(preview, $"Install {sharing.Source.Name} in {string.Join(", ", targets.Select(target => target.ToString()))}.\n" +
				"The destination files will receive the definition, including any configured credentials. Agent sign-in is separate.");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
		{
			Status.Text = exception.Message;
		}
	}

	private void OnNativeEdit(object? sender, RoutedEventArgs e)
	{
		if (SelectedServer is not { Native: { } source } row || FindNativeOwner(row) is not { } owner ||
			!Enum.TryParse((sender as Button)?.Tag as string, out McpNativeEditAction action))
		{
			return;
		}
		try
		{
			var preview = McpCatalogService.PreviewNativeEdit(owner.Profile, source, action);
			var summary = $"{action} {source.Name} in the native configuration.";
			if (owner.Profile.Kind == AgentKind.Claude)
			{
				if (action is McpNativeEditAction.Enable or McpNativeEditAction.Disable)
				{
					summary += source.ProjectDirectory is null ? "\nThe user-level rule applies to this server name across projects." :
						"\nThe project rule applies to every declaration of this server name in this project.";
				}
				else if (source.BlockingSettings is { } blocked)
				{
					summary += $"\nNative deny rules stay in {blocked}.";
				}
			}
			if (action == McpNativeEditAction.Remove && _snapshot?.Settings.Selections.Any(rule =>
				rule.IsNative && rule.Kind == owner.Profile.Kind && rule.ServerId == source.Name) == true)
			{
				summary += "\nPact rules for this server name stay visible in MCP → Agent mapping and can be cleared there.";
			}
			ShowNativePreview(preview, summary);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
		{
			Status.Text = exception.Message;
		}
	}

	private void ShowNativePreview(McpNativeEditPreview preview, string summary)
	{
		if (preview.Files.Count == 0)
		{
			Status.Text = "No changes to apply.";
			return;
		}
		_nativePending = preview;
		ShowPreviewText($"{summary}\n\n{string.Join(Environment.NewLine, preview.Files)}");
	}

	private void FillDefinition(McpCatalogServer? server)
	{
		_editing = server;
		DefinitionEditor.IsVisible = true;
		ServerId.Text = server?.Id ?? "";
		ServerId.IsReadOnly = server is not null;
		Transport.SelectedIndex = server?.Command is not null ? 1 : 0;
		ServerUrl.Text = server?.Url;
		TokenVariable.Text = server?.BearerTokenVariable;
		Command.Text = server?.Command;
		Arguments.Text = server is null ? "" : JsonSerializer.Serialize(server.Arguments);
		EnvironmentNames.Text = server is null ? "" : string.Join(Environment.NewLine, server.EnvironmentVariables);
	}

	private void SetCatalogEditing(bool editing)
	{
		MappingTab.IsEnabled = Servers.IsEnabled = !editing;
		AddServer.IsVisible = !editing;
	}

	private void OnTransportChanged(object? sender, SelectionChangedEventArgs e)
	{
		HttpFields.IsVisible = Transport.SelectedIndex == 0;
		StdioFields.IsVisible = Transport.SelectedIndex == 1;
	}

	private void OnReviewDefinition(object? sender, RoutedEventArgs e)
	{
		if (_snapshot is not { } snapshot)
		{
			return;
		}
		try
		{
			var id = ServerId.Text?.Trim() ?? "";
			var token = string.IsNullOrWhiteSpace(TokenVariable.Text) ? null : TokenVariable.Text.Trim();
			var server = Transport.SelectedIndex == 0 ? new McpCatalogServer(id, ServerUrl.Text?.Trim() ?? "", null, [], token) :
				new McpCatalogServer(id, null, Command.Text?.Trim(),
					string.IsNullOrWhiteSpace(Arguments.Text) ? [] : JsonSerializer.Deserialize<string[]>(Arguments.Text) ?? throw new JsonException(), null)
				{
					EnvironmentVariables = (EnvironmentNames.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
						.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
				};
			server = server with { NativeOrigin = _editing?.NativeOrigin, Extra = _editing?.Extra };
			McpCatalogService.ValidateServer(server);
			if (_editing is { } original)
			{
				var current = snapshot.Settings.Servers.SingleOrDefault(item => item.Id == original.Id);
				if (!ReferenceEquals(current, original) && JsonSerializer.Serialize(current) != JsonSerializer.Serialize(original))
				{
					Status.Text = "This server changed on disk. Keep a copy of your draft and reopen the current definition before editing it.";
					return;
				}
			}
			var otherServers = snapshot.Settings.Servers.Where(item => item.Id != _editing?.Id).ToArray();
			if (otherServers.Any(item => item.Id == id))
			{
				throw new ArgumentException("A server with that ID already exists.");
			}
			ShowPreview(snapshot.Settings with { Servers = [.. otherServers, server] }, $"{(_editing is null ? "Add" : "Update")} {id}", AffectedProfiles(snapshot, id));
		}
		catch (ArgumentException exception)
		{
			Status.Text = exception.Message;
		}
		catch (JsonException)
		{
			Status.Text = "Enter the arguments as a JSON array of strings.";
		}
	}

	private void OnRemoveServer(object? sender, RoutedEventArgs e)
	{
		if (_snapshot is { } snapshot && CurrentServer is { } server)
		{
			ShowPreview(snapshot.Settings with
			{
				Servers = snapshot.Settings.Servers.Where(item => item.Id != server.Id).ToArray(),
				Selections = snapshot.Settings.Selections.Where(rule => rule.IsNative || rule.ServerId != server.Id).ToArray()
			}, $"Remove {server.Id} and its saved availability choices", AffectedProfiles(snapshot, server.Id));
		}
	}

	private string[] AffectedProfiles(McpCatalogSnapshot snapshot, string serverId) => _profiles
		.Where(item => snapshot.Settings.Selections.Any(rule => !rule.IsNative && rule.ServerId == serverId &&
			rule.ProfileId == item.Profile.Id && rule.Kind == item.Profile.Kind))
		.Select(item => item.ToString()).ToArray();

	private void ShowPreview(McpCatalogSettings pending, string summary, string[] affectedProfiles)
	{
		_pending = pending;
		ShowPreviewText($"{summary}\n\nProfiles: {(affectedProfiles.Length == 0 ? "None" : string.Join(", ", affectedProfiles))}\nFile: {_catalog.SettingsPath}");
	}

	private void ShowPreviewText(string text)
	{
		PreviewText.Text = text;
		Status.Text = "";
		Preview.IsVisible = true;
		Catalog.IsEnabled = false;
		Apply.Focus();
	}

	private async void OnApply(object? sender, RoutedEventArgs e)
	{
		Preview.IsEnabled = false;
		try
		{
			if (_nativePending is { } native)
			{
				var applied = await McpCatalogService.ApplyNativeEditAsync(native, CancellationToken.None);
				ClosePreview();
				if (applied || _nativeDraft is null)
				{
					await LoadNativeAsync(refreshEditor: applied);
				}
				Status.Text = applied ? "Saved. The native changes apply the next time the agent starts." :
					"A native configuration changed on disk. Review the change again before applying.";
				return;
			}
			if (_snapshot is not { } snapshot || _pending is not { } pending)
			{
				return;
			}
			var id = DefinitionEditor.IsVisible ? ServerId.Text?.Trim() : CurrentServer?.Id;
			if (!await _catalog.SaveAsync(snapshot, pending, CancellationToken.None))
			{
				Reload(id, refreshEditor: false);
				if (_snapshot is not null)
				{
					Status.Text = "The MCP catalog changed on disk. Review the change again before applying.";
				}
				ClosePreview();
				return;
			}
			if (ReferenceEquals(Catalog.SelectedItem, MappingTab))
			{
				_mappingDraft.Clear();
			}
			_addingServer = false;
			Status.Text = "Saved. Changes apply when starting or restarting a session.";
			Reload(id);
			ClosePreview();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
		{
			Status.Text = exception.Message;
		}
		finally
		{
			Preview.IsEnabled = true;
		}
	}

	private void ShowNativeDefinition(AgentProfileRecord profile, NativeMcpServer server)
	{
		try
		{
			var draft = McpCatalogService.OpenNativeDefinition(profile, server);
			_nativeDraft = draft;
			NativeDefinitionFormat.Text = profile.Kind == AgentKind.Codex ? "Server configuration (TOML)" : "Server configuration (JSON)";
			NativeDefinitionContent.Text = draft.Content;
			NativeDefinitionContent.IsUndoEnabled = true;
			NativeDefinitionEditor.IsVisible = true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
		{
			Status.Text = "Could not open the selected native definition.";
		}
	}

	private void CloseNativeDefinition()
	{
		_nativeDraft = null;
		NativeDefinitionEditor.IsVisible = false;
		NativeActions.IsEnabled = true;
		NativeDefinitionContent.IsUndoEnabled = false;
		NativeDefinitionContent.Text = null;
	}

	private void OnReviewNativeDefinition(object? sender, RoutedEventArgs e)
	{
		if (_nativeDraft is not { } draft)
		{
			return;
		}
		try
		{
			var preview = McpCatalogService.PreviewNativeDefinition(draft, NativeDefinitionContent.Text ?? "");
			ShowNativePreview(preview, $"Update {draft.Source.Name} in the native configuration.");
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
		{
			Status.Text = exception.Message;
		}
	}

	private void OnBack(object? sender, RoutedEventArgs e) => ClosePreview();

	private void ClosePreview()
	{
		_pending = null;
		_nativePending = null;
		Preview.IsVisible = false;
		Catalog.IsEnabled = true;
	}
}

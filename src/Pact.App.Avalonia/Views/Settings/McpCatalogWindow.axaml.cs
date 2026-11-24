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

	private sealed record ScopeChoice(string? Directory)
	{
		public override string ToString() => Directory ?? "Profile defaults";
	}

	private sealed record ServerChoice(string Name, string Transport, string Source, McpCatalogServer? Definition)
	{
		public override string ToString() => $"{Name} · {Transport}{(Definition is null ? " · Native" : "")}";
	}

	private readonly McpCatalogService _catalog;
	private readonly ProfileChoice[] _profiles;
	private McpCatalogSnapshot? _snapshot;
	private McpCatalogSettings? _pending;
	private IReadOnlyList<NativeMcpServer> _native = [];
	private McpCatalogServer? _editing;
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
		_profiles = profiles.Where(McpCatalogService.Supports).Select(profile => new ProfileChoice(profile)).ToArray();
		InitializeComponent();
		var scopes = new[] { new ScopeChoice(null) }.Concat(projects.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new ScopeChoice(path))).ToArray();
		_refreshing = true;
		Profiles.ItemsSource = _profiles;
		Profiles.SelectedItem = _profiles.FirstOrDefault(item => item.Profile.Id == selectedProfileId) ?? _profiles.FirstOrDefault();
		Scopes.ItemsSource = scopes;
		Scopes.SelectedItem = scopes.FirstOrDefault(scope => StringComparer.OrdinalIgnoreCase.Equals(scope.Directory, selectedProject)) ?? scopes[0];
		_refreshing = false;
		Reload();
		Opened += async (_, _) => await LoadNativeAsync();
		Closed += (_, _) => _scopeRead++;
	}

	private AgentProfileRecord? CurrentProfile => (Profiles.SelectedItem as ProfileChoice)?.Profile;
	private string? CurrentProject => (Scopes.SelectedItem as ScopeChoice)?.Directory;
	private McpCatalogServer? CurrentServer => (Servers.SelectedItem as ServerChoice)?.Definition;

	private void Reload(string? selectedId = null)
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
		RefreshServers(selectedId);
	}

	private async Task LoadNativeAsync()
	{
		var read = ++_scopeRead;
		var profile = CurrentProfile;
		var project = CurrentProject;
		_native = [];
		RefreshServers();
		if (profile is null)
		{
			return;
		}
		List<string> messages = [];
		var native = await Task.Run(() => _catalog.ReadNative(profile, project, messages.Add));
		if (read == _scopeRead)
		{
			_native = native;
			RefreshServers();
			if (messages.Count > 0 && _snapshot is not null)
			{
				Status.Text = string.Join(Environment.NewLine, messages);
			}
		}
	}

	private void RefreshServers(string? selectedId = null)
	{
		var previous = Servers.SelectedItem as ServerChoice;
		var rows = (_snapshot?.Settings.Servers ?? []).Select(server => new ServerChoice(server.Id,
			server.Url is not null ? "HTTP" : "stdio", _catalog.SettingsPath, server))
			.Concat(_native.Select(server => new ServerChoice(server.Name, server.Transport, server.SourcePath, null)))
			.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		_refreshing = true;
		Servers.ItemsSource = rows;
		var selected = selectedId is null ? rows.FirstOrDefault(row => row == previous)
			: rows.FirstOrDefault(row => row.Definition?.Id == selectedId);
		Servers.SelectedItem = selected ?? rows.FirstOrDefault();
		_refreshing = false;
		if (!DefinitionEditor.IsVisible && (selectedId is not null || previous is null || previous != (Servers.SelectedItem as ServerChoice)))
		{
			ShowServer();
		}
	}

	private void ShowServer()
	{
		SetDefinitionEditing(false);
		var row = Servers.SelectedItem as ServerChoice;
		var server = row?.Definition;
		DefinitionActions.IsVisible = server is not null;
		SelectionEditor.IsVisible = CurrentProfile is not null && server is not null;
		ServerTitle.Text = row?.Name ?? "No servers found";
		ServerDetails.Text = row is null ? "Add a server to make it available to Pact launch profiles." :
			$"{row.Transport} · {row.Source}\n" + (server is null ? "Managed by the agent's native configuration." :
				$"{server.Url ?? server.Command}\nAgent name: {server.AgentName}");
		if (server is not null && CurrentProfile is { } profile && _snapshot is { } snapshot)
		{
			var rules = snapshot.Settings.Selections.Where(rule => rule.ServerId == server.Id).ToArray();
			var enabledByDefault = rules.SingleOrDefault(rule => McpCatalogService.Matches(rule, profile, null))?.Enabled == true;
			var project = CurrentProject;
			var exception = project is not null ? rules.SingleOrDefault(rule => McpCatalogService.Matches(rule, profile, project)) : null;
			Selection.ItemsSource = project is null ? new[] { "Off", "On" } : new[] { "Inherit", "On", "Off" };
			Selection.SelectedIndex = project is null ? (enabledByDefault ? 1 : 0) : exception?.Enabled switch { true => 1, false => 2, _ => 0 };
			SelectionStatus.Text = (exception?.Enabled ?? enabledByDefault) ? "Enabled for the next launch" : "Off for the next launch";
		}
	}

	private async void OnScopeChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (!_refreshing)
		{
			Status.Text = "";
			ShowServer();
			await LoadNativeAsync();
		}
	}

	private void OnServerSelected(object? sender, SelectionChangedEventArgs e)
	{
		if (!_refreshing)
		{
			ShowServer();
		}
	}

	private void OnReviewSelection(object? sender, RoutedEventArgs e)
	{
		if (_snapshot is not { } snapshot || CurrentServer is not { } server || CurrentProfile is not { } profile)
		{
			return;
		}
		var project = CurrentProject;
		var existing = snapshot.Settings.Selections.SingleOrDefault(rule => rule.ServerId == server.Id && McpCatalogService.Matches(rule, profile, project));
		bool? enabled = project is null ? (Selection.SelectedIndex == 1 ? true : null) : Selection.SelectedIndex switch { 1 => true, 2 => false, _ => null };
		if (existing?.Enabled == enabled)
		{
			Status.Text = "The availability choice is already saved.";
			return;
		}
		var selections = snapshot.Settings.Selections.Where(rule => rule != existing).ToList();
		if (enabled is { } value)
		{
			selections.Add(existing is null ? new(profile.Id, profile.Kind, project, server.Id, value) : existing with { Enabled = value });
		}
		var scopeLabel = project ?? "Profile defaults";
		ShowPreview(snapshot.Settings with { Selections = selections.ToArray() }, $"{server.Id} · {Selection.SelectedItem}\n{scopeLabel}",
			[$"{profile.DisplayName} ({profile.Kind})"]);
	}

	private void OnAddServer(object? sender, RoutedEventArgs e) => EditServer(null);
	private void OnEditServer(object? sender, RoutedEventArgs e) => EditServer(CurrentServer);
	private void OnCancelDefinition(object? sender, RoutedEventArgs e) => ShowServer();

	private void EditServer(McpCatalogServer? server)
	{
		_editing = server;
		ServerTitle.Text = server is null ? "Add server" : $"Edit {server.Id}";
		ServerDetails.Text = "";
		SelectionEditor.IsVisible = false;
		DefinitionActions.IsVisible = false;
		SetDefinitionEditing(true);
		ServerId.Text = server?.Id ?? "";
		ServerId.IsReadOnly = server is not null;
		Transport.SelectedIndex = server?.Command is not null ? 1 : 0;
		ServerUrl.Text = server?.Url;
		TokenVariable.Text = server?.BearerTokenVariable;
		Command.Text = server?.Command;
		Arguments.Text = server is null ? "" : JsonSerializer.Serialize(server.Arguments);
		EnvironmentNames.Text = server is null ? "" : string.Join(Environment.NewLine, server.EnvironmentVariables);
	}

	private void SetDefinitionEditing(bool editing)
	{
		DefinitionEditor.IsVisible = editing;
		Profiles.IsEnabled = Scopes.IsEnabled = Servers.IsEnabled = !editing;
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
			ShowPreview(snapshot.Settings with { Servers = [.. otherServers, server with { Extra = _editing?.Extra }] },
				$"{(_editing is null ? "Add" : "Update")} {id}", AffectedProfiles(snapshot, id));
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
				Selections = snapshot.Settings.Selections.Where(rule => rule.ServerId != server.Id).ToArray()
			}, $"Remove {server.Id} and its saved availability choices", AffectedProfiles(snapshot, server.Id));
		}
	}

	private string[] AffectedProfiles(McpCatalogSnapshot snapshot, string serverId) => _profiles
		.Where(item => snapshot.Settings.Selections.Any(rule => rule.ServerId == serverId && rule.Enabled &&
			rule.ProfileId == item.Profile.Id && rule.Kind == item.Profile.Kind))
		.Select(item => item.ToString()).ToArray();

	private void ShowPreview(McpCatalogSettings pending, string summary, string[] affectedProfiles)
	{
		_pending = pending;
		PreviewText.Text = $"{summary}\n\nProfiles: {(affectedProfiles.Length == 0 ? "None" : string.Join(", ", affectedProfiles))}\nFile: {_catalog.SettingsPath}";
		Status.Text = "";
		Preview.IsVisible = true;
		Catalog.IsEnabled = false;
		Apply.Focus();
	}

	private async void OnApply(object? sender, RoutedEventArgs e)
	{
		if (_snapshot is not { } snapshot || _pending is not { } pending)
		{
			return;
		}
		Preview.IsEnabled = false;
		try
		{
			var id = DefinitionEditor.IsVisible ? ServerId.Text?.Trim() : CurrentServer?.Id;
			var availability = Selection.SelectedItem;
			if (!await _catalog.SaveAsync(snapshot, pending, CancellationToken.None))
			{
				Reload(id);
				if (id is not null && !DefinitionEditor.IsVisible && CurrentServer?.Id == id)
				{
					Selection.SelectedItem = availability;
				}
				if (_snapshot is not null)
				{
					Status.Text = "The MCP catalog changed on disk. Review the change again before applying.";
				}
				ClosePreview();
				return;
			}
			SetDefinitionEditing(false);
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

	private void OnBack(object? sender, RoutedEventArgs e) => ClosePreview();

	private void ClosePreview()
	{
		_pending = null;
		Preview.IsVisible = false;
		Catalog.IsEnabled = true;
	}
}

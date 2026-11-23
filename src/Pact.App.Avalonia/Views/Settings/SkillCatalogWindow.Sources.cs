using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pact.Infrastructure.Skills;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed partial class SkillCatalogWindow
{
	private IReadOnlyList<SkillDuplicateGroup> _duplicateGroups = [];

	private async void OnFindDuplicates(object? sender, RoutedEventArgs e)
	{
		await RunAsync(async () =>
		{
			ClearPreview();
			var installations = await RefreshNativeRowsAsync();
			RefreshList();
			await RefreshDuplicateReportAsync(installations);
			Browser.IsVisible = false;
			DuplicatePanel.IsVisible = true;
			FindDuplicates.IsEnabled = false;
			SourceEditor.IsEnabled = false;
		});
	}

	private async Task RefreshDuplicateReportAsync(IReadOnlyList<NativeSkillInstallation> installations)
	{
		_duplicateGroups = await Task.Run(() => _catalog.AnalyzeDuplicates(installations, _bindings), _lifetime.Token);
		var independent = _duplicateGroups.OrderBy(group => group.Kind).ToArray();
		DuplicateReport.Text = independent.Length == 0 ? "No independent duplicate packages found." : null;
		DuplicateReport.IsVisible = independent.Length == 0;
		RenderDuplicateLocations(DuplicateLocations, independent);
	}

	private void RenderDuplicateLocations(StackPanel panel, IEnumerable<SkillDuplicateGroup> groups)
	{
		panel.Children.Clear();
		foreach (var group in groups)
		{
			var block = new StackPanel { Spacing = 6 };
			block.Children.Add(new SelectableTextBlock { Text = $"{group.Name} · {ComparisonLabel(group.Kind)}", FontWeight = FontWeight.SemiBold });
			foreach (var item in group.Installations)
			{
				var directory = Path.GetDirectoryName(item.ResolvedManifest);
				var connection = item.Connections.FirstOrDefault(c => string.Equals(c.ManifestPath, item.ResolvedManifest, StringComparison.OrdinalIgnoreCase));
				var owner = connection?.IsSharedUserDirectory == true ? "Shared" : string.Join(", ", item.Connections.SelectMany(c => c.Agents).Distinct());
				var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
				line.Children.Add(new SelectableTextBlock { Text = $"{owner} · {directory}", TextWrapping = TextWrapping.Wrap });
				if (connection is not null && _catalog.CanOfferMaintenance(item, connection, makeShared: true))
				{
					AddMaintenanceButton(line, 1, item, connection, delete: false);
				}
				if (connection is not null && item.Connections.Count == 1 && _catalog.CanOfferMaintenance(item, connection, makeShared: false))
				{
					AddMaintenanceButton(line, 2, item, connection, delete: true, group);
				}
				block.Children.Add(line);
			}
			if (group.DifferingFiles.Count != 0)
			{
				block.Children.Add(new SelectableTextBlock { Text = "Files to compare: " + string.Join(", ", group.DifferingFiles), TextWrapping = TextWrapping.Wrap });
			}
			foreach (var recommendation in group.Recommendations)
			{
				block.Children.Add(new SelectableTextBlock { Text = recommendation, TextWrapping = TextWrapping.Wrap });
			}
			panel.Children.Add(block);
		}
	}

	private void AddMaintenanceButton(Grid line, int column, NativeSkillInstallation installation, NativeSkillConnection connection, bool delete,
		SkillDuplicateGroup? comparison = null)
	{
		var label = delete ? "Delete" : "Make shared";
		var button = new Button { Content = label + "…", VerticalAlignment = VerticalAlignment.Top };
		AutomationProperties.SetName(button, $"{label}: {Path.GetDirectoryName(connection.ManifestPath)}");
		button.Click += async (_, _) => await RunAsync(() => PreviewMaintenanceAsync(installation, connection, delete, comparison));
		Grid.SetColumn(button, column);
		line.Children.Add(button);
	}

	private void RefreshMaintenanceLocations()
	{
		var installation = SelectedRow?.Installation;
		var connections = installation?.Connections;
		MaintenanceLocations.ItemsSource = connections?.Select(connection => Path.GetDirectoryName(connection.ManifestPath)).ToArray();
		MaintenanceLocations.SelectedIndex = connections?.Count == 1 ? 0 : -1;
		RefreshMaintenanceButtons();
	}

	private void OnMaintenanceLocationChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_refreshingSkills)
		{
			return;
		}
		ClearPreview();
		RefreshMaintenanceButtons();
	}

	private NativeSkillConnection? SelectedConnection => SelectedRow?.Installation?.Connections
		.FirstOrDefault(connection => string.Equals(Path.GetDirectoryName(connection.ManifestPath),
			MaintenanceLocations.SelectedItem as string, StringComparison.OrdinalIgnoreCase));

	private void RefreshMaintenanceButtons()
	{
		_renameLocation = null;
		var installation = SelectedRow?.Installation;
		if (installation is null)
		{
			MaintenanceLocations.IsVisible = MakeShared.IsVisible = RenameFolder.IsVisible = DeletePackage.IsVisible = false;
			return;
		}
		var selected = SelectedConnection;
		var locations = installation.Connections;
		MaintenanceLocations.IsVisible = locations.Count > 1 && locations.Any(c => _catalog.CanOfferMaintenance(installation, c, makeShared: false)
			|| _catalog.CanOfferMaintenance(installation, c, makeShared: true) || _catalog.CanOfferRename(installation, c));
		var candidates = selected is null ? locations : [selected];
		DeletePackage.IsVisible = candidates.Any(c => _catalog.CanOfferMaintenance(installation, c, makeShared: false));
		DeletePackage.IsEnabled = selected is not null;
		MakeShared.IsVisible = candidates.Any(c => _catalog.CanOfferMaintenance(installation, c, makeShared: true));
		MakeShared.IsEnabled = selected is not null;
		var renameLocations = candidates.Where(c => _catalog.CanOfferRename(installation, c)).Take(2).ToArray();
		_renameLocation = renameLocations.Length == 1 ? renameLocations[0] : null;
		RenameFolder.IsVisible = renameLocations.Length > 0;
		RenameFolder.IsEnabled = _renameLocation is not null;
	}

	private NativeSkillConnection? _renameLocation;

	private void OnRenameFolder(object? sender, RoutedEventArgs e)
	{
		if (_renameLocation is not { } connection)
		{
			return;
		}
		if (SelectedConnection is null)
		{
			MaintenanceLocations.SelectedItem = Path.GetDirectoryName(connection.ManifestPath);
		}
		ClearPreview();
		BindingEditor.IsVisible = false;
		RenameInput.Text = Path.GetFileName(Path.GetDirectoryName(connection.ManifestPath));
		RenameEditor.IsVisible = true;
		RenameInput.Focus();
		RenameInput.SelectAll();
	}

	private void OnRenameInputChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == TextBox.TextProperty && RenameEditor.IsVisible)
		{
			ClearPreview(keepRenameEditor: true);
		}
	}

	private async void OnReviewRename(object? sender, RoutedEventArgs e)
	{
		if (!RenameEditor.IsVisible || SelectedRow?.Installation is not { } installation || SelectedConnection is not { } connection)
		{
			return;
		}
		var name = RenameInput.Text ?? "";
		await RunAsync(async () =>
		{
			ClearPreview(keepRenameEditor: true);
			var preview = await Task.Run(() => _catalog.PreviewRename(installation, connection, name, _profiles, _projects), _lifetime.Token);
			if (!RenameEditor.IsVisible || !string.Equals(name, RenameInput.Text, StringComparison.Ordinal) || SelectedConnection != connection)
			{
				return;
			}
			var paths = $"Current folder: {preview.SourceDirectory}\nNew folder: {preview.DestinationDirectory}";
			if (preview.Conflict is not null)
			{
				Status.Text = $"{preview.Conflict}\n{paths}";
				return;
			}
			Status.Text = string.Join(Environment.NewLine, new[] { paths }.Concat(preview.Changes)
				.Concat(MaintenanceImpact(preview.AffectedProfiles, preview.AffectedFiles)));
			_pending = async () =>
			{
				await Task.Run(() => _catalog.Apply(preview), _lifetime.Token);
				_selectManifestAfterApply = Path.Combine(preview.DestinationDirectory, "SKILL.md");
				return preview.CleanupWarning;
			};
			Apply.Content = "Rename folder";
			Apply.IsVisible = true;
			CancelRename.IsVisible = false;
			CancelPreview.IsVisible = true;
		});
	}

	private async void OnDeletePackage(object? sender, RoutedEventArgs e) => await PreviewSelectedMaintenanceAsync(delete: true);

	private async void OnMakeShared(object? sender, RoutedEventArgs e) => await PreviewSelectedMaintenanceAsync(delete: false);

	private async Task PreviewSelectedMaintenanceAsync(bool delete)
	{
		if (SelectedRow?.Installation is { } installation && SelectedConnection is { } connection)
		{
			await RunAsync(() => PreviewMaintenanceAsync(installation, connection, delete));
		}
	}

	private async Task PreviewMaintenanceAsync(NativeSkillInstallation installation, NativeSkillConnection connection, bool delete,
		SkillDuplicateGroup? comparison = null)
	{
		ClearPreview();
		var preview = await Task.Run(() => comparison is not null ? _catalog.PreviewDelete(installation, connection, comparison)
			: delete ? _catalog.PreviewDelete(installation, connection) : _catalog.PreviewMakeShared(installation, connection, _profiles), _lifetime.Token);
		if (preview.Conflict is not null)
		{
			Status.Text = $"{preview.Conflict}\nSelected directory: {preview.SourceDirectory}";
			return;
		}
		var target = Path.GetDirectoryName(installation.ResolvedManifest);
		var heading = delete ? (preview.IsLink ? "Delete directory link" : "Delete package directory and all its files") : "Make package shared";
		var lines = new List<string> { $"{heading}: {preview.SourceDirectory}" };
		if (preview.IsLink)
		{
			lines.Add($"Link target: {target}");
			if (delete)
			{
				lines.Add("The target directory and its files will remain.");
			}
		}
		if (preview.SharedDirectory is { } shared)
		{
			lines.Add($"Shared directory: {shared}");
		}
		if (delete && !preview.IsLink)
		{
			foreach (var other in installation.Connections.Where(c => !string.Equals(c.ManifestPath, connection.ManifestPath, StringComparison.OrdinalIgnoreCase)))
			{
				lines.Add($"This location uses the same files: {Path.GetDirectoryName(other.ManifestPath)}");
			}
		}
		lines.AddRange(preview.Changes);
		if (!delete)
		{
			lines.AddRange(MaintenanceImpact(preview.AffectedProfiles, preview.AffectedFiles));
		}
		Status.Text = string.Join(Environment.NewLine, lines);
		_pending = async () =>
		{
			await Task.Run(() => _catalog.Apply(preview), _lifetime.Token);
			return preview.CleanupWarning;
		};
		Apply.Content = delete ? (preview.IsLink ? "Delete link" : "Delete directory") : "Make shared";
		Apply.IsVisible = true;
		CancelPreview.IsVisible = true;
	}

	private static IEnumerable<string> MaintenanceImpact(IReadOnlyList<string> profiles, IReadOnlyList<string> files) =>
		new[] { "Affected profiles: " + (profiles.Count > 0 ? string.Join(", ", profiles) : "none configured"), "Affected paths:" }.Concat(files);

	private void OnCloseDuplicates(object? sender, RoutedEventArgs e)
	{
		DuplicatePanel.IsVisible = false;
		Browser.IsVisible = true;
		FindDuplicates.IsEnabled = true;
		SourceEditor.IsEnabled = _settings is not null;
		RefreshSelection();
	}

	private static string ComparisonLabel(SkillDuplicateKind kind) => kind switch
	{
		SkillDuplicateKind.Identical => "Identical independent copies",
		SkillDuplicateKind.Different => "Same name, different contents",
		SkillDuplicateKind.Incomplete => "Incomplete comparison",
		_ => throw new ArgumentOutOfRangeException(nameof(kind))
	};

	private string? DuplicateHint(NativeSkillInstallation? installation)
	{
		if (installation is null)
		{
			return null;
		}
		var comparison = _duplicateGroups.FirstOrDefault(group =>
			group.Installations.Any(item => string.Equals(item.ResolvedManifest, installation.ResolvedManifest, StringComparison.OrdinalIgnoreCase)));
		return comparison is not null ? ComparisonLabel(comparison.Kind) :
			installation.Connections.Count > 1 ? "One package with multiple links" : null;
	}

	private void OnLinkSource(object? sender, RoutedEventArgs e)
	{
		if (Selected is not { } installed || !IsInstallDestination(installed) || _bindings.ContainsKey(installed.FolderName))
		{
			return;
		}
		ClearPreview();
		BindingSource.PropertyChanged -= OnBindingSourceChanged;
		BindingSource.Text = "";
		BindingSource.PropertyChanged += OnBindingSourceChanged;
		BindingCandidates.IsVisible = false;
		BindingCandidates.ItemsSource = null;
		_bindingPackages = [];
		BindingEditor.IsVisible = true;
		BindingSource.Focus();
	}

	private void OnBindingSourceChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == TextBox.TextProperty && BindingEditor.IsVisible)
		{
			ClearPreview();
			_bindingPackages = [];
			BindingCandidates.IsVisible = false;
			BindingCandidates.ItemsSource = null;
		}
	}

	private async void OnBrowseBinding(object? sender, RoutedEventArgs e)
	{
		await RunAsync(async () =>
		{
			var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Select update repository" });
			if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
			{
				BindingSource.Text = path;
			}
		});
	}

	private async void OnReadBinding(object? sender, RoutedEventArgs e)
	{
		await RunAsync(async () =>
		{
			ClearPreview();
			_bindingPackages = [];
			BindingCandidates.ItemsSource = null;
			BindingCandidates.IsVisible = false;
			if (!BindingEditor.IsVisible || Selected is not { } installed || !IsInstallDestination(installed))
			{
				return;
			}
			var source = BindingSource.Text?.Trim();
			if (string.IsNullOrWhiteSpace(source))
			{
				Status.Text = "Choose a repository folder or enter a public GitHub URL.";
				return;
			}
			var packages = await _catalog.BrowseAsync(source, _lifetime.Token);
			_bindingPackages = packages.Where(candidate => candidate.InstalledFilePath is null &&
				string.Equals(candidate.Name, installed.Name, StringComparison.OrdinalIgnoreCase))
				.OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal).ToArray();
			if (_bindingPackages.Length == 0)
			{
				Status.Text = $"No package named {installed.Name} was found in {source}.";
			}
			else if (_bindingPackages.Length == 1)
			{
				await PreviewBindingAsync(_bindingPackages[0], installed);
			}
			else
			{
				BindingCandidates.ItemsSource = _bindingPackages.Select(candidate => $"{candidate.Name} · {candidate.RelativePath}").ToArray();
				BindingCandidates.SelectedIndex = -1;
				BindingCandidates.IsVisible = true;
				Status.Text = "Choose the package to compare with this installation.";
			}
		});
	}

	private async void OnBindingChosen(object? sender, SelectionChangedEventArgs e)
	{
		if (BindingEditor.IsVisible && BindingCandidates.IsVisible && Selected is { } installed &&
			BindingCandidates.SelectedIndex is var index && index >= 0 && index < _bindingPackages.Length)
		{
			await RunAsync(() => PreviewBindingAsync(_bindingPackages[index], installed));
		}
	}

	private async Task PreviewBindingAsync(CatalogSkill candidate, CatalogSkill installed)
	{
		ClearPreview();
		if (!File.Exists(installed.InstalledFilePath))
		{
			Status.Text = $"The installed manifest is missing at {installed.InstalledFilePath}.";
			return;
		}
		var preview = await Task.Run(() => _catalog.PreviewBinding(candidate, installed), _lifetime.Token);
		await ShowInstallAsync(preview);
	}
}

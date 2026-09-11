using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Core.Updates;

namespace Pact.App.Avalonia.Views.Dialogs;

internal enum UpdateAvailableDialogResult
{
	Download,
	Later,
	OpenReleaseNotes
}

/// <summary>Offers the three discovery-phase actions for one validated release.</summary>
internal sealed partial class UpdateAvailableDialogWindow : Window
{
	public UpdateAvailableDialogWindow()
		: this(new UpdateRelease(
			new StableReleaseVersion(0, 0, 0),
			"v0.0.0",
			new Uri("https://github.com/s-titov-82/pact-mission-control/releases"),
			new UpdateAsset("setup.exe", new Uri("https://github.com/setup.exe"), 0),
			new UpdateAsset("SHA256SUMS.txt", new Uri("https://github.com/SHA256SUMS.txt"), 0)))
	{
	}

	public UpdateAvailableDialogWindow(UpdateRelease release)
	{
		ArgumentNullException.ThrowIfNull(release);
		InitializeComponent();
		ReleaseText.Text = $"Pact {release.Version} is available. Download it now, decide later, or open the release notes.";
	}

	public static async Task<UpdateAvailableDialogResult> ShowOwnedAsync(
		Window owner,
		UpdateRelease release)
	{
		ArgumentNullException.ThrowIfNull(owner);
		UpdateAvailableDialogWindow window = new(release);
		return await window.ShowDialog<UpdateAvailableDialogResult?>(owner)
			?? UpdateAvailableDialogResult.Later;
	}

	private void OnDownloadClicked(object? sender, RoutedEventArgs e) =>
		Close(UpdateAvailableDialogResult.Download);

	private void OnLaterClicked(object? sender, RoutedEventArgs e) =>
		Close(UpdateAvailableDialogResult.Later);

	private void OnReleaseNotesClicked(object? sender, RoutedEventArgs e) =>
		Close(UpdateAvailableDialogResult.OpenReleaseNotes);
}

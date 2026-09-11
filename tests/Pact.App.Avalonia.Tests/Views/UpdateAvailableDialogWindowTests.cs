using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Updates;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class UpdateAvailableDialogWindowTests
{
	[AvaloniaTest]
	public void Dialog_has_dedicated_download_later_and_release_notes_actions()
	{
		UpdateRelease release = new(
			new StableReleaseVersion(1, 3, 0),
			"v1.3.0",
			new Uri("https://github.com/s-titov-82/pact-mission-control/releases/tag/v1.3.0"),
			new UpdateAsset("setup.exe", new Uri("https://github.com/setup.exe"), 42),
			new UpdateAsset("SHA256SUMS.txt", new Uri("https://github.com/SHA256SUMS.txt"), 65));
		UpdateAvailableDialogWindow window = new(release);

		window.FindControl<TextBlock>("ReleaseText")!.Text.ShouldNotBeNull()
			.ShouldContain("1.3.0");
		window.FindControl<Button>("DownloadButton")!.Content.ShouldBe("Download");
		window.FindControl<Button>("LaterButton")!.Content.ShouldBe("Later");
		window.FindControl<Button>("ReleaseNotesButton")!.Content.ShouldBe("Open release notes");
	}
}

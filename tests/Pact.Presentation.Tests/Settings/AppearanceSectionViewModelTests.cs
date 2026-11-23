using System.Text.Json.Nodes;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class AppearanceSectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _directory => _temporaryDirectory.Path;
	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	[TestCase(null)]
	[TestCase("not-json")]
	[TestCase(/*lang=json,strict*/ "{\"theme\":\"unknown\"}")]
	public async Task Load_falls_back_to_system_for_missing_or_invalid_settings(string? json)
	{
		var path = Path.Combine(_directory, "appearance.json");
		if (json is not null)
		{
			await File.WriteAllTextAsync(path, json);
		}

		AppearanceSettingsStore store = new(path);

		(await store.LoadAsync(CancellationToken.None)).ShouldBe(AppearanceMode.System);
	}

	[Test]
	public async Task Save_round_trips_selected_mode()
	{
		AppearanceSettingsStore store = new(Path.Combine(_directory, "appearance.json"));

		await store.SaveAsync(AppearanceMode.Dark, CancellationToken.None);

		(await store.LoadAsync(CancellationToken.None)).ShouldBe(AppearanceMode.Dark);
	}

	[Test]
	public async Task Preferences_default_details_to_visible_and_preserve_unknown_fields_on_save()
	{
		var path = Path.Combine(_directory, "appearance.json");
		await File.WriteAllTextAsync(
			path,
			/*lang=json,strict*/ "{\"theme\":\"dark\",\"future\":{\"value\":7}}");
		AppearanceSettingsStore store = new(path);

		var loaded = await store.LoadPreferencesAsync(CancellationToken.None);
		loaded.ShouldBe(new AppearancePreferences(
			AppearanceMode.Dark,
			ShowSelectedTabDetails: true,
			ShowExternalProcessMetrics: false));

		await store.SaveAsync(
			new AppearancePreferences(
				AppearanceMode.Light,
				ShowSelectedTabDetails: false,
				ShowExternalProcessMetrics: true),
			CancellationToken.None);

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(path)).ShouldBeOfType<JsonObject>();
		saved["theme"]!.GetValue<string>().ShouldBe("light");
		saved["showSelectedTabDetails"]!.GetValue<bool>().ShouldBeFalse();
		saved["showExternalProcessMetrics"]!.GetValue<bool>().ShouldBeTrue();
		saved["future"]!["value"]!.GetValue<int>().ShouldBe(7);
	}

	[Test]
	public async Task Invalid_optional_fields_do_not_reset_other_preferences_or_overwrite_terminal_json()
	{
		var path = Path.Combine(_directory, "appearance.json");
		await File.WriteAllTextAsync(path, """
			{"theme":"dark","showNotifications":true,"showSelectedTabDetails":false,
			 "notificationMode":"future-mode","notificationSeconds":"wrong-type",
			 "terminal":{"fontSize":"future-value","extra":42}}
			""");
		AppearanceSettingsStore store = new(path);

		var loaded = await store.LoadPreferencesAsync(CancellationToken.None);
		loaded.Theme.ShouldBe(AppearanceMode.Dark);
		loaded.ShowNotifications.ShouldBeTrue();
		loaded.ShowSelectedTabDetails.ShouldBeFalse();
		loaded.NotificationMode.ShouldBe(NotificationBannerMode.WindowsDefault);
		loaded.NotificationSeconds.ShouldBe(10);
		loaded.Terminal.ShouldBeNull();

		await store.SaveAsync(loaded with { Theme = AppearanceMode.Light, NotificationSeconds = 0 }, CancellationToken.None);
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(path)).ShouldBeOfType<JsonObject>();
		saved["terminal"]!["fontSize"]!.GetValue<string>().ShouldBe("future-value");
		saved["terminal"]!["extra"]!.GetValue<int>().ShouldBe(42);
		saved["notificationSeconds"]!.GetValue<int>().ShouldBe(1);
	}

	[Test]
	public async Task Notification_options_default_to_off_and_round_trip_through_the_editor()
	{
		AppearanceSettingsStore store = new(Path.Combine(_directory, "appearance.json"));
		AppearanceSectionViewModel section = new(store, _ => { });
		await section.LoadAsync(CancellationToken.None);
		section.ShowNotifications.ShouldBeFalse();
		section.NotificationModeIndex.ShouldBe((int)NotificationBannerMode.WindowsDefault);
		section.IsCustomNotificationTimeout.ShouldBeFalse();
		section.ShowNotifications = true;
		section.NotificationSoundIndex = (int)NotificationSoundMode.Station;
		section.NotificationSeconds = 17;
		section.NotificationModeIndex = (int)NotificationBannerMode.CustomTimeout;
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		AppearanceSectionViewModel reopened = new(store, _ => { });
		await reopened.LoadAsync(CancellationToken.None);
		reopened.ShowNotifications.ShouldBeTrue();
		reopened.NotificationSoundIndex.ShouldBe((int)NotificationSoundMode.Station);
		reopened.NotificationSeconds.ShouldBe(17);
		reopened.IsCustomNotificationTimeout.ShouldBeTrue();
	}

	[Test]
	[TestCase("true", NotificationSoundMode.Windows)]
	[TestCase("false", NotificationSoundMode.Silent)]
	public async Task Existing_sound_checkbox_preserves_its_effect(string enabled, NotificationSoundMode expected)
	{
		var path = Path.Combine(_directory, "appearance.json");
		await File.WriteAllTextAsync(path, $"{{\"notificationSound\":{enabled}}}");
		var preferences = await new AppearanceSettingsStore(path).LoadPreferencesAsync(CancellationToken.None);
		preferences.NotificationSoundMode.ShouldBe(expected);
	}

	[Test]
	public async Task Imported_profile_inherits_defaults_and_remains_editable_after_restart()
	{
		AppearanceSettingsStore store = new(Path.Combine(_directory, "appearance.json"));
		AppearanceSectionViewModel editor = new(store, _ => { });
		await editor.LoadAsync(CancellationToken.None);
		editor.LoadProfiles("""
			{
			  // Windows Terminal accepts comments and trailing commas.
			  "profiles": { "defaults": { "font": { "face": "Consolas", "size": 11 } },
			    "list": [{"name":"Shell","colorScheme":"missing"}, {"name":"PowerShell","font":{"size":12},"background":"#123",}] },
			  "schemes": []
			}
			""");
		editor.ProfileIndex.ShouldBe(-1);
		editor.IsDirty.ShouldBeFalse();
		editor.SelectProfile(0, systemDark: true);
		editor.StatusText.ShouldNotBeNull().ShouldContain("missing");
		editor.IsDirty.ShouldBeFalse();
		editor.SelectProfile(1, systemDark: true);
		editor.StatusText.ShouldBeNull();
		editor.IsDirty.ShouldBeTrue();
		editor.FontFamily.ShouldBe("Consolas");
		editor.FontSize.ShouldBe(16);
		editor.PreviewBackground.ShouldBe("#112233");
		editor.Palette.Single(c => c.Name == "magenta").Value.ShouldBe("#881798");
		editor.Palette.Single(c => c.Name == "green").Value = "#123456";
		(await editor.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		AppearanceSectionViewModel reopened = new(store, _ => { });
		await reopened.LoadAsync(CancellationToken.None);
		reopened.PreviewGreen.ShouldBe("#123456");
		reopened.FontSize.ShouldBe(16);
	}

	[Test]
	public async Task Section_marks_dirty_saves_applies_and_reloads()
	{
		AppearanceSettingsStore store = new(Path.Combine(_directory, "appearance.json"));
		AppearancePreferences? applied = null;
		AppearanceSectionViewModel section = new(store, preferences => applied = preferences);
		await section.LoadAsync(CancellationToken.None);

		section.SelectedMode = AppearanceMode.Dark;
		section.ShowSelectedTabDetails = false;
		section.ShowExternalProcessMetrics = true;
		section.IsDirty.ShouldBeTrue();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();
		applied.ShouldBe(new AppearancePreferences(
			AppearanceMode.Dark,
			ShowSelectedTabDetails: false,
			ShowExternalProcessMetrics: true));

		section.SelectedMode = AppearanceMode.Light;
		section.ShowSelectedTabDetails = true;
		section.ShowExternalProcessMetrics = false;
		await section.ReloadAsync(CancellationToken.None);
		section.SelectedMode.ShouldBe(AppearanceMode.Dark);
		section.ShowSelectedTabDetails.ShouldBeFalse();
		section.ShowExternalProcessMetrics.ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();
	}
}

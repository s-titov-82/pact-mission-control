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

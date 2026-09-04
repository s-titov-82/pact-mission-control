using Pact.Presentation.Settings;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public class SettingsSectionViewModelBaseTests
{
	private sealed class TestSection() : SettingsSectionViewModelBase(
		SettingsSection.LaunchProfiles, "Launch profiles", "desc", "shell-profiles.json", @"C:\x\shell-profiles.json")
	{
		public override Task LoadAsync(CancellationToken ct) { ClearDirty(); return Task.CompletedTask; }
		public override Task<bool> SaveAsync(CancellationToken ct) { ClearDirty(); return Task.FromResult(true); }
		public void Touch() => MarkDirty();
	}

	[Test]
	public void MarkDirty_sets_IsDirty_and_raises_PropertyChanged()
	{
		TestSection section = new();
		List<string?> raised = [];
		section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
		section.Touch();
		section.IsDirty.ShouldBeTrue();
		raised.ShouldContain(nameof(SettingsSectionViewModelBase.IsDirty));
	}

	[Test]
	public async Task Reload_defaults_to_Load_and_clears_dirty()
	{
		TestSection section = new();
		section.Touch();
		await section.ReloadAsync(CancellationToken.None);
		section.IsDirty.ShouldBeFalse();
	}
}
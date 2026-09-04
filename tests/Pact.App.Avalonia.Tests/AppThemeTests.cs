namespace Pact.App.Avalonia.Tests;

public sealed class AppThemeTests
{
	[Test]
	[TestCase(AppearanceMode.System, "Default")]
	[TestCase(AppearanceMode.Light, "Light")]
	[TestCase(AppearanceMode.Dark, "Dark")]
	public void Appearance_mode_maps_to_Avalonia_theme(AppearanceMode mode, string expectedKey)
	{
		var result = App.ToThemeVariant(mode);

		result.Key.ShouldBe(expectedKey);
	}
}
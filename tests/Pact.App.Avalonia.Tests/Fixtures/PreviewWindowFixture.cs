using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(Pact.App.Avalonia.Tests.Fixtures.PreviewWindowFixture))]

namespace Pact.App.Avalonia.Tests.Fixtures;

public sealed class PreviewWindowFixture
{
	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<Application>()
			.UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
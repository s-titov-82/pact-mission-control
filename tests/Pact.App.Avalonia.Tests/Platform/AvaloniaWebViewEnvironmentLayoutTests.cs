using Pact.App.Avalonia.Platform;

namespace Pact.App.Avalonia.Tests.Platform;

public sealed class AvaloniaWebViewEnvironmentLayoutTests
{
	[Test]
	public void Both_hosts_share_one_udf_with_distinct_profiles()
	{
		var root = Path.Combine("C:\\preview", "webview2");

		var layout = AvaloniaWebViewEnvironmentLayout.Create(root);

		layout.TerminalUserDataFolder.ShouldBe(root);
		layout.TerminalProfileName.ShouldBe("PactTerminal");
		layout.BrowserUserDataFolder.ShouldBe(root);
		layout.BrowserProfileName.ShouldBeNull();
	}

	[Test]
	public void Browser_arguments_preserve_unrelated_switches_and_add_each_required_switch_once()
	{
		const string unrelated = "--enable-features=PactProbe";
		const string alreadyPresent = "--disable-background-timer-throttling";

		var result = AvaloniaWebViewEnvironment.MergeAdditionalBrowserArguments(
			$"{unrelated} {alreadyPresent}");
		var tokens = result.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		result.ShouldContain(unrelated);
		tokens.Count(token => token == alreadyPresent).ShouldBe(1);
		tokens.Count(token => token == "--disable-renderer-backgrounding").ShouldBe(1);
		tokens.Count(token => token == "--disable-backgrounding-occluded-windows").ShouldBe(1);
	}
}
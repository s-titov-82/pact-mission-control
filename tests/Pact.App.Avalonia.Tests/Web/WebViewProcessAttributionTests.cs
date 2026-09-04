using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class WebViewProcessAttributionTests
{
	[Test]
	public void Only_exact_page_and_shared_processes_satisfy_native_evidence()
	{
		new WebViewProcessAttribution([200], [100])
			.HasExactPageAttribution.ShouldBeTrue();
		new WebViewProcessAttribution([], [100], PageAttributionAvailable: true)
			.HasExactPageAttribution.ShouldBeFalse();
		new WebViewProcessAttribution([200], [], PageAttributionAvailable: true)
			.HasExactPageAttribution.ShouldBeFalse();
		new WebViewProcessAttribution([], [100], PageAttributionAvailable: false, 100)
			.HasExactPageAttribution.ShouldBeFalse();
	}

	[Test]
	public void Classify_separates_selected_renderers_shared_runtime_and_other_tabs()
	{
		WebViewRuntimeProcessInfo[] processes =
		[
			new(100, WebViewRuntimeProcessKind.Browser, []),
			new(101, WebViewRuntimeProcessKind.Gpu, []),
			new(200, WebViewRuntimeProcessKind.Renderer, [42]),
			new(201, WebViewRuntimeProcessKind.Renderer, [42, 77]),
			new(202, WebViewRuntimeProcessKind.Renderer, [77]),
			new(203, WebViewRuntimeProcessKind.Renderer, [])
		];

		var attribution = WebViewProcessAttributionClassifier.Classify(
			selectedFrameId: 42,
			processes);

		attribution.PageProcessIds.ShouldBe([200]);
		attribution.SharedProcessIds.ShouldBe([100, 101, 201]);
	}
}

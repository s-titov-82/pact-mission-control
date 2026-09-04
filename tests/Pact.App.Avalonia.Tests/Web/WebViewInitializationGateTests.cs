using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class WebViewInitializationGateTests
{
	private static readonly Uri Source = new("file:///terminal.html");

	[Test]
	public void NavigationAloneLeavesReadyMissing()
	{
		WebViewInitializationGate gate = new(Source);

		gate.ReportNavigationCompleted(isSuccess: true);

		gate.Completion.IsCompleted.ShouldBeFalse();
		gate.MissingSignals.ShouldBe(["javascript-ready"]);
	}

	[Test]
	public void ReadyAloneLeavesNavigationMissing()
	{
		WebViewInitializationGate gate = new(Source);

		gate.ReportJavaScriptReady();

		gate.Completion.IsCompleted.ShouldBeFalse();
		gate.MissingSignals.ShouldBe(["navigation-completed"]);
	}

	[Test]
	[TestCase(true)]
	[TestCase(false)]
	public async Task BothSignalsCompleteInEitherOrder(bool navigationFirst)
	{
		WebViewInitializationGate gate = new(Source);

		if (navigationFirst)
		{
			gate.ReportNavigationCompleted(isSuccess: true);
			gate.ReportJavaScriptReady();
		}
		else
		{
			gate.ReportJavaScriptReady();
			gate.ReportNavigationCompleted(isSuccess: true);
		}

		await gate.Completion;
		gate.MissingSignals.ShouldBeEmpty();
	}

	[Test]
	public async Task FailedNavigationReportsOnlyAvailableNativeContext()
	{
		WebViewInitializationGate gate = new(Source);

		gate.ReportNavigationCompleted(isSuccess: false);

		var exception = await Should.ThrowAsync<InvalidOperationException>(
			async () => await gate.Completion);
		exception.Message.Contains(Source.AbsoluteUri, StringComparison.Ordinal).ShouldBeTrue();
		exception.Message.Contains("IsSuccess=False", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public async Task CancelFaultsWaitersAndDuplicateSignalsAreHarmless()
	{
		WebViewInitializationGate gate = new(Source);
		OperationCanceledException cancellation = new("probe cancelled");

		gate.ReportJavaScriptReady();
		gate.ReportJavaScriptReady();
		gate.Cancel(cancellation);
		gate.ReportNavigationCompleted(isSuccess: true);

		await Should.ThrowAsync<OperationCanceledException>(
			async () => await gate.Completion);
		gate.Completion.IsFaulted.ShouldBeTrue();
		gate.Completion.Exception.InnerException.ShouldBeSameAs(cancellation);
	}
}
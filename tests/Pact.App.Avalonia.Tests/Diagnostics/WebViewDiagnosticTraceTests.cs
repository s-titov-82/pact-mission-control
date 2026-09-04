using Pact.App.Avalonia.Diagnostics;

namespace Pact.App.Avalonia.Tests.Diagnostics;

public sealed class WebViewDiagnosticTraceTests
{
	[Test]
	public void RecordPreservesSequenceAndNativeState()
	{
		WebViewDiagnosticTrace trace = new("terminal");

		trace.Record(
			"adapter-created",
			isUiThread: true,
			isVisible: true,
			isAttached: true,
			hasPlatformHandle: true,
			detail: "generation=1");
		trace.Record(
			"webmessage-received",
			isUiThread: false,
			isVisible: true,
			isAttached: true,
			hasPlatformHandle: true,
			detail: "type=ready");

		var entries = trace.Snapshot();

		entries.Length.ShouldBe(2);
		entries[0].Sequence.ShouldBe(1);
		entries[0].Host.ShouldBe("terminal");
		entries[0].Phase.ShouldBe("adapter-created");
		entries[0].IsUiThread.ShouldBeTrue();
		entries[0].IsVisible.ShouldBe(true);
		entries[0].IsAttached.ShouldBe(true);
		entries[0].HasPlatformHandle.ShouldBe(true);
		entries[0].Detail.ShouldBe("generation=1");
		entries[1].Sequence.ShouldBe(2);
		entries[1].Phase.ShouldBe("webmessage-received");
		entries[1].IsUiThread.ShouldBeFalse();
		entries[1].Detail.ShouldBe("type=ready");
	}

	[Test]
	public void SnapshotIsStableWhenLaterEventsArrive()
	{
		WebViewDiagnosticTrace trace = new("browser:test");
		trace.Record("adapter-created", true, true, true, true);

		var firstSnapshot = trace.Snapshot();
		trace.Record("adapter-destroyed", true, false, false, false);

		firstSnapshot.ShouldHaveSingleItem();
		trace.Snapshot().Length.ShouldBe(2);
	}
}
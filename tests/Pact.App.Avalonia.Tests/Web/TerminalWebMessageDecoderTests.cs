using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class TerminalWebMessageDecoderTests
{
	[Test]
	public void TryDecode_returns_typed_messages_for_every_supported_message()
	{
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"ready"}""")
			.ShouldBe(new TerminalWebMessage.Ready());
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"input","sessionId":"s1","data":"hello"}""")
			.ShouldBe(new TerminalWebMessage.Input("s1", "hello"));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"resize","sessionId":"s1","cols":120,"rows":36}""")
			.ShouldBe(new TerminalWebMessage.Resize("s1", 120, 36));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"screenSnapshot","sessionId":"s1","text":"screen","stable":false}""")
			.ShouldBe(new TerminalWebMessage.ScreenSnapshot("s1", "screen", false));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"selectionChanged","sessionId":"s1","hasSelection":true}""")
			.ShouldBe(new TerminalWebMessage.SelectionChanged("s1", true));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"selectionCompleted","sessionId":"s1","x":100.5,"y":50,"revision":4}""")
			.ShouldBe(new TerminalWebMessage.SelectionCompleted("s1", 100.5, 50, 4));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"selectionDismissed","sessionId":"s1"}""")
			.ShouldBe(new TerminalWebMessage.SelectionDismissed("s1"));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"linkRequested","sessionId":"s1","url":"https://example.test/review/42"}""")
			.ShouldBe(new TerminalWebMessage.LinkRequested(
				"s1",
				"https://example.test/review/42"));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"pasteRequested"}""")
			.ShouldBe(new TerminalWebMessage.PasteRequested());
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"busyOverlayAction"}""")
			.ShouldBe(new TerminalWebMessage.BusyOverlayAction());
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"copySelection","sessionId":"s1","data":"copied","x":100.5,"y":50,"revision":4}""")
			.ShouldBe(new TerminalWebMessage.CopySelection("s1", "copied", 100.5, 50, 4));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"copySelection","sessionId":"s1","data":"copied"}""")
			.ShouldBe(new TerminalWebMessage.CopySelection("s1", "copied", null, null, null));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"selectedTextResponse","data":"selected"}""")
			.ShouldBe(new TerminalWebMessage.SelectedTextResponse("selected"));
	}

	[Test]
	public void TryDecode_defaults_missing_snapshot_stability_to_true()
	{
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"screenSnapshot","sessionId":"s1","text":"screen"}""")
			.ShouldBe(new TerminalWebMessage.ScreenSnapshot("s1", "screen", true));
	}

	[TestCase("""{"type":"resize","sessionId":"s1","cols":0,"rows":20}""")]
	[TestCase("""{"type":"resize","sessionId":"s1","cols":80,"rows":-1}""")]
	[TestCase("""{"type":"input","data":"hello"}""")]
	[TestCase("""{"type":"selectionCompleted","x":100,"y":50,"revision":1}""")]
	[TestCase("""{"type":"selectionCompleted","sessionId":"s1","x":100,"y":50,"revision":-1}""")]
	[TestCase("""{"type":"selectionCompleted","sessionId":"s1","x":1e999,"y":50,"revision":1}""")]
	[TestCase("""{"type":"copySelection","sessionId":"s1","data":"copied","x":100,"revision":1}""")]
	[TestCase("""{"type":"copySelection","sessionId":"s1","data":42}""")]
	[TestCase("""{"type":"screenSnapshot","sessionId":"s1","text":"screen","stable":"yes"}""")]
	[TestCase("""{"type":"selectionChanged","sessionId":"s1","hasSelection":1}""")]
	[TestCase("""{"type":"selectionDismissed"}""")]
	[TestCase("""{"type":"linkRequested","url":"https://example.test"}""")]
	[TestCase("""{"type":"linkRequested","sessionId":"s1"}""")]
	[TestCase("""{"type":"unknown"}""")]
	[TestCase("""{"data":"missing type"}""")]
	[TestCase("[]")]
	[TestCase("not-json")]
	[TestCase("")]
	public void TryDecode_rejects_invalid_messages_without_throwing(string json) =>
		TerminalWebMessageDecoder.TryDecode(json).ShouldBeNull();
}

using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class ClientPasteConfirmationTests
{
	[TestCase("\u001b[38;5;6m[Pasted Content 2910 chars]\u001b[0m")]
	[TestCase("  [Pasted text #1 +39 lines]  ")]
	public void A_collapsed_paste_placeholder_confirms_the_paste(string output)
	{
		ClientPasteConfirmation confirmation = new(new string('x', 3000));

		confirmation.Observe(output).ShouldBeTrue();
	}

	[Test]
	public void The_end_of_the_text_split_across_rows_and_chunks_confirms_the_paste()
	{
		ClientPasteConfirmation confirmation = new("review the parser\nedge cases please");

		confirmation.Observe("\u001b[38;3H> review the parser\u001b[39;3Hedge cases pl").ShouldBeFalse();
		confirmation.Observe("\u001b[39;16Hease\u001b[?25h").ShouldBeTrue();
	}

	[Test]
	public void Output_without_the_paste_does_not_confirm_it()
	{
		ClientPasteConfirmation confirmation = new("review the parser edge cases");

		confirmation.Observe("\u001b[?2026h\u001b[37;3H\u001b[?25h\u001b[?2026l").ShouldBeFalse();
		confirmation.Observe("◐ medium · /effort").ShouldBeFalse();
	}

	[Test]
	public void Text_without_visible_characters_needs_no_evidence()
	{
		ClientPasteConfirmation confirmation = new(" \r\n\t");

		confirmation.IsTriviallyConfirmed.ShouldBeTrue();
	}

	[Test]
	public async Task A_runtime_wait_completes_only_from_output_after_it_started()
	{
		SessionRuntime runtime = new("session-1");
		runtime.AppendRecentOutput("[Pasted Content 1500 chars]");

		var landed = runtime.WaitForClientPasteAsync(
			new string('x', 1500),
			TimeSpan.FromMinutes(1),
			TestContext.CurrentContext.CancellationToken);
		landed.IsCompleted.ShouldBeFalse();

		runtime.AppendRecentOutput("[Pasted Content 1500 chars]");
		await landed;
		landed.IsCompletedSuccessfully.ShouldBeTrue();
	}
}

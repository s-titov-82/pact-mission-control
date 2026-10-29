using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Views.Editing;

namespace Pact.App.Avalonia.Tests.Views.Editing;

public sealed class SourceEditorSyntaxTests
{
	// Markdown pulls in dozens of embedded grammars, megabytes of JSON. Read anew for each editor they
	// fill the large object heap test after test, until a capped heap runs out.
	[AvaloniaTest]
	public void A_second_markdown_editor_reuses_the_grammars_the_first_one_read()
	{
		new SourceEditor().SetSyntaxFor("first.md");

		var before = GC.GetAllocatedBytesForCurrentThread();
		new SourceEditor().SetSyntaxFor("second.md");
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		allocated.ShouldBeLessThan(MaxSecondEditorBytes);
	}

	private const long MaxSecondEditorBytes = 8 * 1024 * 1024;
}

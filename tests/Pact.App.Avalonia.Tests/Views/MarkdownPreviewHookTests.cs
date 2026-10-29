using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Views;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class MarkdownPreviewHookTests
{
	private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

	[Test]
	public void A_missing_local_image_becomes_its_alt_text()
	{
		using var folder = TemporaryDirectory.Create();

		MarkdownPreviewHook.Prepare("before ![alt words](nope.png) after", folder.Path).ShouldBe("before *alt words* after");
	}

	[Test]
	public void Images_in_fenced_and_inline_code_stay_as_written()
	{
		using var folder = TemporaryDirectory.Create();
		const string text = "```md\n![example](missing.png)\n```\n\nUse `![x](nope.png)` like this.";

		MarkdownPreviewHook.Prepare(text, folder.Path).ShouldBe(text);
	}

	[TestCase("Use ``![x](nope.png)`` here.")]
	[TestCase("A span `that starts here\n![x](nope.png) and ends` there.")]
	[TestCase("Text.\n\n    ![x](nope.png)\n\nMore.")]
	[TestCase("``a ` b`` then `![x](nope.png)`")]
	[TestCase("```md\n    ```\n![example](missing.png)\n```")]
	public void Images_in_longer_spans_multiline_spans_and_indented_code_stay(string text)
	{
		using var folder = TemporaryDirectory.Create();

		MarkdownPreviewHook.Prepare(text, folder.Path).ShouldBe(text);
	}

	[Test]
	public void An_image_after_a_closed_code_span_is_still_replaced()
	{
		using var folder = TemporaryDirectory.Create();

		MarkdownPreviewHook.Prepare("``code`` ![x](nope.png)", folder.Path).ShouldBe("``code`` *x*");
	}

	[AvaloniaTest]
	public void An_existing_image_in_angle_brackets_stays()
	{
		using var folder = TemporaryDirectory.Create();
		File.WriteAllBytes(Path.Combine(folder.Path, "my pic.png"), Convert.FromBase64String(OnePixelPng));
		const string text = "![pic](<my pic.png>)";

		MarkdownPreviewHook.Prepare(text, folder.Path).ShouldBe(text);
	}

	[AvaloniaTest]
	public void A_local_file_that_is_not_an_image_becomes_its_alt_text()
	{
		using var folder = TemporaryDirectory.Create();
		File.WriteAllText(Path.Combine(folder.Path, "bad.png"), "not an image");

		MarkdownPreviewHook.Prepare("![broken](bad.png)", folder.Path).ShouldBe("*broken*");
	}
}

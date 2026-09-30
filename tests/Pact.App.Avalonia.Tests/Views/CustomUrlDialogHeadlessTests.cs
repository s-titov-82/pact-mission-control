using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Views.Dialogs;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class CustomUrlDialogHeadlessTests
{
	[AvaloniaTest]
	public void Invalid_address_keeps_dialog_open_with_validation_message()
	{
		CustomUrlDialog dialog = new()
		{
			UrlText = "javascript:alert(1)"
		};

		dialog.TryAccept().ShouldBeFalse();
		dialog.AcceptedUri.ShouldBeNull();
		dialog.ValidationMessage.ShouldNotBeNullOrWhiteSpace();
	}

	[AvaloniaTest]
	public void Absolute_http_address_is_trimmed_and_accepted()
	{
		CustomUrlDialog dialog = new()
		{
			UrlText = " https://example.test/exact?q=1 "
		};

		dialog.TryAccept().ShouldBeTrue();
		dialog.AcceptedUri.ShouldBe(new Uri("https://example.test/exact?q=1"));
		dialog.ValidationMessage.ShouldBeEmpty();
	}

	[AvaloniaTest]
	public void Initial_address_is_filled_in_with_caret_at_end()
	{
		CustomUrlDialog dialog = new("https://example.test/path");

		dialog.UrlText.ShouldBe("https://example.test/path");
		dialog.UrlCaretIndex.ShouldBe("https://example.test/path".Length);
	}

	[AvaloniaTest]
	public void Missing_initial_address_leaves_field_empty()
	{
		CustomUrlDialog dialog = new(null);

		dialog.UrlText.ShouldBeEmpty();
	}

	[TestCase(" https://example.test/a?q=1 \r\n", "https://example.test/a?q=1")]
	[TestCase("http://localhost:5000", "http://localhost:5000")]
	public void Clipboard_http_address_is_suggested_trimmed(string clipboard, string expected)
	{
		CustomUrlDialog.SuggestUrlFromClipboard(clipboard).ShouldBe(expected);
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("just some copied text")]
	[TestCase("javascript:alert(1)")]
	[TestCase("ftp://example.test/file")]
	[TestCase("example.test/no-scheme")]
	[TestCase("https://example.test/a\nhttps://example.test/b")]
	public void Clipboard_text_that_is_not_one_http_address_is_ignored(string? clipboard)
	{
		CustomUrlDialog.SuggestUrlFromClipboard(clipboard).ShouldBeNull();
	}
}

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
}

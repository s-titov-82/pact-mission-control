using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Pact.App.Avalonia.Views.Dialogs;

internal enum MessageDialogButtons
{
	YesNo,
	YesNoCancel
}

internal enum MessageDialogResult
{
	Yes,
	No,
	Cancel
}

internal sealed record MessageDialogRequest(
	string Title,
	string Message,
	MessageDialogButtons Buttons,
	MessageDialogResult DefaultResult);

internal sealed partial class MessageDialogWindow : Window
{
	public MessageDialogWindow()
		: this(new MessageDialogRequest(
			"Confirm", "Continue?", MessageDialogButtons.YesNo, MessageDialogResult.No))
	{
	}

	public MessageDialogWindow(MessageDialogRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		InitializeComponent();
		Title = request.Title;
		MessageText.Text = request.Message;
		CancelButton.IsVisible = request.Buttons == MessageDialogButtons.YesNoCancel;
	}

	public static async Task<MessageDialogResult> ShowOwnedAsync(
		Window owner,
		MessageDialogRequest request)
	{
		ArgumentNullException.ThrowIfNull(owner);
		MessageDialogWindow window = new(request);
		var result = await window.ShowDialog<MessageDialogResult?>(owner);
		return result ?? request.DefaultResult;
	}

	private void OnYesClicked(object? sender, RoutedEventArgs e) => Close(MessageDialogResult.Yes);

	private void OnNoClicked(object? sender, RoutedEventArgs e) => Close(MessageDialogResult.No);

	private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(MessageDialogResult.Cancel);

}
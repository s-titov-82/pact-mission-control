using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Core.Web;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class CustomUrlDialog : Window
{
	public CustomUrlDialog()
	{
		InitializeComponent();
		Opened += (_, _) => UrlTextBox.Focus();
	}

	internal string UrlText
	{
		get => UrlTextBox.Text ?? string.Empty;
		set => UrlTextBox.Text = value;
	}

	internal string ValidationMessage
	{
		get => ValidationText.Text ?? string.Empty;
		private set => ValidationText.Text = value;
	}

	internal Uri? AcceptedUri { get; private set; }

	internal bool TryAccept()
	{
		if (!HttpWebAddress.TryParse(UrlText, out var uri))
		{
			AcceptedUri = null;
			ValidationMessage = "Enter an absolute HTTP or HTTPS address.";
			return false;
		}

		AcceptedUri = uri;
		ValidationMessage = string.Empty;
		return true;
	}

	internal static async Task<Uri?> ShowOwnedAsync(Window owner)
	{
		ArgumentNullException.ThrowIfNull(owner);
		CustomUrlDialog dialog = new();
		return await dialog.ShowDialog<Uri?>(owner);
	}

	private void OnOpenClicked(object? sender, RoutedEventArgs e)
	{
		if (TryAccept())
		{
			Close(AcceptedUri);
		}
	}

	private void OnCancelClicked(object? sender, RoutedEventArgs e) => Close(null);
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.Core.Web;

namespace Pact.App.Avalonia.Views.Dialogs;

internal sealed partial class CustomUrlDialog : Window
{
	public CustomUrlDialog()
		: this(null)
	{
	}

	internal CustomUrlDialog(string? initialUrl)
	{
		InitializeComponent();
		if (!string.IsNullOrEmpty(initialUrl))
		{
			UrlText = initialUrl;
		}

		Opened += (_, _) =>
		{
			UrlTextBox.Focus();
			UrlTextBox.CaretIndex = UrlText.Length;
		};
	}

	internal string UrlText
	{
		get => UrlTextBox.Text ?? string.Empty;
		set
		{
			UrlTextBox.Text = value;
			UrlTextBox.CaretIndex = UrlText.Length;
		}
	}

	internal int UrlCaretIndex => UrlTextBox.CaretIndex;

	/// <summary>
	/// Returns the trimmed clipboard text when it is exactly one absolute HTTP(S) address,
	/// so the dialog can offer it; anything else (prose, several lines, other schemes) is ignored.
	/// </summary>
	internal static string? SuggestUrlFromClipboard(string? clipboardText)
	{
		var candidate = clipboardText?.Trim();
		if (string.IsNullOrEmpty(candidate)
			|| candidate.AsSpan().IndexOfAny('\r', '\n') >= 0
			|| !HttpWebAddress.TryParse(candidate, out _))
		{
			return null;
		}

		return candidate;
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

	internal static async Task<Uri?> ShowOwnedAsync(Window owner, string? initialUrl = null)
	{
		ArgumentNullException.ThrowIfNull(owner);
		CustomUrlDialog dialog = new(initialUrl);
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

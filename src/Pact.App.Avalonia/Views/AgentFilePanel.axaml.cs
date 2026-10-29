using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>
/// A linked file, read-only, over the right part of the center pane. It does not resize the
/// terminal beneath it.
/// </summary>
internal sealed partial class AgentFilePanel : UserControl
{
	/// <summary>Share of the pane's width the panel takes, from the right edge.</summary>
	private const double WidthShare = 0.6;

	private static readonly TimeSpan LinkNoticeTime = TimeSpan.FromSeconds(5);
	private readonly DispatcherTimer _linkNoticeTimer;
	private Control? _pane;
	private Window? _window;
	private AgentFileDocument? _document;

	public AgentFilePanel()
	{
		InitializeComponent();
		MarkdownPreviewHook.Attach(
			MarkdownView,
			() => _document is { ProjectRoot: { } root, ProjectRelativePath: { } relative } ? (root, relative) : null,
			FollowLink);
		_linkNoticeTimer = new DispatcherTimer { Interval = LinkNoticeTime };
		_linkNoticeTimer.Tick += (_, _) =>
		{
			_linkNoticeTimer.Stop();
			LinkNotice.IsVisible = false;
		};
	}

	/// <summary>Raised by the ✕ and by Esc.</summary>
	public event EventHandler? CloseRequested;

	/// <summary>Raised for a link to a Markdown file of the project, for the Docs pane to open.</summary>
	public event EventHandler<ProjectDocumentLink>? DocumentLinkFollowed;

	/// <summary>Whether the panel is shown.</summary>
	public bool IsOpen => FilePopup.IsOpen;

	/// <summary>Shows <paramref name="document"/> over the right part of <paramref name="pane"/>.</summary>
	public void Show(AgentFileDocument document, Control pane)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(pane);
		_document = document;
		Attach(pane);
		FileNameText.Text = document.FileName;
		FullPathText.Text = document.FullPath;
		LinkNotice.IsVisible = false;
		TextView.IsVisible = document.Text is not null && !document.IsMarkdown;
		MarkdownView.IsVisible = document.Text is not null && document.IsMarkdown;
		PlaceholderText.IsVisible = document.Text is null;
		PlaceholderText.Text = document.Placeholder;
		if (MarkdownView.IsVisible)
		{
			// Images come from the file's own folder, inside the project or not.
			var folder = Path.GetDirectoryName(document.FullPath);
			MarkdownPreviewHook.SetDocument(MarkdownView, folder, document.FileName);
			MarkdownView.Markdown = MarkdownPreviewHook.Prepare(document.Text!, folder);
			TextView.Text = string.Empty;
		}
		else
		{
			TextView.SetSyntaxFor(document.FullPath);
			TextView.Text = document.Text ?? string.Empty;
			TextView.ScrollToHome();
			MarkdownView.Markdown = string.Empty;
		}

		Place();
		FilePopup.IsOpen = true;
	}

	/// <summary>Closes the panel.</summary>
	public void Hide()
	{
		FilePopup.IsOpen = false;
		_document = null;
		Detach();
	}

	private void Attach(Control pane)
	{
		if (!ReferenceEquals(_pane, pane))
		{
			Detach();
			_pane = pane;
			_pane.PropertyChanged += OnPanePropertyChanged;
		}

		var window = TopLevel.GetTopLevel(pane) as Window;
		if (!ReferenceEquals(_window, window))
		{
			_window?.PositionChanged -= OnWindowPositionChanged;
			_window = window;
			_window?.PositionChanged += OnWindowPositionChanged;
		}
	}

	private void Detach()
	{
		_pane?.PropertyChanged -= OnPanePropertyChanged;
		_pane = null;
		_window?.PositionChanged -= OnWindowPositionChanged;
		_window = null;
	}

	private void OnPanePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
	{
		if (e.Property == BoundsProperty)
		{
			Place();
		}
	}

	// A popup does not follow its owner window by itself; offsets reapplied move it along.
	private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e) => Place();

	private void Place()
	{
		if (_pane is not { } pane)
		{
			return;
		}

		var size = pane.Bounds.Size;
		var width = Math.Round(size.Width * WidthShare);
		PanelBorder.Width = width;
		PanelBorder.Height = size.Height;
		FilePopup.PlacementTarget = pane;
		FilePopup.HorizontalOffset = size.Width - width;
		FilePopup.VerticalOffset = 0;
	}

	private void OnCloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

	private void OnPanelKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			CloseRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	private void FollowLink(MarkdownLinkTarget target)
	{
		switch (target)
		{
			case ProjectDocumentLink document:
				DocumentLinkFollowed?.Invoke(this, document);
				break;
			case HeadingLink heading when !MarkdownPreviewHook.ScrollToHeading(MarkdownView, heading.Heading):
				ShowLinkNotice($"No such heading: #{heading.Heading}");
				break;
			case WebLink web:
				MarkdownPreviewHook.OpenWeb(web.Uri);
				break;
			case RefusedLink refused:
				ShowLinkNotice(refused.Notice);
				break;
		}
	}

	private void ShowLinkNotice(string notice)
	{
		LinkNoticeText.Text = notice;
		LinkNotice.IsVisible = true;
		_linkNoticeTimer.Stop();
		_linkNoticeTimer.Start();
	}
}

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
	private bool _requestCloseOnPopupClosed;

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

	/// <summary>Raised by the ✕, Esc, or a click outside the panel.</summary>
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
		_requestCloseOnPopupClosed = true;
		FilePopup.IsOpen = true;
		// The click that opened the panel left focus in the right column; Esc must reach the panel.
		Dispatcher.UIThread.Post(() =>
		{
			if (IsOpen)
			{
				PanelBorder.Focus();
			}
		}, DispatcherPriority.Input);
	}

	/// <summary>Closes the panel.</summary>
	public void Hide()
	{
		_requestCloseOnPopupClosed = false;
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
			_window?.RemoveHandler(KeyDownEvent, OnPanelKeyDown);
			_window = window;
			_window?.PositionChanged += OnWindowPositionChanged;
			_window?.AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
		}
	}

	private void Detach()
	{
		_pane?.PropertyChanged -= OnPanePropertyChanged;
		_pane = null;
		_window?.PositionChanged -= OnWindowPositionChanged;
		_window?.RemoveHandler(KeyDownEvent, OnPanelKeyDown);
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
		// Size the native host, not just its child: auto-sizing can be capped by another
		// monitor's work area and center an oversized child above the pane, hiding its header.
		FilePopup.Width = width;
		FilePopup.Height = size.Height;
		FilePopup.PlacementTarget = pane;
		FilePopup.HorizontalOffset = size.Width - width;
		FilePopup.VerticalOffset = 0;
	}

	private void OnCloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

	private void OnPopupClosed(object? sender, EventArgs e)
	{
		Detach();
		if (_requestCloseOnPopupClosed)
		{
			_requestCloseOnPopupClosed = false;
			CloseRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	private void OnPanelKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && !e.Handled && IsOpen)
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

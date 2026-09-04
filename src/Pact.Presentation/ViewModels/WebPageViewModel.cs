using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// A web page tab in the project tree, carrying its load state and monitoring projection.
/// </summary>
public sealed class WebPageViewModel : INotifyPropertyChanged
{

	/// <summary>Creates a view model over a saved web page.</summary>
	/// <param name="record">Persisted browser state.</param>
	/// <param name="isRootItem">Whether the page belongs to the project-independent ROOT area.</param>
	public WebPageViewModel(WebPageRecord record, bool isRootItem = false)
	{
		Record = record;
		IsRootItem = isRootItem;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Persisted page state.</summary>
	public WebPageRecord Record { get; private set; }

	/// <summary>Whether the page belongs to the project-independent ROOT area.</summary>
	public bool IsRootItem { get; }

	/// <summary>Whether the user explicitly paused this ROOT page.</summary>
	public bool IsManuallyPaused { get; private set; }

	/// <summary>Whether the ROOT row offers its pause action.</summary>
	public bool CanPause => IsRootItem && !IsManuallyPaused;

	/// <summary>Whether the ROOT row offers its resume action.</summary>
	public bool CanResume => IsRootItem && IsManuallyPaused;

	/// <summary>Tab label, tracking the document title.</summary>
	public string Title => Record.Title;

	/// <summary>Discriminator letting the tree template this row as a web page.</summary>
	public static string PageKind => "web";

	/// <summary>Address the tab reopens at.</summary>
	public string ResumeUrl => Record.ResumeUrl;
	/// <summary>Address rendered in the compact project-tree row.</summary>
	public string DisplayAddress => WebPageAddressFormatter.Format(ResumeUrl);
	/// <summary>Full address and the localized interaction hint shown after hover delay.</summary>
	public string AddressToolTip => ResumeUrl + Environment.NewLine + "Right-click → Copy";
	/// <summary>Whether a browser host is attached and holding this page.</summary>
	public bool IsBrowserLoaded { get; private set; }

	/// <summary>Whether the page has no live host, so monitoring cannot observe it.</summary>
	public bool IsBrowserPaused => !IsBrowserLoaded;

	/// <summary>Whether this tab is the selected item.</summary>
	public bool IsCurrentBrowser { get; private set; }

	/// <summary>Whether a navigation is in progress.</summary>
	public bool IsLoading { get; private set; }

	/// <summary>Current frame of the loading spinner, advanced by the shell while loading.</summary>
	public string LoadingGlyph { get; private set; } = "⠋";

	/// <summary>Whether live monitoring currently reports activity for this page.</summary>
	public bool IsMonitorActive { get; private set; }

	/// <summary>Whether this saved page owns an unacknowledged monitoring event.</summary>
	public bool HasMonitorUnread { get; private set; }

	/// <summary>
	/// Gets the page-owned monitoring projection independently of whether a live coordinator
	/// registration exists.
	/// </summary>
	public WebMonitorStatus MonitorStatus => IsMonitorActive
		? WebMonitorStatus.Activity
		: HasMonitorUnread
			? WebMonitorStatus.Unread
			: !IsBrowserLoaded
				? WebMonitorStatus.Paused
				: WebMonitorStatus.None;

	/// <summary>Gets the latest sanitized monitoring diagnostic suitable for a tooltip.</summary>
	public string? MonitorDiagnostic { get; private set; }

	/// <summary>
	/// Gets an accessible status description and appends the latest sanitized diagnostic when one
	/// exists.
	/// </summary>
	public string MonitorToolTip
	{
		get
		{
			var status = MonitorStatus switch
			{
				WebMonitorStatus.Activity => "Monitored activity is running",
				WebMonitorStatus.Unread => "Monitored page has unseen changes",
				WebMonitorStatus.Paused => "Web page is unloaded",
				_ => "Web monitoring is idle"
			};
			return string.IsNullOrWhiteSpace(MonitorDiagnostic)
				? status
				: status + Environment.NewLine + MonitorDiagnostic;
		}
	}

	/// <summary>Whether the activity glyph may render after loading takes visual priority.</summary>
	public bool ShowMonitorActivity =>
		!IsLoading && MonitorStatus == WebMonitorStatus.Activity;

	/// <summary>Whether the unread glyph may render after loading takes visual priority.</summary>
	public bool ShowMonitorUnread =>
		!IsLoading && MonitorStatus == WebMonitorStatus.Unread;

	/// <summary>Whether the pause glyph may render after loading takes visual priority.</summary>
	public bool ShowMonitorPaused =>
		!IsLoading && MonitorStatus == WebMonitorStatus.Paused;

	/// <summary>Sets the loading flag, which suppresses every monitor glyph while set.</summary>
	public void SetLoading(bool isLoading)
	{
		if (IsLoading == isLoading)
		{
			return;
		}

		IsLoading = isLoading;
		OnPropertyChanged(nameof(IsLoading));
		NotifyMonitorGlyphVisibilityChanged();
	}

	/// <summary>Advances the spinner frame.</summary>
	public void SetLoadingGlyph(string glyph)
	{
		if (string.Equals(LoadingGlyph, glyph, StringComparison.Ordinal))
		{
			return;
		}

		LoadingGlyph = glyph;
		OnPropertyChanged(nameof(LoadingGlyph));
	}

	/// <summary>Sets whether this tab is the selected item.</summary>
	public void SetCurrentBrowser(bool isCurrentBrowser)
	{
		if (IsCurrentBrowser == isCurrentBrowser)
		{
			return;
		}

		IsCurrentBrowser = isCurrentBrowser;
		OnPropertyChanged(nameof(IsCurrentBrowser));
	}

	/// <summary>Projects the persisted per-item ROOT pause state onto the row.</summary>
	public void SetManuallyPaused(bool isManuallyPaused)
	{
		if (IsManuallyPaused == isManuallyPaused)
		{
			return;
		}

		IsManuallyPaused = isManuallyPaused;
		OnPropertyChanged(nameof(IsManuallyPaused));
		OnPropertyChanged(nameof(CanPause));
		OnPropertyChanged(nameof(CanResume));
	}

	/// <summary>
	/// Sets whether a browser host is attached. Losing the host moves the page to
	/// <see cref="WebMonitorStatus.Paused"/>, since nothing can observe it.
	/// </summary>
	public void SetBrowserLoaded(bool isBrowserLoaded)
	{
		if (IsBrowserLoaded == isBrowserLoaded)
		{
			return;
		}

		var previousStatus = MonitorStatus;
		IsBrowserLoaded = isBrowserLoaded;
		OnPropertyChanged(nameof(IsBrowserLoaded));
		OnPropertyChanged(nameof(IsBrowserPaused));
		NotifyMonitorProjectionChanged(previousStatus);
	}

	/// <summary>
	/// Applies one coordinator projection while retaining an already-recorded unread event behind
	/// higher-priority activity.
	/// </summary>
	public void SetMonitorStatus(WebMonitorStatus status)
	{
		var active = status == WebMonitorStatus.Activity;
		var unread = status switch
		{
			WebMonitorStatus.Activity => HasMonitorUnread,
			WebMonitorStatus.Unread => true,
			_ => false
		};
		SetMonitorState(active, unread);
	}

	/// <summary>
	/// Restores page-owned unread state before a browser host or state-engine registration exists.
	/// </summary>
	public void SetMonitorUnread(bool hasUnread) =>
		SetMonitorState(IsMonitorActive, hasUnread);

	/// <summary>Replaces the sanitized tooltip diagnostic for this page.</summary>
	public void SetMonitorDiagnostic(string? diagnostic)
	{
		if (string.Equals(MonitorDiagnostic, diagnostic, StringComparison.Ordinal))
		{
			return;
		}

		MonitorDiagnostic = diagnostic;
		OnPropertyChanged(nameof(MonitorDiagnostic));
		OnPropertyChanged(nameof(MonitorToolTip));
	}

	/// <summary>
	/// Replaces the persisted state and raises change notifications for the derived properties.
	/// </summary>
	public void UpdateRecord(WebPageRecord record)
	{
		Record = record;
		OnPropertyChanged(nameof(Record));
		OnPropertyChanged(nameof(Title));
		OnPropertyChanged(nameof(ResumeUrl));
		OnPropertyChanged(nameof(DisplayAddress));
		OnPropertyChanged(nameof(AddressToolTip));
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	private void SetMonitorState(bool active, bool unread)
	{
		if (IsMonitorActive == active && HasMonitorUnread == unread)
		{
			return;
		}

		var previousStatus = MonitorStatus;
		var activeChanged = IsMonitorActive != active;
		var unreadChanged = HasMonitorUnread != unread;
		IsMonitorActive = active;
		HasMonitorUnread = unread;
		if (activeChanged)
		{
			OnPropertyChanged(nameof(IsMonitorActive));
		}

		if (unreadChanged)
		{
			OnPropertyChanged(nameof(HasMonitorUnread));
		}

		NotifyMonitorProjectionChanged(previousStatus);
	}

	private void NotifyMonitorProjectionChanged(WebMonitorStatus previousStatus)
	{
		if (previousStatus != MonitorStatus)
		{
			OnPropertyChanged(nameof(MonitorStatus));
			OnPropertyChanged(nameof(MonitorToolTip));
		}

		NotifyMonitorGlyphVisibilityChanged();
	}

	private void NotifyMonitorGlyphVisibilityChanged()
	{
		OnPropertyChanged(nameof(ShowMonitorActivity));
		OnPropertyChanged(nameof(ShowMonitorUnread));
		OnPropertyChanged(nameof(ShowMonitorPaused));
	}
}

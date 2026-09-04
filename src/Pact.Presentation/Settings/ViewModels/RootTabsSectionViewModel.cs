using System.Collections.ObjectModel;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Edits project-independent ROOT terminals and browser pages through live state.</summary>
public sealed class RootTabsSectionViewModel : SettingsSectionViewModelBase
{
	private static readonly SessionSettingsEdit EmptySessionEdit = new();
	private static readonly RootWebPageSettingsEdit EmptyWebPageEdit = new();
	private readonly Func<RootTabsViewModel> _rootTabsProvider;
	private readonly IRootTabsSettingsEditor _editor;

	/// <summary>Creates the ROOT tabs settings section.</summary>
	public RootTabsSectionViewModel(
		Func<RootTabsViewModel> rootTabsProvider,
		IRootTabsSettingsEditor editor,
		string filePath)
		: base(
			SettingsSection.RootTabs,
			"Root tabs",
			"Project-independent terminals and web pages. Terminal working directories are explicit and default to the Windows user profile when the tab is created.",
			"root-tabs.json",
			filePath)
	{
		_rootTabsProvider = rootTabsProvider
			?? throw new ArgumentNullException(nameof(rootTabsProvider));
		_editor = editor ?? throw new ArgumentNullException(nameof(editor));
	}

	/// <summary>ROOT terminal and browser settings items in tree order.</summary>
	public ObservableCollection<object> Items { get; } = [];

	/// <summary>Currently selected ROOT item.</summary>
	public object? SelectedItem
	{
		get;
		set => SetField(ref field, value);
	}

	/// <summary>Whether the section has no saved ROOT items.</summary>
	public bool IsEmpty => Items.Count == 0;

	/// <inheritdoc />
	public override Task LoadAsync(CancellationToken cancellationToken)
	{
		DetachItems();
		Items.Clear();
		var rootTabs = _rootTabsProvider();
		foreach (var session in rootTabs.Sessions)
		{
			SessionSettingsItemViewModel item = new(
				session,
				showWorkingDirectoryForAllKinds: true);
			item.Changed += OnItemChanged;
			Items.Add(item);
		}

		foreach (var webPage in rootTabs.WebPages)
		{
			RootWebPageSettingsItemViewModel item = new(webPage);
			item.Changed += OnItemChanged;
			Items.Add(item);
		}

		SelectedItem = Items.FirstOrDefault();
		StatusText = null;
		ClearDirty();
		OnPropertyChanged(nameof(IsEmpty));
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		foreach (var item in Items)
		{
			var error = item switch
			{
				SessionSettingsItemViewModel session => session.Validate(),
				RootWebPageSettingsItemViewModel webPage => webPage.Validate(),
				_ => null
			};
			if (error is not null)
			{
				StatusText = error;
				return false;
			}
		}

		var appliedCount = 0;
		foreach (var item in Items)
		{
			switch (item)
			{
				case SessionSettingsItemViewModel session:
					var sessionEdit = session.BuildSessionEdit();
					if (sessionEdit != EmptySessionEdit)
					{
						await _editor.UpdateRootSessionSettingsAsync(
							session.Id,
							sessionEdit,
							cancellationToken);
						session.Rebaseline();
						appliedCount++;
					}
					break;
				case RootWebPageSettingsItemViewModel webPage:
					var webPageEdit = webPage.BuildEdit();
					if (webPageEdit != EmptyWebPageEdit)
					{
						await _editor.UpdateRootWebPageSettingsAsync(
							webPage.Id,
							webPageEdit,
							cancellationToken);
						webPage.Rebaseline();
						appliedCount++;
					}
					break;
			}
		}

		StatusText = appliedCount == 0 ? "No changes." : "Saved.";
		ClearDirty();
		return true;
	}

	/// <inheritdoc />
	public override void SelectItem(string? itemId, string? subItemId)
	{
		if (string.IsNullOrWhiteSpace(itemId))
		{
			return;
		}

		SelectedItem = Items.FirstOrDefault(item => item switch
		{
			SessionSettingsItemViewModel session =>
				string.Equals(session.Id, itemId, StringComparison.Ordinal),
			RootWebPageSettingsItemViewModel webPage =>
				string.Equals(webPage.Id, itemId, StringComparison.Ordinal),
			_ => false
		}) ?? SelectedItem;
	}

	private void DetachItems()
	{
		foreach (var item in Items)
		{
			switch (item)
			{
				case SessionSettingsItemViewModel session:
					session.Changed -= OnItemChanged;
					break;
				case RootWebPageSettingsItemViewModel webPage:
					webPage.Changed -= OnItemChanged;
					break;
			}
		}
	}

	private void OnItemChanged(object? sender, EventArgs e) =>
		IsDirty = Items.Any(item => item switch
		{
			SessionSettingsItemViewModel session => session.IsItemDirty,
			RootWebPageSettingsItemViewModel webPage => webPage.IsItemDirty,
			_ => false
		});
}

/// <summary>Editable settings projection for one ROOT browser page.</summary>
public sealed class RootWebPageSettingsItemViewModel : SettingsObservableObject
{
	private string _baselineTitle;
	private string _baselineUrl;

	/// <summary>Creates an editable projection from a loaded ROOT page.</summary>
	public RootWebPageSettingsItemViewModel(WebPageViewModel webPage)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		Id = webPage.Record.Id;
		_title = webPage.Record.Title;
		_baselineTitle = _title;
		_url = webPage.Record.ResumeUrl;
		_baselineUrl = _url;
		IsPaused = webPage.IsManuallyPaused;
	}

	/// <summary>Raised whenever an editable field changes.</summary>
	public event EventHandler? Changed;

	/// <summary>Stable page id.</summary>
	public string Id { get; }

	/// <summary>Whether the page is currently parked.</summary>
	public bool IsPaused { get; }

	/// <summary>Compact read-only summary.</summary>
	public string InfoLine => IsPaused ? $"{Id} · web · Paused" : $"{Id} · web";

	/// <summary>Tab title.</summary>
	public string Title
	{
		get => _title;
		set
		{
			if (SetField(ref _title, value))
			{
				RaiseChanged();
			}
		}
	}
	private string _title;

	/// <summary>Address opened and restored by the page.</summary>
	public string Url
	{
		get => _url;
		set
		{
			if (SetField(ref _url, value))
			{
				RaiseChanged();
			}
		}
	}
	private string _url;

	/// <summary>Whether either editable field differs from its loaded baseline.</summary>
	public bool IsItemDirty =>
		!ProjectFieldDiff.TrimEquals(Title, _baselineTitle)
		|| !ProjectFieldDiff.TrimEquals(Url, _baselineUrl);

	/// <summary>Builds a minimal partial edit.</summary>
	public RootWebPageSettingsEdit BuildEdit() => new(
		ProjectFieldDiff.TrimEquals(Title, _baselineTitle) ? null : Title,
		ProjectFieldDiff.TrimEquals(Url, _baselineUrl) ? null : Url);

	/// <summary>Validates a non-empty title and absolute HTTP(S) URL.</summary>
	public string? Validate()
	{
		if (string.IsNullOrWhiteSpace(Title))
		{
			return "Every ROOT web page needs a non-empty title.";
		}

		if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri)
			|| uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			return $"ROOT web page '{Title}' needs an absolute HTTP or HTTPS URL.";
		}

		return null;
	}

	internal void Rebaseline()
	{
		_baselineTitle = Title;
		_baselineUrl = Url;
		RaiseChanged();
	}

	private void RaiseChanged()
	{
		OnPropertyChanged(nameof(IsItemDirty));
		Changed?.Invoke(this, EventArgs.Empty);
	}
}
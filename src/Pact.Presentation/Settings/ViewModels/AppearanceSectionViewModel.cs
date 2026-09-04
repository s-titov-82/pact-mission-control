namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Edits and applies the persisted application color-theme preference.</summary>
public sealed class AppearanceSectionViewModel : SettingsSectionViewModelBase
{
	private readonly AppearanceSettingsStore _store;
	private readonly Action<AppearancePreferences> _apply;
	private bool _loading;

	/// <summary>Creates the appearance section over the supplied store and application callback.</summary>
	public AppearanceSectionViewModel(
		AppearanceSettingsStore store,
		Action<AppearancePreferences> apply)
		: base(
			SettingsSection.Appearance,
			"Appearance",
			"Application theme and optional interface details.",
			"appearance.json",
			ResolvePath(store))
	{
		_store = store;
		_apply = apply ?? throw new ArgumentNullException(nameof(apply));
	}

	// A base-constructor argument is evaluated before the body, so the null check cannot
	// be a statement here; routing the store through this guard keeps the failure an
	// ArgumentNullException naming the parameter.
	private static string ResolvePath(AppearanceSettingsStore store)
	{
		ArgumentNullException.ThrowIfNull(store);
		return store.Path;
	}

	/// <summary>Lists the supported theme choices in display order.</summary>
	public IReadOnlyList<AppearanceMode> Modes { get; } =
		[AppearanceMode.System, AppearanceMode.Light, AppearanceMode.Dark];

	/// <summary>Gets or sets the theme that will be persisted on save.</summary>
	public AppearanceMode SelectedMode
	{
		get;
		set
		{
			if (SetField(ref field, value) && !_loading)
			{
				MarkDirty();
			}
		}
	}

	/// <summary>Gets or sets whether the right panel shows details for the selected tab.</summary>
	public bool ShowSelectedTabDetails
	{
		get;
		set
		{
			if (SetField(ref field, value) && !_loading)
			{
				MarkDirty();
			}
		}
	} = true;

	/// <summary>Gets or sets whether selected terminal details include live process-tree metrics.</summary>
	public bool ShowExternalProcessMetrics
	{
		get;
		set
		{
			if (SetField(ref field, value) && !_loading)
			{
				MarkDirty();
			}
		}
	}

	/// <inheritdoc />
	public override async Task LoadAsync(CancellationToken cancellationToken)
	{
		_loading = true;
		try
		{
			var preferences = await _store.LoadPreferencesAsync(cancellationToken);
			SelectedMode = preferences.Theme;
			ShowSelectedTabDetails = preferences.ShowSelectedTabDetails;
			ShowExternalProcessMetrics = preferences.ShowExternalProcessMetrics;
			StatusText = null;
			ClearDirty();
		}
		finally
		{
			_loading = false;
		}
	}

	/// <inheritdoc />
	public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		AppearancePreferences preferences = new(
			SelectedMode,
			ShowSelectedTabDetails,
			ShowExternalProcessMetrics);
		await _store.SaveAsync(preferences, cancellationToken);
		_apply(preferences);
		StatusText = $"Saved Appearance ({SelectedMode}).";
		ClearDirty();
		return true;
	}
}

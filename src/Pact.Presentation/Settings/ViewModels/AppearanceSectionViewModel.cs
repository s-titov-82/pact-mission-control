namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Edits and applies the persisted application color-theme preference.</summary>
public sealed partial class AppearanceSectionViewModel : SettingsSectionViewModelBase
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
		foreach (var color in Palette)
		{
			color.PropertyChanged += (_, _) => TerminalChanged();
		}
	}

	// A base-constructor argument is evaluated before the body, so the null check cannot
	// be a statement here; routing the store through this guard keeps the failure an
	// ArgumentNullException naming the parameter.
	private static string ResolvePath(AppearanceSettingsStore store)
	{
		ArgumentNullException.ThrowIfNull(store);
		return store.Path;
	}

	/// <summary>Show reminders for new items in the attention feed.</summary>
	public bool ShowNotifications
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

	/// <summary>Selected notification sound in editor display order.</summary>
	public int NotificationSoundIndex
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

	/// <summary>Selected banner mode in the order offered by the editor.</summary>
	public int NotificationModeIndex
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				OnPropertyChanged(nameof(IsCustomNotificationTimeout));
				if (!_loading)
				{
					MarkDirty();
				}
			}
		}
	}

	/// <summary>Whether the seconds field is used by the current mode.</summary>
	public bool IsCustomNotificationTimeout => NotificationModeIndex == (int)NotificationBannerMode.CustomTimeout;

	/// <summary>Seconds before moving the reminder to Notification Center in custom mode.</summary>
	public int NotificationSeconds
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

	/// <summary>Gets or sets whether the sidebar shows site icons for web tabs.</summary>
	public bool ShowFavicons
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

	/// <summary>Gets or sets whether the sidebar shows agent logos alongside sessions.</summary>
	public bool ShowAgentIcons
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
			ShowFavicons = preferences.ShowFavicons;
			ShowAgentIcons = preferences.ShowAgentIcons;
			ShowSelectedTabDetails = preferences.ShowSelectedTabDetails;
			ShowExternalProcessMetrics = preferences.ShowExternalProcessMetrics;
			ShowNotifications = preferences.ShowNotifications;
			NotificationSoundIndex = (int)preferences.NotificationSoundMode;
			NotificationSeconds = preferences.NotificationSeconds;
			NotificationModeIndex = (int)preferences.NotificationMode;
			SetTerminal(preferences.Terminal ?? new());
			StatusText = null;
			await LoadDefaultProfilesAsync(cancellationToken);
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
		var terminal = new TerminalAppearance
		{
			FontFamily = FontFamily.Trim(),
			FontSize = FontSize,
			LineHeight = LineHeight,
			Colors = Palette.Where(c => !string.IsNullOrWhiteSpace(c.Value)).ToDictionary(c => c.Name, c => c.Value.Trim())
		};
		if (!terminal.IsValid)
		{
			StatusText = "Enter a font name, a size from 4 to 128 px, line height from 1 to 4 and colors as #RRGGBB.";
			return false;
		}
		var preferences = GetPreviewPreferences() with { Terminal = terminal.UsesDefaults ? null : terminal };
		await _store.SaveAsync(preferences, cancellationToken);
		_apply(preferences);
		StatusText = $"Saved Appearance ({SelectedMode}).";
		ClearDirty();
		return true;
	}

	/// <summary>Captures the current banner and sound choices for a preview before saving.</summary>
	public AppearancePreferences GetPreviewPreferences() =>
		new(SelectedMode, ShowSelectedTabDetails, ShowExternalProcessMetrics)
		{
			ShowFavicons = ShowFavicons,
			ShowAgentIcons = ShowAgentIcons,
			ShowNotifications = ShowNotifications,
			NotificationSoundMode = (NotificationSoundMode)NotificationSoundIndex,
			NotificationSeconds = NotificationSeconds,
			NotificationMode = (NotificationBannerMode)NotificationModeIndex
		};
}

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One editable ANSI or terminal chrome color.</summary>
public sealed class TerminalPaletteEntry(string name) : SettingsObservableObject
{
	/// <summary>Key understood by xterm.js.</summary>
	public string Name { get; } = name;
	/// <summary>Hex RGB override, or empty to follow the theme.</summary>
	public string Value { get; set => SetField(ref field, value); } = "";
}

public sealed partial class AppearanceSectionViewModel
{
	private WindowsTerminalProfiles? _profiles;
	/// <summary>Font family used by the terminal.</summary>
	public string FontFamily
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				TerminalChanged();
			}
		}
	} = "";
	/// <summary>Terminal font size in logical pixels.</summary>
	public double FontSize
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				TerminalChanged();
			}
		}
	}
	/// <summary>Line spacing multiplier.</summary>
	public double LineHeight
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				TerminalChanged();
			}
		}
	}
	/// <summary>Editable palette entries; an empty value follows the Pact theme.</summary>
	public IReadOnlyList<TerminalPaletteEntry> Palette { get; } =
		TerminalAppearance.ColorNames.Select(key => new TerminalPaletteEntry(key)).ToArray();
	/// <summary>Palette override used by the sample or null for the theme default.</summary>
	public string? PreviewBackground => PreviewColor("background");
	/// <inheritdoc cref="PreviewBackground"/>
	public string? PreviewForeground => PreviewColor("foreground");
	/// <inheritdoc cref="PreviewBackground"/>
	public string? PreviewGreen => PreviewColor("green");
	/// <summary>Pixel baseline spacing for the sample.</summary>
	public double PreviewLineHeight => FontSize * LineHeight * 1.2;
	/// <summary>Names available in the chosen Windows Terminal settings file.</summary>
	public IReadOnlyList<string> ProfileNames => _profiles?.Names ?? [];
	/// <summary>Whether the profile picker has any entries to offer.</summary>
	public bool HasProfiles => ProfileNames.Count != 0;
	/// <summary>Selected profile within the imported file.</summary>
	public int ProfileIndex { get; set => SetField(ref field, value); } = -1;

	/// <summary>Loads available profiles without applying one until selected.</summary>
	public void LoadProfiles(string json)
	{
		SetProfiles(new WindowsTerminalProfiles(json));
	}

	private void SetProfiles(WindowsTerminalProfiles? profiles)
	{
		_profiles = profiles;
		OnPropertyChanged(nameof(ProfileNames));
		OnPropertyChanged(nameof(HasProfiles));
		ProfileIndex = ProfileNames.Count > 0 ? 0 : -1;
	}

	private async Task LoadDefaultProfilesAsync(CancellationToken cancellationToken)
	{
		var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json");
		try
		{
			SetProfiles(File.Exists(file)
				? new WindowsTerminalProfiles(await File.ReadAllTextAsync(file, cancellationToken))
				: null);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
			or FormatException or InvalidOperationException)
		{
			SetProfiles(null);
			StatusText = "Could not read Windows Terminal settings.json. Choose a settings file in Terminal theme.";
		}
	}

	/// <summary>Copies the selected profile into the draft settings.</summary>
	public void ImportProfile(bool dark)
	{
		if (_profiles is not null && ProfileIndex >= 0 && ProfileIndex < ProfileNames.Count)
		{
			SetTerminal(_profiles.Import(ProfileIndex, dark));
		}
	}

	private string? PreviewColor(string key) =>
		Palette.First(c => c.Name == key).Value is { Length: 7 } color && color[0] == '#' && color[1..].All(Uri.IsHexDigit) ? color : null;

	private void SetTerminal(TerminalAppearance appearance)
	{
		FontFamily = appearance.FontFamily;
		FontSize = appearance.FontSize;
		LineHeight = appearance.LineHeight;
		foreach (var color in Palette)
		{
			color.Value = appearance.Colors.GetValueOrDefault(color.Name) ?? "";
		}
	}

	private void TerminalChanged()
	{
		if (!_loading)
		{
			MarkDirty();
		}
		OnPropertyChanged(nameof(PreviewBackground));
		OnPropertyChanged(nameof(PreviewForeground));
		OnPropertyChanged(nameof(PreviewGreen));
		OnPropertyChanged(nameof(PreviewLineHeight));
	}
}

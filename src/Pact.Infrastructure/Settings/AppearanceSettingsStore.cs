using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Settings;

/// <summary>Sound played when a new Windows attention notification appears.</summary>
public enum NotificationSoundMode
{
	/// <summary>Deliver notifications silently.</summary>
	Silent,
	/// <summary>Use the notification sound selected in the Windows sound scheme.</summary>
	Windows,
	/// <summary>Play Pact's short messenger chirp.</summary>
	Icq,
	/// <summary>Play Pact's station announcement chime.</summary>
	Station
}

/// <summary>How Windows presents an attention event before moving it to Notification Center.</summary>
public enum NotificationBannerMode
{
	/// <summary>Use the user's Windows banner duration.</summary>
	WindowsDefault,
	/// <summary>Keep a reminder visible until the user acts.</summary>
	UntilDismissed,
	/// <summary>Move the reminder to Notification Center after a configured interval.</summary>
	CustomTimeout
}

/// <summary>Identifies how the application chooses its color theme.</summary>
public enum AppearanceMode
{
	/// <summary>Follow the operating system's light/dark preference.</summary>
	System,

	/// <summary>Always use the light theme.</summary>
	Light,

	/// <summary>Always use the dark theme.</summary>
	Dark
}

/// <summary>Application appearance preferences shared by startup and the settings editor.</summary>
/// <param name="Theme">How the application chooses its color theme.</param>
/// <param name="ShowSelectedTabDetails">Whether the right panel shows details for the selected tab.</param>
/// <param name="ShowExternalProcessMetrics">Whether selected live-tab details include external process metrics.</param>
public sealed record AppearancePreferences(
	AppearanceMode Theme,
	bool ShowSelectedTabDetails = true,
	bool ShowExternalProcessMetrics = false)
{
	/// <summary>Show site icons alongside web tabs in the sidebar.</summary>
	public bool ShowFavicons { get; init; } = true;
	/// <summary>Show agent logos alongside terminal sessions in the sidebar.</summary>
	public bool ShowAgentIcons { get; init; } = true;
	/// <summary>Terminal rendering overrides. Missing overrides follow the application theme.</summary>
	public TerminalAppearance? Terminal { get; init; }
	/// <summary>Controls native Windows notifications for new attention events.</summary>
	public bool ShowNotifications { get; init; }
	/// <summary>Audio cue for new attention events.</summary>
	public NotificationSoundMode NotificationSoundMode { get; init; }
	/// <summary>Presentation mode for new notifications.</summary>
	public NotificationBannerMode NotificationMode { get; init; }
	/// <summary>Banner timeout in seconds when using a custom interval.</summary>
	public int NotificationSeconds { get; init; } = 10;
}

/// <summary>Persists the application appearance preference independently of editable feature settings.</summary>
public sealed class AppearanceSettingsStore
{
	private readonly string? _stagingDirectory;

	/// <summary>Creates a store that reads and writes the specified JSON file.</summary>
	public AppearanceSettingsStore(string path, string? stagingDirectory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Path = path;
		_stagingDirectory = stagingDirectory;
	}

	/// <summary>Gets the absolute JSON path edited by this store.</summary>
	public string Path { get; }

	/// <summary>Loads the saved preference, falling back to System for missing or invalid data.</summary>
	public async Task<AppearanceMode> LoadAsync(CancellationToken cancellationToken) =>
		(await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false)).Theme;

	/// <summary>
	/// Loads all appearance preferences. Legacy files show selected-tab details but keep external
	/// process metrics disabled until the user explicitly opts in.
	/// </summary>
	public async Task<AppearancePreferences> LoadPreferencesAsync(CancellationToken cancellationToken)
	{
		var settings = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
		var sound = ReadValue<NotificationSoundMode?>(settings["notificationSoundMode"], null);
		var banner = ReadValue(settings["notificationMode"], NotificationBannerMode.WindowsDefault);
		var terminal = ReadValue<TerminalAppearance?>(settings["terminal"], null);

		return new(
			Parse(ReadValue<string?>(settings["theme"], null)),
			ReadValue(settings["showSelectedTabDetails"], true),
			ReadValue(settings["showExternalProcessMetrics"], false))
		{
			ShowFavicons = ReadValue(settings["showFavicons"], true),
			ShowAgentIcons = ReadValue(settings["showAgentIcons"], true),
			Terminal = terminal is { IsValid: true } ? terminal : null,
			ShowNotifications = ReadValue(settings["showNotifications"], false),
			NotificationSoundMode = sound is { } knownSound && Enum.IsDefined(knownSound)
				? knownSound
				: ReadValue(settings["notificationSound"], false) ? NotificationSoundMode.Windows : NotificationSoundMode.Silent,
			NotificationMode = Enum.IsDefined(banner) ? banner : NotificationBannerMode.WindowsDefault,
			NotificationSeconds = Math.Clamp(ReadValue(settings["notificationSeconds"], 10), 1, 3600)
		};
	}

	/// <summary>Saves a normalized appearance preference atomically.</summary>
	public async Task SaveAsync(AppearanceMode mode, CancellationToken cancellationToken)
	{
		var current = await LoadPreferencesAsync(cancellationToken).ConfigureAwait(false);
		await SaveAsync(current with { Theme = mode }, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Saves all appearance preferences atomically while preserving unknown JSON fields.</summary>
	public async Task SaveAsync(
		AppearancePreferences preferences,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(preferences);
		var root = await LoadRootAsync(cancellationToken).ConfigureAwait(false);
		root["theme"] = preferences.Theme.ToString().ToLowerInvariant();
		root["showFavicons"] = preferences.ShowFavicons;
		root["showAgentIcons"] = preferences.ShowAgentIcons;
		root["showSelectedTabDetails"] = preferences.ShowSelectedTabDetails;
		root["showExternalProcessMetrics"] = preferences.ShowExternalProcessMetrics;
		root["showNotifications"] = preferences.ShowNotifications;
		root.Remove("notificationSound");
		root["notificationSoundMode"] = JsonSerializer.SerializeToNode(preferences.NotificationSoundMode, SettingsFileStore.JsonOptions);
		root["notificationSeconds"] = Math.Clamp(preferences.NotificationSeconds, 1, 3600);
		root["notificationMode"] = JsonSerializer.SerializeToNode(preferences.NotificationMode, SettingsFileStore.JsonOptions);
		if (preferences.Terminal is not null || ReadValue<TerminalAppearance?>(root["terminal"], null) is { IsValid: true })
		{
			var terminal = root["terminal"] as JsonObject ?? new JsonObject();
			var appearance = preferences.Terminal ?? new TerminalAppearance();
			terminal["fontFamily"] = appearance.FontFamily;
			terminal["fontSize"] = appearance.FontSize;
			terminal["lineHeight"] = appearance.LineHeight;
			terminal["colors"] = JsonSerializer.SerializeToNode(appearance.Colors, SettingsFileStore.JsonOptions);
			if (terminal.Parent is null)
			{
				root["terminal"] = terminal;
			}
		}
		await AtomicFileWriter.WriteTextAsync(
			Path,
			root.ToJsonString(SettingsFileStore.JsonOptions),
			_stagingDirectory,
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<JsonObject> LoadRootAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(Path))
		{
			return [];
		}

		try
		{
			await using var stream = File.OpenRead(Path);
			return await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)
				.ConfigureAwait(false) as JsonObject ?? [];
		}
		catch (JsonException)
		{
			return [];
		}
		catch (IOException)
		{
			return [];
		}
	}

	private static AppearanceMode Parse(string? value) => value?.ToLowerInvariant() switch
	{
		"light" => AppearanceMode.Light,
		"dark" => AppearanceMode.Dark,
		_ => AppearanceMode.System
	};

	private static T ReadValue<T>(JsonNode? node, T fallback)
	{
		try
		{
			return node is null ? fallback : node.Deserialize<T>(SettingsFileStore.JsonOptions)!;
		}
		catch (JsonException)
		{
			return fallback;
		}
	}
}

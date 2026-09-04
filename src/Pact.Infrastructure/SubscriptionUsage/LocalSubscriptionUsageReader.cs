using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pact.Core.Agents;

namespace Pact.Infrastructure.SubscriptionUsage;
/// <summary>
/// Reads subscription usage from what each agent leaves on this machine: Codex from its session
/// files, Claude from its statusline input or by running its usage command.
/// </summary>
public sealed partial class LocalSubscriptionUsageReader : ISubscriptionUsageReader
{
	private const int CodexCandidateFileCount = 10;
	private const int CodexTailBytes = 1024 * 1024;
	private static readonly Regex ClaudeCurrentSessionRegex = MyRegex();
	private static readonly Regex ClaudeCurrentWeekRegex = MyRegex1();
	private static readonly Regex ClaudeFableWeekRegex = CreateClaudeFableWeekRegex();
	private readonly string _codexSessionsDirectory;
	private readonly string _claudeStatuslineInputPath;
	private readonly IClaudeUsageCommandRunner _claudeUsageCommandRunner;

	/// <summary>
	/// Creates a reader over the given agent data locations, running Claude's usage command
	/// through PowerShell.
	/// </summary>
	public LocalSubscriptionUsageReader(
		string codexSessionsDirectory,
		string claudeStatuslineInputPath)
		: this(codexSessionsDirectory, claudeStatuslineInputPath, new PowerShellClaudeUsageCommandRunner())
	{
	}

	/// <summary>
	/// Creates a reader with an injectable usage-command runner, for tests that must not launch
	/// a process.
	/// </summary>
	public LocalSubscriptionUsageReader(
		string codexSessionsDirectory,
		string claudeStatuslineInputPath,
		IClaudeUsageCommandRunner claudeUsageCommandRunner)
	{
		_codexSessionsDirectory = codexSessionsDirectory;
		_claudeStatuslineInputPath = claudeStatuslineInputPath;
		_claudeUsageCommandRunner = claudeUsageCommandRunner;
	}

	/// <summary>
	/// Creates a reader pointed at the current user's agent data under their profile directory.
	/// </summary>
	public static LocalSubscriptionUsageReader ForCurrentUser()
	{
		var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return new LocalSubscriptionUsageReader(
			Path.Combine(userProfile, ".codex", "sessions"),
			Path.Combine(userProfile, ".claude", "statusline-input.json"));
	}

	/// <inheritdoc />
	public async Task<SubscriptionUsageSnapshot> ReadAsync(
		AgentProfileRecord profile,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profile);

		try
		{
			return profile.Kind switch
			{
				AgentKind.Codex => await ReadCodexAsync(profile, cancellationToken).ConfigureAwait(false),
				AgentKind.Claude => await ReadClaudeAsync(profile, cancellationToken).ConfigureAwait(false),
				_ => CreateUnavailable(profile, "Unsupported profile")
			};
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return new SubscriptionUsageSnapshot(
				profile.Id,
				profile.DisplayName,
				profile.Kind,
				SubscriptionUsageState.Failed,
				FiveHour: null,
				Weekly: null,
				ex.Message,
				RawResponseText: null,
				ErrorDetailsText: ex.Message,
				DateTimeOffset.UtcNow);
		}
	}

	private async Task<SubscriptionUsageSnapshot> ReadClaudeAsync(
		AgentProfileRecord profile,
		CancellationToken cancellationToken)
	{
		var commandName = GetCommandName(profile.CommandTemplate);
		if (!string.IsNullOrWhiteSpace(commandName))
		{
			return await ReadClaudeUsageCommandAsync(profile, commandName, cancellationToken)
				.ConfigureAwait(false);
		}

		var statuslineInputPath = ResolveClaudeStatuslineInputPath(profile);
		if (statuslineInputPath is null)
		{
			return CreateUnavailable(profile, "No Claude usage data");
		}

		await using var stream = OpenClaudeStatuslineInput(statuslineInputPath);
		using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		var rawJson = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
		JsonDocument document;
		try
		{
			document = JsonDocument.Parse(rawJson);
		}
		catch (JsonException ex)
		{
			return CreateFailed(
				profile,
				"Claude statusline JSON could not be parsed",
				rawJson,
				ex.Message,
				File.GetLastWriteTimeUtc(statuslineInputPath));
		}

		using (document)
		{
			if (!document.RootElement.TryGetProperty("rate_limits", out var rateLimits))
			{
				return CreateUnavailable(
					profile,
					"No Claude rate limits",
					rawJson,
					"No Claude rate limits");
			}

			return CreateReady(
				profile,
				ReadLimit(rateLimits, "five_hour", "used_percentage"),
				ReadLimit(rateLimits, "seven_day", "used_percentage"),
				File.GetLastWriteTimeUtc(statuslineInputPath),
				rawJson);
		}
	}

	internal static FileStream OpenClaudeStatuslineInput(string path) => new FileStream(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete,
			bufferSize: 4096,
			FileOptions.Asynchronous | FileOptions.SequentialScan);

	private async Task<SubscriptionUsageSnapshot> ReadClaudeUsageCommandAsync(
		AgentProfileRecord profile,
		string commandName,
		CancellationToken cancellationToken)
	{
		var result = await _claudeUsageCommandRunner.RunAsync(commandName, cancellationToken)
			.ConfigureAwait(false);
		var rawResponse = BuildClaudeRawResponse(result.StandardOutput, result.StandardError);
		if (!result.Succeeded)
		{
			return CreateFailed(
				profile,
				"Claude usage command failed",
				rawResponse,
				result.FailureMessage ?? "Claude usage command failed.",
				result.UpdatedAt);
		}

		var parsed = TryParseClaudeUsageOutput(
			profile,
			result.StandardOutput,
			result.UpdatedAt,
			rawResponse);
		return parsed ?? CreateFailed(
			profile,
			"Claude usage response was not recognized",
			rawResponse,
			"Unable to parse Claude usage response.",
			result.UpdatedAt);
	}

	private static SubscriptionUsageSnapshot? TryParseClaudeUsageOutput(
		AgentProfileRecord profile,
		string output,
		DateTimeOffset updatedAt,
		string? rawResponse)
	{
		var session = ClaudeCurrentSessionRegex.Match(output);
		var week = ClaudeCurrentWeekRegex.Match(output);
		var fableWeek = ClaudeFableWeekRegex.Match(output);
		if (!session.Success && !week.Success && !fableWeek.Success)
		{
			return null;
		}

		return CreateReady(
			profile,
			ReadClaudeUsageLimit(session, updatedAt),
			ReadClaudeUsageLimit(week, updatedAt),
			updatedAt,
			rawResponse,
			ReadClaudeUsageLimit(fableWeek, updatedAt));
	}

	private static string? BuildClaudeRawResponse(string standardOutput, string standardError)
	{
		var output = standardOutput.Trim();
		var error = standardError.Trim();
		if (output.Length == 0 && error.Length == 0)
		{
			return null;
		}

		if (error.Length == 0)
		{
			return output;
		}

		var errorSection = $"[stderr]{Environment.NewLine}{error}";
		return output.Length == 0
			? errorSection
			: $"{output}{Environment.NewLine}{Environment.NewLine}{errorSection}";
	}

	private static SubscriptionLimitSnapshot? ReadClaudeUsageLimit(Match match, DateTimeOffset now)
	{
		if (!match.Success
			|| !int.TryParse(match.Groups["used"].Value, CultureInfo.InvariantCulture, out var usedPercent))
		{
			return null;
		}

		var resetsAt = ParseClaudeUsageResetAt(match.Groups["reset"].Value, now);
		return new SubscriptionLimitSnapshot(Math.Clamp(usedPercent, 0, 100), resetsAt);
	}

	private static DateTimeOffset? ParseClaudeUsageResetAt(string value, DateTimeOffset now)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		var resetText = value.Trim();
		var timezoneStart = resetText.IndexOf(" (", StringComparison.Ordinal);
		if (timezoneStart >= 0)
		{
			resetText = resetText[..timezoneStart].TrimEnd();
		}

		var localNow = now.ToLocalTime();
		var resetTextWithYear = $"{resetText}, {localNow.Year}";
		string[] formats =
		[
			"MMM d, h:mmtt, yyyy",
			"MMM d, htt, yyyy",
			"MMM d, h:mm tt, yyyy",
			"MMM d, h tt, yyyy",
			"MMM d, H:mm, yyyy",
			"MMM d, H, yyyy"
		];

		if (!DateTime.TryParseExact(
				resetTextWithYear,
				formats,
				CultureInfo.InvariantCulture,
				DateTimeStyles.AllowWhiteSpaces,
				out var parsedLocal))
		{
			return null;
		}

		DateTimeOffset candidate = new(DateTime.SpecifyKind(parsedLocal, DateTimeKind.Local));
		if (candidate < localNow.AddDays(-1))
		{
			candidate = candidate.AddYears(1);
		}

		return candidate.ToUniversalTime();
	}

	private string? ResolveClaudeStatuslineInputPath(AgentProfileRecord profile)
	{
		var defaultCommandName = GetCommandName(profile.CommandTemplate);
		var rootDirectory = Directory.GetParent(Path.GetDirectoryName(_claudeStatuslineInputPath) ?? string.Empty)
			?.FullName;

		if (!string.IsNullOrWhiteSpace(rootDirectory)
			&& !string.Equals(defaultCommandName, "claude", StringComparison.OrdinalIgnoreCase))
		{
			foreach (var candidate in GetProfileClaudeStatuslineCandidates(rootDirectory, profile, defaultCommandName))
			{
				if (File.Exists(candidate))
				{
					return candidate;
				}
			}

			return null;
		}

		return File.Exists(_claudeStatuslineInputPath)
			? _claudeStatuslineInputPath
			: null;
	}

	private static IEnumerable<string> GetProfileClaudeStatuslineCandidates(
		string rootDirectory,
		AgentProfileRecord profile,
		string commandName)
	{
		string[] profileDirectoryNames =
		[
			$".{profile.Id}",
			$".{commandName}"
		];

		return profileDirectoryNames
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(name => Path.Combine(rootDirectory, name, "statusline-input.json"));
	}

	private static string GetCommandName(string commandTemplate)
	{
		var trimmed = commandTemplate.Trim();
		if (string.IsNullOrWhiteSpace(trimmed))
		{
			return string.Empty;
		}

		var separatorIndex = trimmed.IndexOfAny([' ', '\t']);
		return separatorIndex < 0
			? trimmed
			: trimmed[..separatorIndex];
	}

	private async Task<SubscriptionUsageSnapshot> ReadCodexAsync(
		AgentProfileRecord profile,
		CancellationToken cancellationToken)
	{
		if (!Directory.Exists(_codexSessionsDirectory))
		{
			return CreateUnavailable(profile, "No Codex session data");
		}

		var files = Directory
			.EnumerateFiles(_codexSessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
			.Select(path => new FileInfo(path))
			.OrderByDescending(file => file.LastWriteTimeUtc)
			.Take(CodexCandidateFileCount)
			.Select(file => file.FullName);

		foreach (var file in files)
		{
			var snapshot = await TryReadCodexFileAsync(
					profile,
					file,
					cancellationToken)
				.ConfigureAwait(false);
			if (snapshot is not null)
			{
				return snapshot;
			}
		}

		return CreateUnavailable(profile, "No Codex rate limits");
	}

	private static async Task<SubscriptionUsageSnapshot?> TryReadCodexFileAsync(
		AgentProfileRecord profile,
		string path,
		CancellationToken cancellationToken)
	{
		SubscriptionLimitSnapshot? fiveHour = null;
		SubscriptionLimitSnapshot? weekly = null;

		await using var stream = File.Open(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete);
		var buffer = new byte[Math.Min(CodexTailBytes, checked((int)Math.Min(stream.Length, int.MaxValue)))];
		stream.Seek(-buffer.Length, SeekOrigin.End);
		await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);

		foreach (var line in Encoding.UTF8.GetString(buffer).Split(
			['\r', '\n'],
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			try
			{
				using var document = JsonDocument.Parse(line);
				if (document.RootElement.ValueKind != JsonValueKind.Object
					|| !document.RootElement.TryGetProperty("payload", out var payload)
					|| payload.ValueKind != JsonValueKind.Object
					|| !payload.TryGetProperty("type", out var type)
					|| !string.Equals(type.GetString(), "token_count", StringComparison.Ordinal)
					|| !payload.TryGetProperty("rate_limits", out var rateLimits))
				{
					continue;
				}

				ReadCodexLimit(rateLimits, "primary", isLegacyFiveHour: true, ref fiveHour, ref weekly);
				ReadCodexLimit(rateLimits, "secondary", isLegacyFiveHour: false, ref fiveHour, ref weekly);
			}
			catch (JsonException)
			{
				continue;
			}
		}

		return fiveHour is null && weekly is null
			? null
			: CreateReady(profile, fiveHour, weekly, File.GetLastWriteTimeUtc(path));
	}

	private static void ReadCodexLimit(
		JsonElement rateLimits,
		string propertyName,
		bool isLegacyFiveHour,
		ref SubscriptionLimitSnapshot? fiveHour,
		ref SubscriptionLimitSnapshot? weekly)
	{
		var snapshot = ReadLimit(rateLimits, propertyName, "used_percent");
		if (snapshot is null
			|| !rateLimits.TryGetProperty(propertyName, out var limit)
			|| limit.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		int? windowMinutes = limit.TryGetProperty("window_minutes", out var window)
			&& window.ValueKind == JsonValueKind.Number
			&& window.TryGetInt32(out var parsedWindow)
				? parsedWindow
				: null;
		if (windowMinutes >= 24 * 60)
		{
			weekly = snapshot;
		}
		else if (windowMinutes is > 0)
		{
			fiveHour = snapshot;
		}
		else if (isLegacyFiveHour)
		{
			fiveHour = snapshot;
		}
		else
		{
			weekly = snapshot;
		}
	}

	private static SubscriptionLimitSnapshot? ReadLimit(
		JsonElement parent,
		string propertyName,
		string usedPercentPropertyName)
	{
		if (parent.ValueKind != JsonValueKind.Object
			|| !parent.TryGetProperty(propertyName, out var limit)
			|| limit.ValueKind != JsonValueKind.Object
			|| !limit.TryGetProperty(usedPercentPropertyName, out var usedPercentElement)
			|| !TryReadPercent(usedPercentElement, out var usedPercent))
		{
			return null;
		}

		DateTimeOffset? resetsAt = null;
		if (limit.TryGetProperty("resets_at", out var resetsAtElement)
			&& resetsAtElement.ValueKind == JsonValueKind.Number
			&& resetsAtElement.TryGetInt64(out var unixSeconds))
		{
			resetsAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
		}

		return new SubscriptionLimitSnapshot(usedPercent, resetsAt);
	}

	private static bool TryReadPercent(JsonElement element, out int percent)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Number when element.TryGetInt32(out var intValue):
				percent = Math.Clamp(intValue, 0, 100);
				return true;
			case JsonValueKind.Number when element.TryGetDouble(out var doubleValue):
				percent = Math.Clamp((int)Math.Round(doubleValue), 0, 100);
				return true;
			default:
				percent = 0;
				return false;
		}
	}

	private static SubscriptionUsageSnapshot CreateReady(
		AgentProfileRecord profile,
		SubscriptionLimitSnapshot? fiveHour,
		SubscriptionLimitSnapshot? weekly,
		DateTimeOffset sourceUpdatedAt,
		string? rawResponseText = null,
		SubscriptionLimitSnapshot? fableWeekly = null)
	{
		var localUpdatedAt = sourceUpdatedAt.ToLocalTime();
		return new SubscriptionUsageSnapshot(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Ready,
			fiveHour,
			weekly,
			$"Updated {localUpdatedAt:dd.MM HH:mm}",
			rawResponseText,
			ErrorDetailsText: null,
			sourceUpdatedAt,
			fableWeekly);
	}

	private static SubscriptionUsageSnapshot CreateUnavailable(
		AgentProfileRecord profile,
		string statusText,
		string? rawResponseText = null,
		string? errorDetailsText = null) => new SubscriptionUsageSnapshot(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Unavailable,
			FiveHour: null,
			Weekly: null,
			statusText,
			rawResponseText,
			errorDetailsText ?? statusText,
			DateTimeOffset.UtcNow);

	private static SubscriptionUsageSnapshot CreateFailed(
		AgentProfileRecord profile,
		string statusText,
		string? rawResponseText,
		string errorDetailsText,
		DateTimeOffset updatedAt) => new SubscriptionUsageSnapshot(
			profile.Id,
			profile.DisplayName,
			profile.Kind,
			SubscriptionUsageState.Failed,
			FiveHour: null,
			Weekly: null,
			statusText,
			rawResponseText,
			errorDetailsText,
			updatedAt);
	[GeneratedRegex(@"^\s*Current session:\s+(?<used>\d+)% used(?:\s+·\s+resets\s+(?<reset>.+))?\s*$", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
	private static partial Regex MyRegex();
	[GeneratedRegex(@"^\s*Current week \(all models\):\s+(?<used>\d+)% used(?:\s+·\s+resets\s+(?<reset>.+))?\s*$", RegexOptions.Multiline | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
	private static partial Regex MyRegex1();

	[GeneratedRegex(@"^\s*Current week \(Fable\):\s+(?<used>\d+)% used(?:\s+·\s+resets\s+(?<reset>.+))?\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
	private static partial Regex CreateClaudeFableWeekRegex();
}

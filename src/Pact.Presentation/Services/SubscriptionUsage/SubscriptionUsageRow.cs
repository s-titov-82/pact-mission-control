using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Pact.Core.Agents;

namespace Pact.Presentation.Services;
/// <summary>
/// Bindable row showing one profile's usage in the subscription panel. Rows are created up
/// front in an updating state and mutated in place as reads complete, so the panel's item list
/// is stable.
/// </summary>
public sealed class SubscriptionUsageRow : INotifyPropertyChanged
{

	/// <summary>Creates a row for <paramref name="profile"/> in the updating state.</summary>
	public SubscriptionUsageRow(AgentProfileRecord profile)
	{
		ArgumentNullException.ThrowIfNull(profile);

		ProfileId = profile.Id;
		ProfileName = profile.DisplayName;
		Kind = profile.Kind;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Profile this row reports on.</summary>
	public string ProfileId { get; }

	/// <summary>Profile display name.</summary>
	public string ProfileName { get; }

	/// <summary>Agent the profile launches.</summary>
	public AgentKind Kind { get; }

	/// <summary>Whether the latest read succeeded.</summary>
	public SubscriptionUsageState State { get; private set; } = SubscriptionUsageState.Updating;

	/// <summary>Latest five-hour window, or <see langword="null"/> when not reported.</summary>
	public SubscriptionLimitSnapshot? FiveHour { get; private set; }

	/// <summary>Latest weekly window, or <see langword="null"/> when not reported.</summary>
	public SubscriptionLimitSnapshot? Weekly { get; private set; }

	/// <summary>Latest Fable weekly window, or <see langword="null"/> when not reported.</summary>
	public SubscriptionLimitSnapshot? FableWeekly { get; private set; }

	/// <summary>Five-hour window formatted for display.</summary>
	public string FiveHourText { get; private set; } = "...";

	/// <summary>Weekly window formatted for display.</summary>
	public string WeeklyText { get; private set; } = "...";

	/// <summary>Short status summary.</summary>
	public string StatusText { get; private set; } = "Updating...";

	/// <summary>Unparsed agent output, for inspecting an unexpected format.</summary>
	public string? RawResponseText { get; private set; }

	/// <summary>Whether raw output is available to show.</summary>
	public bool HasRawResponse => !string.IsNullOrWhiteSpace(RawResponseText);

	/// <summary>Failure detail from the last read.</summary>
	public string? ErrorDetailsText { get; private set; }

	/// <summary>Whether failure detail is available to show.</summary>
	public bool HasErrorDetails => !string.IsNullOrWhiteSpace(ErrorDetailsText);

	/// <summary>
	/// Whether any window is nearly exhausted, which highlights the row and shortens the
	/// refresh interval.
	/// </summary>
	public bool IsNearLimit { get; private set; }

	/// <summary>Applies a snapshot using the current time.</summary>
	public SubscriptionUsageRow Apply(SubscriptionUsageSnapshot snapshot) => Apply(snapshot, DateTimeOffset.UtcNow);

	/// <summary>
	/// Applies a snapshot as of <paramref name="now"/> and returns this row.
	/// </summary>
	/// <remarks>
	/// Limit figures are replaced only when the read succeeded, so a failed refresh leaves the
	/// last known numbers visible instead of blanking the row.
	/// </remarks>
	public SubscriptionUsageRow Apply(SubscriptionUsageSnapshot snapshot, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		State = snapshot.State;
		if (snapshot.State == SubscriptionUsageState.Ready)
		{
			FiveHour = snapshot.FiveHour;
			Weekly = snapshot.Weekly;
			FableWeekly = snapshot.FableWeekly;
		}

		FiveHourText = FormatLimitText(FiveHour, State, now, showRemainingTime: false);
		WeeklyText = FormatWeeklyText(Weekly, FableWeekly, State, now);
		StatusText = snapshot.StatusText;
		RawResponseText = string.IsNullOrWhiteSpace(snapshot.RawResponseText)
			? null
			: snapshot.RawResponseText;
		ErrorDetailsText = snapshot.State == SubscriptionUsageState.Ready
			? null
			: string.IsNullOrWhiteSpace(snapshot.ErrorDetailsText)
				? snapshot.StatusText
				: snapshot.ErrorDetailsText;

		IsNearLimit = FiveHour?.IsLowAt(now) == true
			|| Weekly?.IsLowAt(now) == true
			|| FableWeekly?.IsLowAt(now) == true;

		OnPropertyChanged(nameof(State));
		OnPropertyChanged(nameof(FiveHour));
		OnPropertyChanged(nameof(Weekly));
		OnPropertyChanged(nameof(FableWeekly));
		OnPropertyChanged(nameof(FiveHourText));
		OnPropertyChanged(nameof(WeeklyText));
		OnPropertyChanged(nameof(StatusText));
		OnPropertyChanged(nameof(RawResponseText));
		OnPropertyChanged(nameof(HasRawResponse));
		OnPropertyChanged(nameof(ErrorDetailsText));
		OnPropertyChanged(nameof(HasErrorDetails));
		OnPropertyChanged(nameof(IsNearLimit));

		return this;
	}

	private static string FormatWeeklyText(
		SubscriptionLimitSnapshot? weekly,
		SubscriptionLimitSnapshot? fableWeekly,
		SubscriptionUsageState state,
		DateTimeOffset now)
	{
		var text = FormatLimitText(weekly, state, now, showRemainingTime: true);
		if (fableWeekly is null)
		{
			return text;
		}

		var fableText = $" [F: {fableWeekly.RemainingPercentAt(now)}%]";
		var lineBreak = text.IndexOf(Environment.NewLine, StringComparison.Ordinal);
		return lineBreak < 0
			? text + fableText
			: text.Insert(lineBreak, fableText);
	}

	private static string FormatLimitText(
		SubscriptionLimitSnapshot? limit,
		SubscriptionUsageState state,
		DateTimeOffset now,
		bool showRemainingTime)
	{
		if (limit is not null)
		{
			var remainingPercent = limit.RemainingPercentAt(now);
			var resetText = showRemainingTime
				? FormatRemainingTime(limit.ResetsAt, now)
				: FormatResetText(limit.ResetsAt, now);
			return string.IsNullOrWhiteSpace(resetText)
				? $"{remainingPercent}%"
				: $"{remainingPercent}%{Environment.NewLine}{resetText}";
		}

		return state switch
		{
			SubscriptionUsageState.Updating => "...",
			SubscriptionUsageState.Unavailable => "n/a",
			SubscriptionUsageState.Failed => "—",
			_ => "--"
		};
	}

	private static string FormatRemainingTime(DateTimeOffset? resetsAt, DateTimeOffset now)
	{
		if (resetsAt is null)
		{
			return string.Empty;
		}

		var remaining = resetsAt.Value > now ? resetsAt.Value - now : TimeSpan.Zero;
		if (remaining.TotalHours >= 24)
		{
			return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
		}

		return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
	}

	private static string FormatResetText(DateTimeOffset? resetsAt, DateTimeOffset now)
	{
		if (resetsAt is null)
		{
			return string.Empty;
		}

		var localReset = resetsAt.Value.ToLocalTime();
		var localNow = now.ToLocalTime();
		if (resetsAt <= now)
		{
			return localReset.Date == localNow.Date
				? localReset.ToString("'reset' HH:mm", CultureInfo.InvariantCulture)
				: localReset.ToString("'reset' dd.MM HH:mm", CultureInfo.InvariantCulture);
		}

		return localReset.Date == localNow.Date
			? localReset.ToString("HH:mm", CultureInfo.InvariantCulture)
			: localReset.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
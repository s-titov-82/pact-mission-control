using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Platform;

/// <summary>A Windows reminder that can outlive its on-screen banner in Notification Center.</summary>
internal interface IAttentionReminder : IDisposable
{
	/// <summary>Whether the banner is still open or Windows retains it in Notification Center.</summary>
	bool IsOpen { get; }
}

/// <summary>Keeps one outstanding reminder per target and acknowledges a terminal when it is viewed.</summary>
internal sealed class AttentionNotificationList(Func<AttentionItem, AppearancePreferences, IAttentionReminder> show,
	Action<Exception> reportFailure) : IDisposable
{
	private readonly Dictionary<object, (TerminalTabIndicator Reason, IAttentionReminder? Reminder)> _current = [];

	internal void Update(IReadOnlyList<AttentionItem> items, AppearancePreferences preferences, SessionViewModel? viewedSession = null)
	{
		var pending = items.Where(item => item.Indicator is TerminalTabIndicator.InputRequested
			or TerminalTabIndicator.Unread or TerminalTabIndicator.Failed).ToDictionary(item => item.Target);
		foreach (var target in _current.Keys.ToArray())
		{
			if (!preferences.ShowNotifications || !pending.ContainsKey(target))
			{
				Remove(target);
			}
		}
		if (!preferences.ShowNotifications)
		{
			return;
		}
		foreach (var item in pending.Values)
		{
			var target = item.Target;
			var tracked = _current.TryGetValue(target, out var entry);
			if (ReferenceEquals(target, viewedSession))
			{
				if (entry.Reminder is not null)
				{
					Remove(target);
				}
				_current[target] = (item.Indicator, null);
				continue;
			}
			if (tracked && entry.Reason == item.Indicator)
			{
				continue;
			}
			if (entry.Reminder is { } reminder)
			{
				if (IsStillOpen(reminder))
				{
					_current[target] = (item.Indicator, reminder);
					continue;
				}
				Remove(target);
			}

			_current[target] = (item.Indicator, null);
			try
			{
				_current[target] = (item.Indicator, show(item, preferences));
			}
			catch (Exception ex)
			{
				reportFailure(ex);
			}
		}
	}

	private bool IsStillOpen(IAttentionReminder reminder)
	{
		try
		{
			return reminder.IsOpen;
		}
		catch (Exception ex)
		{
			reportFailure(ex);
			return true;
		}
	}

	private void Remove(object target)
	{
		var entry = _current[target];
		_current.Remove(target);
		try
		{
			entry.Reminder?.Dispose();
		}
		catch (Exception ex)
		{
			reportFailure(ex);
		}
	}

	public void Dispose()
	{
		foreach (var target in _current.Keys.ToArray())
		{
			Remove(target);
		}
	}
}

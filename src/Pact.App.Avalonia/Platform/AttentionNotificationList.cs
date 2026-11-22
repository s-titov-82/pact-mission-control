using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Platform;

/// <summary>Mirrors actionable feed entries without replaying a reminder when a title or tooltip changes.</summary>
internal sealed class AttentionNotificationList(Func<AttentionItem, AppearancePreferences, IDisposable> show,
	Action<Exception> reportFailure) : IDisposable
{
	private readonly Dictionary<object, (TerminalTabIndicator Reason, IDisposable? Reminder)> _current = [];

	internal void Update(IReadOnlyList<AttentionItem> items, AppearancePreferences preferences)
	{
		var pending = items.Where(item => item.Indicator is TerminalTabIndicator.InputRequested
			or TerminalTabIndicator.Unread or TerminalTabIndicator.Failed).ToDictionary(item => item.Target);
		foreach (var (target, entry) in _current.ToArray())
		{
			if (!preferences.ShowNotifications || !pending.TryGetValue(target, out var item) || item.Indicator != entry.Reason)
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
			if (_current.TryAdd(item.Target, (item.Indicator, null)))
			{
				try
				{
					_current[item.Target] = (item.Indicator, show(item, preferences));
				}
				catch (Exception ex)
				{
					reportFailure(ex);
				}
			}
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

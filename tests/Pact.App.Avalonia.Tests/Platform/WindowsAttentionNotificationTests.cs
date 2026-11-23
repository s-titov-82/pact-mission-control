using Pact.App.Avalonia.Platform;
using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Platform;

public sealed class WindowsAttentionNotificationTests
{
	[Test]
	public void Feed_updates_only_notify_new_events_and_withdraw_resolved_events()
	{
		List<Reminder> shown = [];
		using var notifications = new AttentionNotificationList((_, _) =>
		{
			Reminder reminder = new();
			shown.Add(reminder);
			return reminder;
		}, ex => Assert.Fail(ex.Message));
		AppearancePreferences disabled = new(AppearanceMode.System);
		var enabled = disabled with { ShowNotifications = true };
		AttentionItem question = new(new object(), null, false, "ROOT", "Agent",
			TerminalTabIndicator.InputRequested, "Waiting");
		var second = question with { Target = new object(), Indicator = TerminalTabIndicator.Unread };
		notifications.Update([question], disabled);
		shown.ShouldBeEmpty();
		notifications.Update([question], enabled);
		notifications.Update([question with { Title = "Renamed" }, second], enabled);
		var failure = question with { Indicator = TerminalTabIndicator.Failed };
		notifications.Update([failure, second], enabled);
		shown.Count.ShouldBe(2);
		shown[0].Closed.ShouldBeFalse();

		shown[0].IsOpen = false;
		notifications.Update([failure, second], enabled);
		shown.Count.ShouldBe(2);
		notifications.Update([question, second], enabled);
		shown.Count.ShouldBe(3);
		shown[0].Closed.ShouldBeTrue();

		notifications.Update([second], enabled);
		shown[2].Closed.ShouldBeTrue();
		shown[1].Closed.ShouldBeFalse();
		notifications.Update([second], disabled);
		shown[1].Closed.ShouldBeTrue();
	}

	[Test]
	public void Viewing_a_terminal_withdraws_only_its_reminder_and_does_not_replay_it_on_leave()
	{
		Dictionary<object, List<Reminder>> shown = [];
		using var notifications = new AttentionNotificationList((item, _) =>
		{
			if (!shown.TryGetValue(item.Target, out var reminders))
			{
				shown[item.Target] = reminders = [];
			}
			Reminder reminder = new();
			reminders.Add(reminder);
			return reminder;
		}, ex => Assert.Fail(ex.Message));
		var now = DateTimeOffset.UtcNow;
		SessionViewModel session = new(new SessionRecord(
			"first", AgentKind.Codex, "Agent", @"C:\repo", "codex", null, SessionStatus.Stopped, now, now));
		var preferences = new AppearancePreferences(AppearanceMode.System) { ShowNotifications = true };
		AttentionItem unread = new(session, null, false, "ROOT", "Agent", TerminalTabIndicator.Unread, "Unread");
		var other = unread with { Target = new object() };

		notifications.Update([unread, other], preferences);
		notifications.Update([unread, other], preferences, viewedSession: session);

		shown[session].Single().Closed.ShouldBeTrue();
		shown[other.Target].Single().Closed.ShouldBeFalse();
		notifications.Update([unread, other], preferences);
		shown[session].Count.ShouldBe(1);

		var question = unread with { Indicator = TerminalTabIndicator.InputRequested };
		notifications.Update([question, other], preferences, viewedSession: session);
		notifications.Update([question, other], preferences);
		shown[session].Count.ShouldBe(1);

		notifications.Update([other], preferences);
		notifications.Update([unread, other], preferences);
		shown[session].Count.ShouldBe(2);
		shown[session][1].Closed.ShouldBeFalse();
	}

	[TestCase(NotificationBannerMode.WindowsDefault, NotificationSoundMode.Silent)]
	[TestCase(NotificationBannerMode.UntilDismissed, NotificationSoundMode.Windows)]
	[TestCase(NotificationBannerMode.CustomTimeout, NotificationSoundMode.Icq)]
	[TestCase(NotificationBannerMode.WindowsDefault, NotificationSoundMode.Station)]
	public void Reminder_button_and_body_open_the_same_event(NotificationBannerMode mode, NotificationSoundMode sound)
	{
		var id = Guid.NewGuid();
		var xml = WindowsAttentionNotification.BuildContent(id, "App & tools", "Waiting",
			new AppearancePreferences(AppearanceMode.System) { NotificationMode = mode, NotificationSoundMode = sound });
		((string?)xml.Attribute("scenario")).ShouldBe(mode == NotificationBannerMode.WindowsDefault ? null : "reminder");
		((string?)xml.Attribute("launch")).ShouldBe(id.ToString("N"));
		var actions = xml.Descendants("action").ToArray();
		((string?)actions[0].Attribute("arguments")).ShouldBe(id.ToString("N"));
		actions.Length.ShouldBe(mode == NotificationBannerMode.UntilDismissed ? 2 : 1);
		if (mode == NotificationBannerMode.UntilDismissed)
		{
			((string?)actions[1].Attribute("activationType")).ShouldBe("system");
			((string?)actions[1].Attribute("arguments")).ShouldBe("dismiss");
		}
		xml.Descendants("text").First().Value.ShouldBe("App & tools");
		var audio = xml.Element("audio")!;
		if (sound == NotificationSoundMode.Windows)
		{
			((string?)audio.Attribute("src")).ShouldBe("ms-winsoundevent:Notification.Default");
		}
		else
		{
			((string?)audio.Attribute("silent")).ShouldBe("true");
		}
	}

	[Test]
	public void Custom_modes_ship_distinct_wave_files()
	{
		WindowsAttentionNotification.CustomSoundPath(NotificationSoundMode.Windows).ShouldBeNull();
		WindowsAttentionNotification.CustomSoundPath(NotificationSoundMode.Silent).ShouldBeNull();
		var icq = File.ReadAllBytes(WindowsAttentionNotification.CustomSoundPath(NotificationSoundMode.Icq)!);
		var station = File.ReadAllBytes(WindowsAttentionNotification.CustomSoundPath(NotificationSoundMode.Station)!);
		System.Text.Encoding.ASCII.GetString(icq, 8, 4).ShouldBe("WAVE");
		System.Text.Encoding.ASCII.GetString(station, 8, 4).ShouldBe("WAVE");
		station.SequenceEqual(icq).ShouldBeFalse();
	}

	[Test]
	public void Click_wakes_only_the_owner_of_the_matching_live_event()
	{
		var id = Guid.NewGuid();
		using EventWaitHandle signal = new(false, EventResetMode.AutoReset, WindowsNotificationActivation.EventName(id));
		WindowsNotificationActivation.Signal(Guid.NewGuid().ToString("N"));
		signal.WaitOne(0).ShouldBeFalse();
		WindowsNotificationActivation.Signal(id.ToString("N"));
		signal.WaitOne(0).ShouldBeTrue();
	}

	private sealed class Reminder : IAttentionReminder
	{
		public bool Closed { get; private set; }
		public bool IsOpen { get; set; } = true;
		public void Dispose() => Closed = true;
	}
}

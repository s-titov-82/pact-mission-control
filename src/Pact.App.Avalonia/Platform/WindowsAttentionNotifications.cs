using System.Xml.Linq;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.WinUI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Pact.App.Avalonia.Platform;

/// <summary>Owns one live Windows reminder and its route back to the originating Pact process.</summary>
internal sealed partial class WindowsAttentionNotification : IDisposable
{
	private readonly Guid _id = Guid.NewGuid();
	private readonly ToastNotifierCompat _notifier;
	private readonly ToastNotification _toast;
	private readonly EventWaitHandle _clicked;
	private readonly RegisteredWaitHandle _wait;
	private readonly DispatcherTimer? _timer;
	private bool _disposed;

	internal WindowsAttentionNotification(string group, string title, string description,
		AppearancePreferences preferences, Action open, Action<Exception> reportFailure)
	{
		WindowsNotificationActivation.Register();
		_notifier = ToastNotificationManagerCompat.CreateToastNotifier();
		var document = new XmlDocument();
		document.LoadXml(BuildContent(_id, title, description, preferences).ToString());
		_toast = new ToastNotification(document) { Tag = _id.ToString("N")[..16], Group = group };
		_clicked = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsNotificationActivation.EventName(_id));
		_wait = ThreadPool.RegisterWaitForSingleObject(_clicked, (_, _) => Dispatcher.UIThread.Post(() =>
		{
			if (!_disposed)
			{
				open();
			}
		}), null, Timeout.Infinite, executeOnlyOnce: true);
		try
		{
			_notifier.Show(_toast);
			if (_notifier.Setting == NotificationSetting.Enabled)
			{
				PlayCustomSound(preferences.NotificationSoundMode);
			}
			if (preferences.NotificationMode == NotificationBannerMode.CustomTimeout)
			{
				_timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(preferences.NotificationSeconds) };
				_timer.Tick += (_, _) =>
				{
					_timer.Stop();
					try
					{
						_notifier.Hide(_toast);
					}
					catch (Exception ex)
					{
						reportFailure(ex);
					}
				};
				_timer.Start();
			}
		}
		catch
		{
			_wait.Unregister(null);
			_clicked.Dispose();
			throw;
		}
	}

	internal static XElement BuildContent(Guid id, string title, string description, AppearancePreferences preferences)
	{
		var route = id.ToString("N");
		return new XElement("toast", preferences.NotificationMode == NotificationBannerMode.WindowsDefault
			? null : new XAttribute("scenario", "reminder"), new XAttribute("launch", route),
			new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
				new XElement("text", title), new XElement("text", description))),
			new XElement("actions", new XElement("action", new XAttribute("content", "Open in Pact"),
				new XAttribute("arguments", route)),
				preferences.NotificationMode == NotificationBannerMode.UntilDismissed
					? new XElement("action", new XAttribute("activationType", "system"),
						new XAttribute("arguments", "dismiss"), new XAttribute("content", ""))
					: null),
			preferences.NotificationSoundMode == NotificationSoundMode.Windows ? new XElement("audio", new XAttribute("src", "ms-winsoundevent:Notification.Default"))
				: new XElement("audio", new XAttribute("silent", "true")));
	}

	internal static string? CustomSoundPath(NotificationSoundMode mode)
	{
		var file = mode switch
		{
			NotificationSoundMode.Icq => "Icq.wav",
			NotificationSoundMode.Station => "Station.wav",
			_ => null
		};
		return file is null ? null : Path.Combine(AppContext.BaseDirectory, "Assets", "NotificationSounds", file);
	}

	private static void PlayCustomSound(NotificationSoundMode mode)
	{
		// The unpackaged desktop app has no ms-appx audio URI. Use the Windows system-sounds
		// mixer for its WAV cues and respect the shell's current notification availability.
		if (CustomSoundPath(mode) is { } path && SHQueryUserNotificationState(out var state) == 0 && state == 5)
		{
			const uint asyncFileInSystemMixer = 0x00000001 | 0x00000002 | 0x00020000 | 0x00200000;
			_ = PlaySoundW(path, IntPtr.Zero, asyncFileInSystemMixer);
		}
	}

	[LibraryImport("shell32.dll")]
	private static partial int SHQueryUserNotificationState(out int state);

	[LibraryImport("winmm.dll", StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool PlaySoundW(string sound, IntPtr module, uint flags);

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_timer?.Stop();
		_wait.Unregister(null);
		_clicked.Dispose();
		ToastNotificationManagerCompat.History.Remove(_toast.Tag, _toast.Group);
	}
}

internal static class WindowsNotificationActivation
{
	private static bool Registered { get; set; }
	internal static string EventName(Guid id) => $@"Local\Pact.Attention.{id:N}";

	internal static void Register()
	{
		if (!Registered)
		{
			ToastNotificationManagerCompat.OnActivated += args => Signal(args.Argument);
			Registered = true;
		}
	}

	internal static void Signal(string argument)
	{
		if (Guid.TryParseExact(argument, "N", out var id)
			&& EventWaitHandle.TryOpenExisting(EventName(id), out var signal))
		{
			using (signal)
			{
				signal.Set();
			}
		}
	}

	// Windows can launch a fresh COM server for a click. Forward it to the live owner
	// before acquiring the profile mutex or creating a second application window.
	internal static int ForwardActivation()
	{
		TaskCompletionSource received = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void Forward(ToastNotificationActivatedEventArgsCompat args)
		{
			Signal(args.Argument);
			received.TrySetResult();
		}
		ToastNotificationManagerCompat.OnActivated += Forward;
		try
		{
			return received.Task.Wait(TimeSpan.FromSeconds(10)) ? 0 : 1;
		}
		finally
		{
			ToastNotificationManagerCompat.OnActivated -= Forward;
		}
	}
}

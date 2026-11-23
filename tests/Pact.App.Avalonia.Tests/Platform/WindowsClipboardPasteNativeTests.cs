using System.Runtime.InteropServices;
using System.Text;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Win32Exception = System.ComponentModel.Win32Exception;

namespace Pact.App.Avalonia.Tests.Platform;

[NonParallelizable]
public sealed partial class WindowsClipboardPasteNativeTests
{
	[Test, Explicit("Run by itself in a Windows test host. The probe uses a separate window station.")]
	[Category("NativeIntegration")]
	public async Task Offers_restore_formats_without_overwriting_a_new_copy_or_serving_previous_text_to_a_late_click()
	{
		var originalStation = GetProcessWindowStation();
		var originalName = ObjectName(originalStation);
		var station = CreateWindowStationW(null, 0, 0x37f, 0);
		Require(station != 0 && Marshal.GetLastPInvokeError() != 183, "Create a separate window station");
		try
		{
			var name = ObjectName(station);
			Require(!name.Equals(originalName, StringComparison.OrdinalIgnoreCase)
				&& !name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase)
				&& SetProcessWindowStation(station) && ObjectName(GetProcessWindowStation()) == name,
				"Switch to the isolated window station");

			TaskCompletionSource finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Thread worker = new(() =>
			{
				try
				{
					ExerciseClipboard();
					finished.SetResult();
				}
				catch (Exception exception)
				{
					finished.SetException(exception);
				}
			})
			{
				IsBackground = true
			};
			worker.Start();
			await finished.Task;
			worker.Join();
		}
		finally
		{
			Require(SetProcessWindowStation(originalStation), "Restore the test host's window station");
			_ = CloseWindowStation(station);
		}
	}

	private static void ExerciseClipboard()
	{
		var desktopName = $"PactClipboardProbe-{Guid.NewGuid():N}";
		var desktop = CreateDesktopW(desktopName, 0, 0, 0, 0x1ff, 0);
		Require(desktop != 0 && (ObjectName(GetThreadDesktop(GetCurrentThreadId())) == desktopName
			|| SetThreadDesktop(desktop)), "Connect to an isolated desktop");
		var window = CreateWindowExW(0, "STATIC", "Clipboard test", 0, 0, 0, 1, 1, 0, 0, 0, 0);
		Require(window != 0, "Create the clipboard owner on the isolated desktop");

		WindowsClipboardPaste? pending = null;
		nint previousProcedure = 0;
		WindowProcedure procedure = (owner, message, format, lParam) =>
			message == 0x0305 && pending?.TryRenderText(owner, format) == true
				? 0 : CallWindowProcW(previousProcedure, owner, message, format, lParam);
		previousProcedure = SetWindowLongPtrW(window, -4, Marshal.GetFunctionPointerForDelegate(procedure));
		Require(previousProcedure != 0, "Install the isolated clipboard window procedure");
		try
		{
			var html = RegisterClipboardFormatW("HTML Format");
			var excluded = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
			Seed(window, "prior text", html, "sample markup");
			var first = Offer("temporary request");
			first.TextRequested.IsCompleted.ShouldBeFalse();
			HasFormat(window, excluded).ShouldBeTrue();
			first.MarkPasteTriggered();
			Read(window, 13).ShouldBe("temporary request");
			first.TextRequested.IsCompletedSuccessfully.ShouldBeTrue();
			first.DisposeAsync().AsTask().GetAwaiter().GetResult();
			Read(window, 13).ShouldBe("prior text");
			Read(window, html).ShouldBe("sample markup");

			var late = Offer("late request");
			late.MarkPasteTriggered();
			late.DisposeAsync().AsTask().GetAwaiter().GetResult();
			late.IsCurrent.ShouldBeTrue();
			late.TextRequested.IsCompleted.ShouldBeFalse();
			Read(window, 13).ShouldBe("late request");
			late.RestoreOnWindowClosing(window);
			Read(window, 13).ShouldBe("prior text");

			var next = Offer("another request");
			Seed(window, "new copy");
			next.DisposeAsync().AsTask().GetAwaiter().GetResult();
			Read(window, 13).ShouldBe("new copy");

			var opaque = RegisterClipboardFormatW("Pact Probe Opaque");
			Seed(window, "current text", opaque, "opaque content");
			WindowsClipboardPaste.TryOffer(window, "different", new InlineDispatcher(),
				offer => pending = offer, out _).ShouldBeNull();
			Read(window, 13).ShouldBe("current text");
			Read(window, opaque).ShouldBe("opaque content");
		}
		finally
		{
			_ = DestroyWindow(window);
			GC.KeepAlive(procedure);
		}

		WindowsClipboardPaste Offer(string text)
		{
			return WindowsClipboardPaste.TryOffer(window, text, new InlineDispatcher(), offer => pending = offer, out _)
				.ShouldNotBeNull();
		}
	}

	private static void Seed(nint window, string text, uint extraFormat = 0, string? extraText = null)
	{
		Require(OpenClipboard(window), "Open the isolated clipboard for seeding");
		try
		{
			Require(EmptyClipboard(), "Empty the isolated clipboard");
			using var content = WindowsClipboardPaste.Buffer.Create(13, Encoding.Unicode.GetBytes(text + '\0'));
			Require(content?.Publish() == true, "Publish seed text");
			if (extraFormat != 0)
			{
				using var extra = WindowsClipboardPaste.Buffer.Create(extraFormat, Encoding.Unicode.GetBytes(extraText + '\0'));
				Require(extra?.Publish() == true, "Publish a second seed format");
			}
		}
		finally
		{
			_ = CloseClipboard();
		}
	}

	private static string? Read(nint window, uint format)
	{
		Require(OpenClipboard(window), "Open the isolated clipboard for reading");
		try
		{
			var handle = GetClipboardData(format);
			var memory = GlobalLock(handle);
			if (memory == 0)
			{
				return null;
			}
			try
			{
				return Marshal.PtrToStringUni(memory);
			}
			finally
			{
				_ = GlobalUnlock(handle);
			}
		}
		finally
		{
			_ = CloseClipboard();
		}
	}

	private static bool HasFormat(nint window, uint format)
	{
		Require(OpenClipboard(window), "Open the isolated clipboard to inspect formats");
		try
		{
			return IsClipboardFormatAvailable(format);
		}
		finally
		{
			_ = CloseClipboard();
		}
	}

	private static void Require(bool condition, string description)
	{
		if (!condition)
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), description);
		}
	}

	private static unsafe string ObjectName(nint handle)
	{
		var buffer = stackalloc char[256];
		Require(GetUserObjectInformationW(handle, 2, buffer, 512, out _), "Read the Win32 object's name");
		return new string(buffer);
	}

	private sealed class InlineDispatcher : IUiTaskDispatcher
	{
		private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

		public void Post(Action action) => action();
		public Task InvokeAsync(Func<Task> operation) =>
			Environment.CurrentManagedThreadId == _ownerThreadId ? operation() : Task.CompletedTask;
	}

	private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

	[LibraryImport("user32.dll")] private static partial nint GetProcessWindowStation();
	[LibraryImport("user32.dll")] private static partial nint GetThreadDesktop(uint threadId);
	[LibraryImport("kernel32.dll")] private static partial uint GetCurrentThreadId();
	[LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateWindowStationW(string? name, uint flags, uint access, nint attributes);
	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetProcessWindowStation(nint station);
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseWindowStation(nint station);
	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static unsafe partial bool GetUserObjectInformationW(nint handle, int index, char* information, uint size, out uint needed);
	[LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateDesktopW(string name, nint device, nint mode, uint flags, uint access, nint attributes);
	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool SetThreadDesktop(nint desktop);
	[LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial nint CreateWindowExW(uint extendedStyle, string type, string name, uint style,
		int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint window);
	[LibraryImport("user32.dll", SetLastError = true)] private static partial nint SetWindowLongPtrW(nint window, int index, nint value);
	[LibraryImport("user32.dll")] private static partial nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);
	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(nint window);
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
	[LibraryImport("user32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
	[LibraryImport("user32.dll")] private static partial nint GetClipboardData(uint format);
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsClipboardFormatAvailable(uint format);
	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial uint RegisterClipboardFormatW(string format);
	[LibraryImport("kernel32.dll")] private static partial nint GlobalLock(nint handle);
	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(nint handle);
}

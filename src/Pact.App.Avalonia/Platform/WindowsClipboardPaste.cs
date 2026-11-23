using System.Runtime.InteropServices;
using System.Text;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Platform;

/// <summary>Temporarily places prompt text on the Windows clipboard and restores owned formats.</summary>
internal sealed partial class WindowsClipboardPaste(
	nint window, uint sequence, List<WindowsClipboardPaste.Buffer>? original, IUiTaskDispatcher dispatcher,
	uint temporaryFormat = 0, nint delayedText = 0)
	: IClipboardPasteLease
{
	private const uint UnicodeText = 13;
	private const uint Bitmap = 2;
	private const uint Palette = 9;
	private const uint MoveableMemory = 0x0002;
	private const int SnapshotByteLimit = 64 * 1024 * 1024;
	private const string ExcludeFromHistory = "ExcludeClipboardContentFromMonitorProcessing";
	private const string TemporaryPrompt = "Pact.TemporaryPromptPaste";
	private List<Buffer>? _original = original;
	private nint _delayedText = delayedText;
	private int _restorationStarted;
	private bool _pasteTriggered;
	private readonly TaskCompletionSource? _textRequested = delayedText == 0
		? null : new(TaskCreationOptions.RunContinuationsAsynchronously);

	/// <inheritdoc />
	public bool IsCurrent => temporaryFormat == 0
		? GetClipboardSequenceNumber() == sequence
		// Rendering the delayed text can increment the sequence without replacing the offer.
		: GetClipboardOwner() == window && IsClipboardFormatAvailable(temporaryFormat);

	/// <inheritdoc />
	public Task TextRequested => _textRequested?.Task ?? Task.CompletedTask;

	/// <inheritdoc />
	public void MarkPasteTriggered() => _pasteTriggered = true;

	/// <summary>Reuses matching text, or saves the clipboard before publishing temporary text.</summary>
	internal static WindowsClipboardPaste? TryOffer(
		nint window, string text, IUiTaskDispatcher dispatcher, Action<WindowsClipboardPaste> registerRenderer,
		out bool clipboardBusy)
	{
		clipboardBusy = !OpenClipboard(window);
		if (clipboardBusy)
		{
			return null;
		}

		List<Buffer>? storage = [];
		try
		{
			var promptFormat = RegisterClipboardFormatW(TemporaryPrompt);
			if (promptFormat == 0 || IsClipboardFormatAvailable(promptFormat))
			{
				// Another Pact process may be pasting. Its prompt is not a restore snapshot.
				return null;
			}

			var current = ReadUnicodeText();
			if (current is not null && NormalizeLineEndings(current) == NormalizeLineEndings(text))
			{
				return new(window, GetClipboardSequenceNumber(), null, dispatcher);
			}

			var exclusionFormat = RegisterClipboardFormatW(ExcludeFromHistory);
			if (exclusionFormat == 0 || !TryCapture(storage, exclusionFormat))
			{
				return null;
			}

			using var exclusion = Buffer.Create(exclusionFormat, [0]);
			using var marker = Buffer.Create(promptFormat, [0]);
			using var content = Buffer.Create(UnicodeText, Encoding.Unicode.GetBytes(text + '\0'));
			if (exclusion is null || marker is null || content is null || !EmptyClipboard())
			{
				return null;
			}

			// Attach the exclusion before publishing the prompt in the same clipboard operation.
			if (!exclusion.Publish() || !marker.Publish())
			{
				RestoreOpenClipboard(storage);
				return null;
			}

			// A repaint can show an old paste marker before the client handles the right click.
			// A delayed Unicode format gives independent evidence that someone requested the data.
			_ = SetClipboardData(UnicodeText, 0);
			if (!IsClipboardFormatAvailable(UnicodeText))
			{
				RestoreOpenClipboard(storage);
				return null;
			}
			var offer = new WindowsClipboardPaste(window, 0, storage, dispatcher, promptFormat, content.TakeHandle());
			registerRenderer(offer);
			storage = null;
			return offer;
		}
		finally
		{
			_ = CloseClipboard();
			storage?.ForEach(static item => item.Dispose());
		}
	}

	/// <summary>Renders the promised Unicode format in response to WM_RENDERFORMAT.</summary>
	internal bool TryRenderText(nint owner, nint format)
	{
		if (owner != window || format != (nint)UnicodeText)
		{
			return false;
		}
		var content = Interlocked.Exchange(ref _delayedText, 0);
		if (content == 0)
		{
			return false;
		}
		if (SetClipboardData(UnicodeText, content) != 0)
		{
			_textRequested!.TrySetResult();
		}
		else
		{
			_ = GlobalFree(content);
		}
		return true;
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
		Justification = "The caller owns the snapshot and disposes each buffer that Windows does not take.")]
	private static unsafe bool TryCapture(List<Buffer> storage, uint exclusionFormat)
	{
		var remainingBytes = (nuint)SnapshotByteLimit;
		var hadOleMetadata = false;
		Span<char> formatName = stackalloc char[256];
		uint format = 0;
		while ((format = EnumClipboardFormats(format)) != 0)
		{
			if (format >= 0xC000)
			{
				int nameLength;
				fixed (char* nameBuffer = formatName)
				{
					nameLength = GetClipboardFormatNameW(format, nameBuffer, formatName.Length);
				}
				var name = formatName[..nameLength];
				if (name.Equals("Ole Private Data", StringComparison.OrdinalIgnoreCase)
					|| name.Equals("DataObject", StringComparison.OrdinalIgnoreCase))
				{
					// These refer to the old OLE owner. Copy the formats it actually renders.
					hadOleMetadata = true;
					continue;
				}
				if (!IsStandaloneFormat(name))
				{
					// Shell and OLE objects can rely on a live source even if some formats
					// render to bytes (for example Explorer's cut-and-paste callbacks).
					return false;
				}
			}
			else if (format is not (1 or Bitmap or 7 or 8 or Palette or UnicodeText or 16 or 17))
			{
				return false;
			}

			var source = GetClipboardData(format);
			var isGdi = format is Bitmap or Palette;
			var size = isGdi ? 0 : GlobalSize(source);
			if (source == 0 || (!isGdi && size == 0) || size > remainingBytes)
			{
				return false;
			}

			var copy = OleDuplicateData(source, (ushort)format, MoveableMemory);
			if (copy == 0)
			{
				return false;
			}
			remainingBytes -= size;
			storage.Add(new Buffer(format, copy));
		}

		if (Marshal.GetLastPInvokeError() != 0 || (hadOleMetadata && storage.Count == 0))
		{
			return false;
		}
		// A republished snapshot is maintenance, so it should not add a duplicate history item.
		if (storage.Count != 0 && storage.All(item => item.Format != exclusionFormat))
		{
			if (Buffer.Create(exclusionFormat, [0]) is not { } exclusion)
			{
				return false;
			}
			storage.Insert(0, exclusion);
		}
		return true;
	}

	private static bool IsStandaloneFormat(ReadOnlySpan<char> name) =>
		name.Equals("HTML Format", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("Rich Text Format", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("PNG", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("JFIF", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("GIF", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("CanIncludeInClipboardHistory", StringComparison.OrdinalIgnoreCase)
		|| name.Equals("CanUploadToCloudClipboard", StringComparison.OrdinalIgnoreCase)
		|| name.Equals(ExcludeFromHistory, StringComparison.OrdinalIgnoreCase);

	private static string? ReadUnicodeText()
	{
		var handle = GetClipboardData(UnicodeText);
		var size = GlobalSize(handle);
		if (size < sizeof(char) || size > SnapshotByteLimit)
		{
			return null;
		}
		var pointer = GlobalLock(handle);
		if (pointer == 0)
		{
			return null;
		}
		try
		{
			var text = Marshal.PtrToStringUni(pointer, (int)(size / sizeof(char)));
			var end = text.IndexOf('\0', StringComparison.Ordinal);
			return end >= 0 ? text[..end] : null;
		}
		finally
		{
			_ = GlobalUnlock(handle);
		}
	}

	private static string NormalizeLineEndings(string text) =>
		text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

	/// <inheritdoc />
	public async ValueTask DisposeAsync()
	{
		if (_original is null || Interlocked.Exchange(ref _restorationStarted, 1) != 0)
		{
			return;
		}
		var restoration = RestoreEventuallyAsync();
		if (_pasteTriggered && !TextRequested.IsCompletedSuccessfully)
		{
			// A late right click must still read the offered text. The prior clipboard
			// might contain something completely unrelated to the terminal.
			return;
		}
		try
		{
			await restoration.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
		}
		catch (TimeoutException)
		{
			// A foreign window can hold the clipboard past the delivery budget. Keep the
			// only snapshot until that lock goes away or a newer copy takes ownership.
		}
	}

	/// <summary>Restores a pending offer on a normal shell close after sessions have stopped.</summary>
	internal void RestoreOnWindowClosing(nint owner)
	{
		if (owner == window && _original is not null)
		{
			try
			{
				_ = TryRestore();
			}
			catch (Exception exception)
			{
				ReportRestorationFailure(exception);
			}
		}
	}

	private async Task RestoreEventuallyAsync()
	{
		try
		{
			for (var delay = 25; IsCurrent && IsWindow(window); delay = Math.Min(delay * 2, 250))
			{
				if (!_pasteTriggered || TextRequested.IsCompletedSuccessfully)
				{
					var finished = false;
					await dispatcher.InvokeAsync(() =>
					{
						finished = TryRestore();
						return Task.CompletedTask;
					}).ConfigureAwait(false);
					if (finished)
					{
						return;
					}
				}
				await Task.Delay(delay).ConfigureAwait(false);
			}
		}
		catch (Exception exception)
		{
			ReportRestorationFailure(exception);
		}
		finally
		{
			var unrendered = Interlocked.Exchange(ref _delayedText, 0);
			if (unrendered != 0)
			{
				_ = GlobalFree(unrendered);
			}
			_textRequested?.TrySetCanceled();
			Interlocked.Exchange(ref _original, null)?.ForEach(static item => item.Dispose());
		}
	}

	private static void ReportRestorationFailure(Exception exception) =>
		System.Diagnostics.Trace.TraceError("Pact clipboard restoration failed: {0}", exception.Message);

	private bool TryRestore()
	{
		if (!IsCurrent)
		{
			return true;
		}
		if (!OpenClipboard(window))
		{
			return false;
		}
		try
		{
			// Test ownership again with the clipboard locked, so a new copy is never overwritten.
			if (IsCurrent && Interlocked.Exchange(ref _original, null) is { } snapshot)
			{
				try
				{
					RestoreOpenClipboard(snapshot);
				}
				finally
				{
					snapshot.ForEach(static item => item.Dispose());
				}
			}
			return true;
		}
		finally
		{
			_ = CloseClipboard();
		}
	}

	private static void RestoreOpenClipboard(List<Buffer> snapshot)
	{
		if (!EmptyClipboard())
		{
			throw new InvalidOperationException("Windows could not clear the temporary clipboard contents.");
		}
		var complete = true;
		foreach (var item in snapshot)
		{
			complete &= item.Publish();
		}
		if (!complete)
		{
			throw new InvalidOperationException("Windows did not restore all previous clipboard formats.");
		}
	}

	internal sealed class Buffer(uint format, nint handle) : IDisposable
	{
		internal uint Format => format;

		internal nint TakeHandle()
		{
			var taken = handle;
			handle = 0;
			return taken;
		}

		internal static Buffer? Create(uint format, byte[] data)
		{
			var memory = GlobalAlloc(MoveableMemory, (nuint)data.Length);
			if (memory == 0)
			{
				return null;
			}
			var pointer = GlobalLock(memory);
			if (pointer == 0)
			{
				_ = GlobalFree(memory);
				return null;
			}
			try
			{
				Marshal.Copy(data, 0, pointer, data.Length);
			}
			finally
			{
				_ = GlobalUnlock(memory);
			}
			return new Buffer(format, memory);
		}

		internal bool Publish()
		{
			if (SetClipboardData(format, handle) == 0)
			{
				return false;
			}
			handle = 0; // Windows owns the handle.
			return true;
		}

		/// <inheritdoc />
		public void Dispose()
		{
			if (handle == 0)
			{
				return;
			}
			if (format is Bitmap or Palette)
			{
				_ = DeleteObject(handle);
			}
			else
			{
				_ = GlobalFree(handle);
			}
			handle = 0;
		}
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool OpenClipboard(nint window);
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseClipboard();
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool EmptyClipboard();
	[LibraryImport("user32.dll")]
	private static partial uint GetClipboardSequenceNumber();
	[LibraryImport("user32.dll")]
	private static partial nint GetClipboardOwner();
	[LibraryImport("user32.dll", SetLastError = true)]
	private static partial uint EnumClipboardFormats(uint format);
	[LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
	private static partial uint RegisterClipboardFormatW(string format);
	[LibraryImport("user32.dll")]
	private static unsafe partial int GetClipboardFormatNameW(uint format, char* name, int maxCount);
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsClipboardFormatAvailable(uint format);
	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsWindow(nint window);
	[LibraryImport("user32.dll")]
	private static partial nint GetClipboardData(uint format);
	[LibraryImport("user32.dll")]
	private static partial nint SetClipboardData(uint format, nint memory);
	[LibraryImport("kernel32.dll")]
	private static partial nint GlobalAlloc(uint flags, nuint size);
	[LibraryImport("kernel32.dll")]
	private static partial nuint GlobalSize(nint memory);
	[LibraryImport("kernel32.dll")]
	private static partial nint GlobalLock(nint memory);
	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GlobalUnlock(nint memory);
	[LibraryImport("kernel32.dll")]
	private static partial nint GlobalFree(nint memory);
	[LibraryImport("ole32.dll")]
	private static partial nint OleDuplicateData(nint source, ushort format, uint flags);
	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteObject(nint handle);
}

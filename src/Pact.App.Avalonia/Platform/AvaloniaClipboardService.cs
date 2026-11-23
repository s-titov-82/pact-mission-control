using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Platform;

internal sealed partial class AvaloniaClipboardService : IClipboardService
{
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly Func<Task<string>> _readTextAsync;
	private readonly Func<string, Task> _setTextAsync;
	private TopLevel? _pasteWindow;
	private WindowsClipboardPaste? _renderingPaste;

	public AvaloniaClipboardService(IUiTaskDispatcher uiTaskDispatcher)
		: this(uiTaskDispatcher, ReadTextCoreAsync, SetTextCoreAsync)
	{
	}

	internal AvaloniaClipboardService(
		IUiTaskDispatcher uiTaskDispatcher,
		Func<Task<string>> readTextAsync,
		Func<string, Task> setTextAsync)
	{
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
		_readTextAsync = readTextAsync ?? throw new ArgumentNullException(nameof(readTextAsync));
		_setTextAsync = setTextAsync ?? throw new ArgumentNullException(nameof(setTextAsync));
	}

	public uint ChangeSequence => GetClipboardSequenceNumber();

	public async Task<IReadOnlyList<string>> GetFilePathsAsync()
	{
		try
		{
			IReadOnlyList<string> paths = [];
			await _uiTaskDispatcher.InvokeAsync(async () =>
			{
				if (GetTopLevel()?.Clipboard is not { } clipboard)
				{ return; }
				var files = await clipboard.TryGetFilesAsync();
				paths = files?.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray() ?? [];
			});
			return paths;
		}
		catch { return []; }
	}

	public async Task<string> GetTextAsync()
	{
		try
		{
			var text = string.Empty;
			await _uiTaskDispatcher.InvokeAsync(async () => text = await _readTextAsync());
			return text;
		}
		catch
		{
			return string.Empty;
		}
	}

	public async Task<bool> TrySetTextAsync(string text)
	{
		try
		{
			await _uiTaskDispatcher.InvokeAsync(() => _setTextAsync(text));
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <inheritdoc />
	public async Task<IClipboardPasteLease?> TryOfferPasteTextAsync(string text)
	{
		nint handle = 0;
		await _uiTaskDispatcher.InvokeAsync(() =>
		{
			var topLevel = GetTopLevel();
			if (topLevel?.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } window)
			{
				handle = window.Handle;
				if (!ReferenceEquals(_pasteWindow, topLevel))
				{
					if (_pasteWindow is not null)
					{
						Win32Properties.RemoveWndProcHookCallback(_pasteWindow, OnClipboardMessage);
					}
					Win32Properties.AddWndProcHookCallback(topLevel, OnClipboardMessage);
					_pasteWindow = topLevel;
				}
			}
			return Task.CompletedTask;
		}).ConfigureAwait(false);
		if (handle == 0)
		{
			return null;
		}

		for (var attempt = 0; attempt < 6; attempt++)
		{
			// A delayed format may call back into its original owner. Keep that wait off the UI thread.
			var (paste, clipboardBusy) = await Task.Run(() =>
			{
				var offer = WindowsClipboardPaste.TryOffer(handle, text, _uiTaskDispatcher,
					pending => Volatile.Write(ref _renderingPaste, pending), out var busy);
				return (offer, busy);
			}).ConfigureAwait(false);
			if (!clipboardBusy)
			{
				return paste;
			}
			await Task.Delay(25).ConfigureAwait(false);
		}
		return null;
	}

	private nint OnClipboardMessage(nint window, uint message, nint format, nint lParam, ref bool handled)
	{
		const uint renderFormat = 0x0305;
		const uint renderAllFormats = 0x0306;
		if (message == renderFormat && Volatile.Read(ref _renderingPaste)?.TryRenderText(window, format) == true)
		{
			handled = true;
		}
		else if (message == renderAllFormats)
		{
			Volatile.Read(ref _renderingPaste)?.RestoreOnWindowClosing(window);
		}
		return 0;
	}

	/// <inheritdoc />
	public Task RestorePendingPasteAsync() => _uiTaskDispatcher.InvokeAsync(() =>
	{
		if (_pasteWindow?.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } owner)
		{
			Volatile.Read(ref _renderingPaste)?.RestoreOnWindowClosing(owner.Handle);
		}
		return Task.CompletedTask;
	});

	private static async Task<string> ReadTextCoreAsync()
	{
		var clipboard = GetTopLevel()?.Clipboard;
		return clipboard is null
			? string.Empty
			: await clipboard.TryGetTextAsync() ?? string.Empty;
	}

	private static Task SetTextCoreAsync(string text)
	{
		var clipboard = GetTopLevel()?.Clipboard
			?? throw new InvalidOperationException("The application clipboard is unavailable.");
		return clipboard.SetTextAsync(text);
	}

	[LibraryImport("user32.dll")]
	private static partial uint GetClipboardSequenceNumber();

	private static TopLevel? GetTopLevel()
	{
		var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
		return window is null ? null : TopLevel.GetTopLevel(window) ?? window;
	}
}

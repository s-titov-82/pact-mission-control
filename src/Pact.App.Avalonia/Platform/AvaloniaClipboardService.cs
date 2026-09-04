using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Platform;

internal sealed class AvaloniaClipboardService : IClipboardService
{
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly Func<Task<string>> _readTextAsync;
	private readonly Func<string, Task> _setTextAsync;

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

	private static TopLevel? GetTopLevel()
	{
		var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
		return window is null ? null : TopLevel.GetTopLevel(window) ?? window;
	}
}

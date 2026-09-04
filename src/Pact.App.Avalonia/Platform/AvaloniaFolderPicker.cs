using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Platform;

internal sealed class AvaloniaFolderPicker : IFolderPicker
{
	public async Task<string?> PickFolderAsync(string? initialDirectory, string title)
	{
		var topLevel = GetTopLevel();
		if (topLevel?.StorageProvider is not { } storageProvider)
		{
			return null;
		}

		IStorageFolder? suggestedStart = null;
		if (!string.IsNullOrWhiteSpace(initialDirectory))
		{
			suggestedStart = await storageProvider.TryGetFolderFromPathAsync(
				new Uri(Path.GetFullPath(initialDirectory)));
		}

		var folders = await storageProvider.OpenFolderPickerAsync(
			new FolderPickerOpenOptions
			{
				Title = title,
				AllowMultiple = false,
				SuggestedStartLocation = suggestedStart
			});
		return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
	}

	private static TopLevel? GetTopLevel()
	{
		var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
		return window is null ? null : TopLevel.GetTopLevel(window) ?? window;
	}
}
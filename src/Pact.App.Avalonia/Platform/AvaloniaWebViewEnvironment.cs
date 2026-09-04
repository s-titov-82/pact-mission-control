using Avalonia.Controls;
using Avalonia.Platform;

namespace Pact.App.Avalonia.Platform;

internal sealed record AvaloniaWebViewEnvironmentLayout(
	string TerminalUserDataFolder,
	string TerminalProfileName,
	string BrowserUserDataFolder,
	string? BrowserProfileName)
{
	public static AvaloniaWebViewEnvironmentLayout Create(string webViewDataRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webViewDataRoot);
		return new(
			webViewDataRoot,
			"PactTerminal",
			webViewDataRoot,
			null);
	}
}

internal static class AvaloniaWebViewEnvironment
{
	private static readonly string[] RequiredBrowserArguments =
	[
		"--disable-background-timer-throttling",
		"--disable-renderer-backgrounding",
		"--disable-backgrounding-occluded-windows",
	];

	public static void Configure(NativeWebView webView, string userDataFolder, string? profileName)
	{
		ArgumentNullException.ThrowIfNull(webView);
		ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);

		webView.EnvironmentRequested += (_, args) =>
		{
			if (args is not WindowsWebView2EnvironmentRequestedEventArgs windows)
			{
				return;
			}

			windows.UserDataFolder = userDataFolder;
			windows.AdditionalBrowserArguments = MergeAdditionalBrowserArguments(
				windows.AdditionalBrowserArguments);
			if (profileName is not null)
			{
				windows.ProfileName = profileName;
			}
		};
	}

	internal static string MergeAdditionalBrowserArguments(string? existingArguments)
	{
		var preserved = existingArguments?.Trim() ?? string.Empty;
		var existingTokens = preserved
			.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.ToHashSet(StringComparer.Ordinal);
		var missing = RequiredBrowserArguments
			.Where(argument => !existingTokens.Contains(argument))
			.ToArray();

		return preserved.Length == 0
			? string.Join(' ', missing)
			: missing.Length == 0
				? preserved
				: $"{preserved} {string.Join(' ', missing)}";
	}
}
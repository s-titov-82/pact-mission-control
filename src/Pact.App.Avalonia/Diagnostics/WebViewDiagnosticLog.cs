namespace Pact.App.Avalonia.Diagnostics;

/// <summary>
/// Persists WebView lifecycle phases into the bounded application log, so a browser tab that ends
/// up in a bad native state can be diagnosed after the in-memory trace is gone.
/// </summary>
internal static class WebViewDiagnosticLog
{
	/// <summary>
	/// Creates a best-effort sink writing one line per entry into the supplied Logs directory.
	/// </summary>
	public static Action<WebViewDiagnosticEntry> CreateSink(string logsDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
		RotatingAppLog log = new(logsDirectory);
		return entry => _ = log.AppendAsync(Format(entry));
	}

	internal static string Format(WebViewDiagnosticEntry entry)
	{
		ArgumentNullException.ThrowIfNull(entry);
		var detail = string.IsNullOrEmpty(entry.Detail) ? string.Empty : " " + entry.Detail;
		return FormattableString.Invariant(
			$"webview {entry.Host} #{entry.Sequence} {entry.Phase} uiThread={entry.IsUiThread} visible={Describe(entry.IsVisible)} attached={Describe(entry.IsAttached)} handle={Describe(entry.HasPlatformHandle)}{detail}");
	}

	private static string Describe(bool? value) =>
		value is null ? "n/a" : value.Value ? "True" : "False";
}

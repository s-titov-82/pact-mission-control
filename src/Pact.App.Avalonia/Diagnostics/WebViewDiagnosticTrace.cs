namespace Pact.App.Avalonia.Diagnostics;

internal sealed record WebViewDiagnosticEntry(
	long Sequence,
	DateTimeOffset Timestamp,
	string Host,
	string Phase,
	bool IsUiThread,
	bool? IsVisible,
	bool? IsAttached,
	bool? HasPlatformHandle,
	string? Detail);

internal sealed class WebViewDiagnosticTrace
{
	private readonly Lock _sync = new();
	private readonly List<WebViewDiagnosticEntry> _entries = [];
	private readonly string _host;
	private readonly Action<WebViewDiagnosticEntry>? _sink;
	private long _sequence;

	/// <summary>
	/// Creates a trace. The optional sink receives every recorded entry as it happens, so a session
	/// that ends in a bad native state can still be diagnosed after the in-memory trace is gone.
	/// </summary>
	public WebViewDiagnosticTrace(string host, Action<WebViewDiagnosticEntry>? sink = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(host);
		_host = host;
		_sink = sink;
	}

	public void Record(
		string phase,
		bool isUiThread,
		bool? isVisible,
		bool? isAttached,
		bool? hasPlatformHandle,
		string? detail = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(phase);
		WebViewDiagnosticEntry entry;
		lock (_sync)
		{
			entry = new WebViewDiagnosticEntry(
				++_sequence,
				DateTimeOffset.UtcNow,
				_host,
				phase,
				isUiThread,
				isVisible,
				isAttached,
				hasPlatformHandle,
				detail);
			_entries.Add(entry);
		}

		_sink?.Invoke(entry);
	}

	public WebViewDiagnosticEntry[] Snapshot()
	{
		lock (_sync)
		{
			return _entries.ToArray();
		}
	}
}
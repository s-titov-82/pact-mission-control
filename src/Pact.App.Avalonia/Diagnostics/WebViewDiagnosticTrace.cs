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
	/// <summary>
	/// How many of the most recent entries stay in memory. Hosts record every web message, so an
	/// unbounded trace would grow for the whole life of the process.
	/// </summary>
	internal const int Capacity = 1024;

	private readonly Lock _sync = new();
	private readonly Queue<WebViewDiagnosticEntry> _entries = new(Capacity);
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
			if (_entries.Count == Capacity)
			{
				_entries.Dequeue();
			}

			_entries.Enqueue(entry);
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
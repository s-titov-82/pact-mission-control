namespace Pact.App.Avalonia.Web;

/// <summary>
/// The terminal size each session's process should have. A new terminal takes the size last
/// fitted for any terminal, because they all stack in one element; the page is told that size
/// rather than choosing one, so the process never starts from a size the page has not reported yet.
/// </summary>
internal sealed class TerminalSizeBook((int Columns, int Rows) defaultSize)
{
	private readonly Dictionary<string, (int Columns, int Rows)> _sizes = new(StringComparer.Ordinal);
	private (int Columns, int Rows)? _lastFitted;

	/// <summary>Picks and records the size a new terminal is created at.</summary>
	public (int Columns, int Rows) Reserve(string sessionId)
	{
		lock (_sizes)
		{
			var size = _lastFitted ?? defaultSize;
			_sizes[sessionId] = size;
			return size;
		}
	}

	/// <summary>Records a size the page fitted a terminal to.</summary>
	public void Record(string sessionId, int columns, int rows)
	{
		lock (_sizes)
		{
			_sizes[sessionId] = (columns, rows);
			_lastFitted = (columns, rows);
		}
	}

	/// <summary>The session's size, or the default for a terminal never created.</summary>
	public (int Columns, int Rows) Get(string sessionId)
	{
		lock (_sizes)
		{
			return _sizes.TryGetValue(sessionId, out var size) ? size : defaultSize;
		}
	}

	/// <summary>Forgets a disposed terminal.</summary>
	public void Remove(string sessionId)
	{
		lock (_sizes)
		{
			_sizes.Remove(sessionId);
		}
	}
}

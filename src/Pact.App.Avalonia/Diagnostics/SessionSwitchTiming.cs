using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pact.App.Avalonia.Diagnostics;

/// <summary>
/// Records how long each awaited stage of one session switch took, so a slow switch can be
/// attributed to the stage that waited rather than guessed at from CPU traces.
/// </summary>
internal sealed class SessionSwitchTiming
{
	/// <summary>Switches at least this long are worth a log line.</summary>
	internal static readonly TimeSpan SlowThreshold = TimeSpan.FromMilliseconds(300);

	private readonly long _started = Stopwatch.GetTimestamp();
	private readonly List<(string Stage, long At)> _marks = [];
	private long _last;

	public SessionSwitchTiming()
	{
		_last = _started;
	}

	public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_started);

	/// <summary>Closes the stage that ends now under <paramref name="stage"/>.</summary>
	public void Mark(string stage)
	{
		var now = Stopwatch.GetTimestamp();
		_marks.Add((stage, now - _last));
		_last = now;
	}

	/// <summary>Formats each closed stage as <c>name=milliseconds</c>.</summary>
	public string Describe()
	{
		StringBuilder builder = new();
		foreach (var (stage, ticks) in _marks)
		{
			if (builder.Length > 0)
			{
				builder.Append(' ');
			}

			builder.Append(stage)
				.Append('=')
				.Append((ticks * 1000.0 / Stopwatch.Frequency).ToString("F0", CultureInfo.InvariantCulture));
		}

		return builder.ToString();
	}
}

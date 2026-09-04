using Pact.Core.Platform;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed class SelectedProcessMetricsMonitor : IDisposable
{
	internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

	private readonly IProcessTreeSnapshotReader _reader;
	private readonly TimeProvider _timeProvider;
	private readonly int _processorCount;
	private readonly Lock _gate = new();
	private ITimer? _timer;
	private ProcessTreeSnapshot? _previous;
	private int? _rootProcessId;
	private int _generation;
	private int _readInProgress;
	private bool _disposed;

	internal SelectedProcessMetricsMonitor(
		IProcessTreeSnapshotReader reader,
		TimeProvider timeProvider,
		int? processorCount = null)
	{
		_reader = reader ?? throw new ArgumentNullException(nameof(reader));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
		_processorCount = processorCount ?? Environment.ProcessorCount;
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_processorCount);
	}

	internal event EventHandler? MetricsChanged;

	internal ProcessTreeMetricsViewModel? Current { get; private set; }

	internal void SetTarget(int? rootProcessId, bool enabled)
	{
		var nextRoot = enabled && rootProcessId > 0 ? rootProcessId : null;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_rootProcessId == nextRoot)
			{
				return;
			}

			_timer?.Dispose();
			_timer = null;
			_rootProcessId = nextRoot;
			_previous = null;
			Current = null;
			var generation = ++_generation;
			if (nextRoot is { } processId)
			{
				_timer = _timeProvider.CreateTimer(
					static state =>
					{
						var callback = (TimerState)state!;
						callback.Owner.Read(processId: callback.ProcessId, callback.Generation);
					},
					new TimerState(this, processId, generation),
					TimeSpan.Zero,
					PollInterval);
			}
		}
	}

	private void Read(int processId, int generation)
	{
		if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
		{
			return;
		}

		try
		{
			ProcessTreeSnapshot? previous;
			lock (_gate)
			{
				if (_disposed || _generation != generation || _rootProcessId != processId)
				{
					return;
				}

				previous = _previous;
			}

			ProcessTreeSnapshot? snapshot = null;
			ProcessTreeMetricsViewModel metrics;
			try
			{
				snapshot = _reader.Read(processId);
				metrics = new(
					snapshot.RootProcessId,
					snapshot.ProcessCount,
					snapshot.WorkingSetBytes,
					CalculateCpuPercent(previous, snapshot),
					snapshot.SampledAt);
			}
			catch (Exception exception)
			{
				metrics = new(
					processId,
					ProcessCount: 0,
					WorkingSetBytes: 0,
					CpuPercent: null,
					_timeProvider.GetUtcNow(),
					exception.Message);
			}

			lock (_gate)
			{
				if (_disposed || _generation != generation || _rootProcessId != processId)
				{
					return;
				}

				_previous = snapshot;
				Current = metrics;
			}

			MetricsChanged?.Invoke(this, EventArgs.Empty);
		}
		finally
		{
			Volatile.Write(ref _readInProgress, 0);
		}
	}

	private double? CalculateCpuPercent(
		ProcessTreeSnapshot? previous,
		ProcessTreeSnapshot current)
	{
		if (previous is null)
		{
			return null;
		}

		var elapsed = current.SampledAt - previous.SampledAt;
		if (elapsed <= TimeSpan.Zero)
		{
			return null;
		}

		long processorTicks = 0;
		foreach (var (processId, currentTime) in current.ProcessorTimes)
		{
			if (!previous.ProcessorTimes.TryGetValue(processId, out var previousTime))
			{
				continue;
			}

			processorTicks += Math.Max(0, (currentTime - previousTime).Ticks);
		}

		var fraction = (double)processorTicks / elapsed.Ticks / _processorCount;
		return Math.Clamp(fraction * 100, 0, 100);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_generation++;
			_timer?.Dispose();
			_timer = null;
			_previous = null;
			Current = null;
		}
	}

	private sealed record TimerState(
		SelectedProcessMetricsMonitor Owner,
		int ProcessId,
		int Generation);
}

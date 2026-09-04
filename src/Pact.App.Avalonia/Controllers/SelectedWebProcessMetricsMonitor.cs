using Pact.Core.Platform;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed record WebProcessMetricsSnapshot(
	ProcessSetSnapshot PageRenderers,
	ProcessSetSnapshot SharedRuntime,
	bool PageAttributionAvailable = true);

internal interface IWebProcessMetricsSnapshotReader
{
	Task<WebProcessMetricsSnapshot> ReadAsync(
		string pageId,
		CancellationToken cancellationToken);
}

internal sealed class SelectedWebProcessMetricsMonitor : IAsyncDisposable
{
	internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

	private readonly IWebProcessMetricsSnapshotReader _reader;
	private readonly TimeProvider _timeProvider;
	private readonly Func<Exception, Task> _reportFailureAsync;
	private readonly int _processorCount;
	private readonly Lock _gate = new();
	private readonly List<CancellationTokenSource> _cancellations = [];
	private readonly List<Task> _loops = [];
	private string? _pageId;
	private int _generation;
	private bool _disposed;

	internal SelectedWebProcessMetricsMonitor(
		IWebProcessMetricsSnapshotReader reader,
		TimeProvider timeProvider,
		int? processorCount = null,
		Func<Exception, Task>? reportFailureAsync = null)
	{
		_reader = reader ?? throw new ArgumentNullException(nameof(reader));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
		_reportFailureAsync = reportFailureAsync ?? (_ => Task.CompletedTask);
		_processorCount = processorCount ?? Environment.ProcessorCount;
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_processorCount);
	}

	internal event EventHandler? MetricsChanged;

	internal WebViewProcessMetricsViewModel? Current { get; private set; }

	internal void SetTarget(string? pageId, bool enabled)
	{
		var nextPageId = enabled && !string.IsNullOrWhiteSpace(pageId) ? pageId : null;
		CancellationTokenSource? cancellation = null;
		int generation;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (string.Equals(_pageId, nextPageId, StringComparison.Ordinal))
			{
				return;
			}

			foreach (var active in _cancellations)
			{
				active.Cancel();
			}

			_pageId = nextPageId;
			Current = null;
			generation = ++_generation;
			if (nextPageId is not null)
			{
				cancellation = new CancellationTokenSource();
				_cancellations.Add(cancellation);
			}
		}

		if (nextPageId is not null && cancellation is not null)
		{
			var loop = Task.Run(
				() => RunLoopAsync(nextPageId, generation, cancellation.Token),
				CancellationToken.None);
			lock (_gate)
			{
				_loops.Add(loop);
			}
		}
	}

	private async Task RunLoopAsync(
		string pageId,
		int generation,
		CancellationToken cancellationToken)
	{
		WebProcessMetricsSnapshot? previous = null;
		string? reportedFailure = null;
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					var snapshot = await _reader.ReadAsync(pageId, cancellationToken)
						.ConfigureAwait(false);
					var current = CreateViewModel(previous, snapshot);
					if (!TryPublish(pageId, generation, current))
					{
						return;
					}

					previous = snapshot;
					reportedFailure = null;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					return;
				}
				catch (Exception exception)
				{
					previous = null;
					var failure = $"{exception.GetType().FullName}\0{exception.Message}";
					if (!string.Equals(reportedFailure, failure, StringComparison.Ordinal))
					{
						reportedFailure = failure;
						await ReportFailureAsync(exception).ConfigureAwait(false);
					}
					if (!TryPublish(pageId, generation, Unavailable(exception.Message)))
					{
						return;
					}
				}

				await Task.Delay(PollInterval, _timeProvider, cancellationToken)
					.ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Target changes and shutdown cancel the loop without surfacing an error.
		}
	}

	private async Task ReportFailureAsync(Exception exception)
	{
		try
		{
			await _reportFailureAsync(exception).ConfigureAwait(false);
		}
		catch
		{
			// Diagnostics are best effort and must not stop resource sampling.
		}
	}

	private WebViewProcessMetricsViewModel CreateViewModel(
		WebProcessMetricsSnapshot? previous,
		WebProcessMetricsSnapshot current) =>
		new(
			CreateGroup(previous?.PageRenderers, current.PageRenderers),
			CreateGroup(previous?.SharedRuntime, current.SharedRuntime),
			current.PageRenderers.SampledAt >= current.SharedRuntime.SampledAt
				? current.PageRenderers.SampledAt
				: current.SharedRuntime.SampledAt,
			PageAttributionAvailable: current.PageAttributionAvailable);

	private ProcessMetricsGroupViewModel CreateGroup(
		ProcessSetSnapshot? previous,
		ProcessSetSnapshot current) =>
		new(
			current.ProcessCount,
			current.WorkingSetBytes,
			CalculateCpuPercent(previous, current));

	private double? CalculateCpuPercent(
		ProcessSetSnapshot? previous,
		ProcessSetSnapshot current)
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
			if (previous.ProcessorTimes.TryGetValue(processId, out var previousTime))
			{
				processorTicks += Math.Max(0, (currentTime - previousTime).Ticks);
			}
		}

		var fraction = (double)processorTicks / elapsed.Ticks / _processorCount;
		return Math.Clamp(fraction * 100, 0, 100);
	}

	private bool TryPublish(
		string pageId,
		int generation,
		WebViewProcessMetricsViewModel metrics)
	{
		lock (_gate)
		{
			if (_disposed
				|| _generation != generation
				|| !string.Equals(_pageId, pageId, StringComparison.Ordinal))
			{
				return false;
			}

			Current = metrics;
		}

		MetricsChanged?.Invoke(this, EventArgs.Empty);
		return true;
	}

	private WebViewProcessMetricsViewModel Unavailable(string error) =>
		new(
			new(ProcessCount: 0, WorkingSetBytes: 0, CpuPercent: null),
			new(ProcessCount: 0, WorkingSetBytes: 0, CpuPercent: null),
			_timeProvider.GetUtcNow(),
			error);

	public async ValueTask DisposeAsync()
	{
		Task[] loops;
		CancellationTokenSource[] cancellations;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_generation++;
			loops = _loops.ToArray();
			cancellations = _cancellations.ToArray();
			foreach (var cancellation in cancellations)
			{
				cancellation.Cancel();
			}
		}

		await Task.WhenAll(loops).ConfigureAwait(false);
		foreach (var cancellation in cancellations)
		{
			cancellation.Dispose();
		}
	}
}

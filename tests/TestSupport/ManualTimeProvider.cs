[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Performance",
	"CA1812:Avoid uninstantiated internal classes",
	Justification = "The shared source file is linked into test assemblies that do not all use virtual time.")]
internal sealed class ManualTimeProvider : TimeProvider
{
	private readonly Lock _sync = new();
	private readonly List<ManualTimer> _timers = [];
	private readonly List<TimerWaiter> _timerWaiters = [];
	private readonly Dictionary<TimeSpan, int> _timerCreationCounts = [];
	private DateTimeOffset _utcNow;

	internal ManualTimeProvider()
		: this(DateTimeOffset.UnixEpoch)
	{
	}

	internal ManualTimeProvider(DateTimeOffset utcNow)
	{
		_utcNow = utcNow;
	}

	public override DateTimeOffset GetUtcNow()
	{
		lock (_sync)
		{
			return _utcNow;
		}
	}

	public override ITimer CreateTimer(
		TimerCallback callback,
		object? state,
		TimeSpan dueTime,
		TimeSpan period)
	{
		ManualTimer timer = new(this, callback, state, dueTime, period);
		List<TaskCompletionSource<object?>> completedWaiters = [];
		lock (_sync)
		{
			_timers.Add(timer);
			var creationCount = _timerCreationCounts.GetValueOrDefault(dueTime) + 1;
			_timerCreationCounts[dueTime] = creationCount;
			for (var index = _timerWaiters.Count - 1; index >= 0; index--)
			{
				var waiter = _timerWaiters[index];
				if (waiter.DueTime == dueTime
					&& waiter.MinimumCount <= creationCount)
				{
					completedWaiters.Add(waiter.Completion);
					_timerWaiters.RemoveAt(index);
				}
			}
		}

		foreach (var waiter in completedWaiters)
		{
			waiter.TrySetResult(null);
		}

		return timer;
	}

	internal Task WaitForTimerCreatedAsync(TimeSpan dueTime)
	{
		lock (_sync)
		{
			return WaitForTimerCountLocked(
				dueTime,
				_timerCreationCounts.GetValueOrDefault(dueTime) + 1);
		}
	}

	internal Task WaitForTimerCountAsync(TimeSpan dueTime, int minimumCount)
	{
		lock (_sync)
		{
			return WaitForTimerCountLocked(dueTime, minimumCount);
		}
	}

	internal void Advance(TimeSpan amount)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);

		List<ManualTimer> due;
		lock (_sync)
		{
			_utcNow += amount;
			due = _timers
				.Where(timer => timer.IsDue(_utcNow))
				.ToList();
		}

		foreach (var timer in due)
		{
			timer.Fire();
		}
	}

	private Task WaitForTimerCountLocked(TimeSpan dueTime, int minimumCount)
	{
		if (_timerCreationCounts.GetValueOrDefault(dueTime) >= minimumCount)
		{
			return Task.CompletedTask;
		}

		TaskCompletionSource<object?> completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		_timerWaiters.Add(new TimerWaiter(dueTime, minimumCount, completion));
		return completion.Task;
	}

	private void Remove(ManualTimer timer)
	{
		lock (_sync)
		{
			_timers.Remove(timer);
		}
	}

	private sealed record TimerWaiter(
		TimeSpan DueTime,
		int MinimumCount,
		TaskCompletionSource<object?> Completion);

	private sealed class ManualTimer : ITimer
	{
		private readonly ManualTimeProvider _owner;
		private readonly TimerCallback _callback;
		private readonly object? _state;
		private TimeSpan _period;
		private DateTimeOffset _dueAt;
		private bool _active;

		internal ManualTimer(
			ManualTimeProvider owner,
			TimerCallback callback,
			object? state,
			TimeSpan dueTime,
			TimeSpan period)
		{
			_owner = owner;
			_callback = callback;
			_state = state;
			Change(dueTime, period);
		}

		public bool Change(TimeSpan dueTime, TimeSpan period)
		{
			if (dueTime < Timeout.InfiniteTimeSpan
				|| period < Timeout.InfiniteTimeSpan)
			{
				throw new ArgumentOutOfRangeException(nameof(dueTime));
			}

			_period = period;
			_active = dueTime != Timeout.InfiniteTimeSpan;
			_dueAt = _active
				? _owner.GetUtcNow() + dueTime
				: DateTimeOffset.MaxValue;
			return true;
		}

		internal bool IsDue(DateTimeOffset now) => _active && _dueAt <= now;

		internal void Fire()
		{
			if (!_active)
			{
				return;
			}

			if (_period == Timeout.InfiniteTimeSpan)
			{
				_active = false;
			}
			else
			{
				_dueAt = _owner.GetUtcNow() + _period;
			}

			_callback(_state);
		}

		public void Dispose()
		{
			_active = false;
			_owner.Remove(this);
		}

		public ValueTask DisposeAsync()
		{
			Dispose();
			return ValueTask.CompletedTask;
		}
	}
}

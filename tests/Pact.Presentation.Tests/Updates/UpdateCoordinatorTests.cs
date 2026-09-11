using Pact.Core.Updates;
using Pact.Infrastructure.Updates;
using Pact.Presentation.Updates;

namespace Pact.Presentation.Tests.Updates;

public sealed class UpdateCoordinatorTests
{
	private static readonly DateTimeOffset Start =
		new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
	private static readonly StableReleaseVersion Running = new(1, 2, 2);

	[Test]
	public async Task Automatic_checks_start_after_30_seconds_and_then_run_hourly()
	{
		ManualTimeProvider time = new(Start);
		FakeReleaseClient client = new((_, _) =>
			Task.FromResult<GitHubReleaseResponse>(new GitHubReleaseResponse.NoUpdate()));
		await using UpdateCoordinator coordinator = new(client, time, Running);
		await coordinator.StartAsync(CancellationToken.None);
		await coordinator.StartAsync(CancellationToken.None);

		time.Advance(TimeSpan.FromSeconds(29));
		await PumpAsync();
		client.CallCount.ShouldBe(0);
		time.Advance(TimeSpan.FromSeconds(1));
		await WaitUntilAsync(() => client.CallCount == 1 && time.PendingTimerCount == 1);
		time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
		await PumpAsync();
		client.CallCount.ShouldBe(1);
		time.Advance(TimeSpan.FromSeconds(1));
		await WaitUntilAsync(() => client.CallCount == 2);
	}

	[Test]
	public async Task Lifetime_cancellation_stops_the_schedule_cleanly()
	{
		ManualTimeProvider time = new(Start);
		FakeReleaseClient client = new((_, _) =>
			Task.FromResult<GitHubReleaseResponse>(new GitHubReleaseResponse.NoUpdate()));
		using CancellationTokenSource lifetime = new();
		await using UpdateCoordinator coordinator = new(client, time, Running);
		await coordinator.StartAsync(lifetime.Token);

		await lifetime.CancelAsync();
		time.Advance(TimeSpan.FromDays(1));
		await PumpAsync();

		client.CallCount.ShouldBe(0);
	}

	[Test]
	public async Task Dispose_cancels_the_schedule_without_another_check()
	{
		ManualTimeProvider time = new(Start);
		FakeReleaseClient client = new((_, _) =>
			Task.FromResult<GitHubReleaseResponse>(new GitHubReleaseResponse.NoUpdate()));
		UpdateCoordinator coordinator = new(client, time, Running);
		await coordinator.StartAsync(CancellationToken.None);

		await coordinator.DisposeAsync();
		time.Advance(TimeSpan.FromDays(1));
		await PumpAsync();

		client.CallCount.ShouldBe(0);
	}

	[Test]
	public async Task Manual_and_automatic_checks_never_overlap()
	{
		ManualTimeProvider time = new(Start);
		TaskCompletionSource<GitHubReleaseResponse> first = NewCompletion();
		TaskCompletionSource<GitHubReleaseResponse> second = NewCompletion();
		FakeReleaseClient client = new((call, _) => call == 1 ? first.Task : second.Task);
		await using UpdateCoordinator coordinator = new(client, time, Running);
		await coordinator.StartAsync(CancellationToken.None);
		time.Advance(TimeSpan.FromSeconds(30));
		await WaitUntilAsync(() => client.CallCount == 1);

		var manual = coordinator.CheckNowAsync(
			UpdateCheckOrigin.Manual,
			CancellationToken.None);
		await PumpAsync();
		client.CallCount.ShouldBe(1);
		client.MaximumConcurrency.ShouldBe(1);

		first.SetResult(new GitHubReleaseResponse.NoUpdate());
		await WaitUntilAsync(() => client.CallCount == 2);
		client.MaximumConcurrency.ShouldBe(1);
		second.SetResult(new GitHubReleaseResponse.NoUpdate());
		(await manual).ShouldBeOfType<GitHubReleaseResponse.NoUpdate>();
	}

	[Test]
	public async Task Later_suppresses_only_automatic_prompting_for_the_same_version()
	{
		ManualTimeProvider time = new(Start);
		var first = Release(1, 3, 0);
		var newer = Release(1, 4, 0);
		Queue<GitHubReleaseResponse> responses = new([
			new GitHubReleaseResponse.Available(first),
			new GitHubReleaseResponse.Available(first),
			new GitHubReleaseResponse.Available(first),
			new GitHubReleaseResponse.Available(newer)
		]);
		FakeReleaseClient client = new((_, _) => Task.FromResult(responses.Dequeue()));
		await using UpdateCoordinator coordinator = new(client, time, Running);
		List<StableReleaseVersion> offers = [];
		coordinator.StatusChanged += (_, args) =>
		{
			if (args.Status.State == UpdateState.Available)
			{
				offers.Add(args.Status.AvailableRelease!.Version);
			}
		};

		await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None);
		coordinator.DeferAutomaticPrompt(first.Version);
		await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None);
		coordinator.Status.State.ShouldBe(UpdateState.Idle);
		offers.ShouldBe([first.Version]);

		await coordinator.CheckNowAsync(UpdateCheckOrigin.Manual, CancellationToken.None);
		offers.ShouldBe([first.Version, first.Version]);
		coordinator.DeferAutomaticPrompt(first.Version);
		await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None);
		offers.ShouldBe([first.Version, first.Version, newer.Version]);
	}

	[Test]
	public async Task Repeated_automatic_result_for_visible_release_does_not_notify_twice()
	{
		var release = Release(1, 3, 0);
		FakeReleaseClient client = new((_, _) => Task.FromResult<GitHubReleaseResponse>(
			new GitHubReleaseResponse.Available(release)));
		await using UpdateCoordinator coordinator = new(
			client,
			new ManualTimeProvider(Start),
			Running);
		var availableTransitions = 0;
		coordinator.StatusChanged += (_, args) =>
		{
			if (args.Status.State == UpdateState.Available)
			{
				availableTransitions++;
			}
		};

		await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None);
		await coordinator.CheckNowAsync(UpdateCheckOrigin.Manual, CancellationToken.None);

		availableTransitions.ShouldBe(1);
	}

	[Test]
	public async Task Rate_limit_controls_schedule_and_manual_check_cannot_bypass_it()
	{
		ManualTimeProvider time = new(Start);
		FakeReleaseClient client = new((call, _) => Task.FromResult<GitHubReleaseResponse>(
			call == 1
				? new GitHubReleaseResponse.RateLimited(
					time.GetUtcNow().AddMinutes(20),
					"primary rate limit")
				: new GitHubReleaseResponse.NoUpdate()));
		await using UpdateCoordinator coordinator = new(client, time, Running);
		await coordinator.StartAsync(CancellationToken.None);
		time.Advance(TimeSpan.FromSeconds(30));
		await WaitUntilAsync(() => client.CallCount == 1 && time.PendingTimerCount == 1);

		var manual = await coordinator.CheckNowAsync(
			UpdateCheckOrigin.Manual,
			CancellationToken.None);
		manual.ShouldBeOfType<GitHubReleaseResponse.RateLimited>()
			.RetryAt.ShouldBe(Start.AddMinutes(20).AddSeconds(30));
		client.CallCount.ShouldBe(1);

		time.Advance(TimeSpan.FromMinutes(19) + TimeSpan.FromSeconds(59));
		await PumpAsync();
		client.CallCount.ShouldBe(1);
		time.Advance(TimeSpan.FromSeconds(1));
		await WaitUntilAsync(() => client.CallCount == 2);
		coordinator.AutomaticChecksSuppressedUntil.ShouldBeNull();
	}

	[Test]
	public async Task Consecutive_rate_limits_back_off_and_success_resets_the_sequence()
	{
		ManualTimeProvider time = new(Start);
		FakeReleaseClient client = new((call, _) => Task.FromResult<GitHubReleaseResponse>(
			call == 9
				? new GitHubReleaseResponse.NoUpdate()
				: new GitHubReleaseResponse.RateLimited(
					time.GetUtcNow().AddMinutes(1),
					"secondary rate limit")));
		await using UpdateCoordinator coordinator = new(client, time, Running);
		int[] expectedMinutes = [1, 2, 4, 8, 16, 32, 60, 60];

		foreach (var expected in expectedMinutes)
		{
			var result = await coordinator.CheckNowAsync(
				UpdateCheckOrigin.Automatic,
				CancellationToken.None);
			var limited = result.ShouldBeOfType<GitHubReleaseResponse.RateLimited>();
			limited.RetryAt.ShouldBe(time.GetUtcNow().AddMinutes(expected));
			time.Advance(TimeSpan.FromMinutes(expected));
		}

		(await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None))
			.ShouldBeOfType<GitHubReleaseResponse.NoUpdate>();
		var afterSuccess = await coordinator.CheckNowAsync(
			UpdateCheckOrigin.Automatic,
			CancellationToken.None);
		afterSuccess.ShouldBeOfType<GitHubReleaseResponse.RateLimited>()
			.RetryAt.ShouldBe(time.GetUtcNow().AddMinutes(1));
	}

	[Test]
	public async Task Automatic_failure_returns_to_idle_while_manual_failure_is_visible()
	{
		var failure = new HttpRequestException("offline");
		FakeReleaseClient client = new((_, _) => Task.FromException<GitHubReleaseResponse>(failure));
		await using UpdateCoordinator coordinator = new(
			client,
			new ManualTimeProvider(Start),
			Running);
		List<UpdateState> states = [];
		coordinator.StatusChanged += (_, args) => states.Add(args.Status.State);

		var automatic = await coordinator.CheckNowAsync(
			UpdateCheckOrigin.Automatic,
			CancellationToken.None);
		automatic.ShouldBeOfType<GitHubReleaseResponse.Invalid>();
		coordinator.Status.State.ShouldBe(UpdateState.Idle);
		states.ShouldContain(UpdateState.Failed);

		var thrown = await Should.ThrowAsync<HttpRequestException>(() =>
			coordinator.CheckNowAsync(UpdateCheckOrigin.Manual, CancellationToken.None));
		thrown.ShouldBeSameAs(failure);
		coordinator.Status.State.ShouldBe(UpdateState.Failed);
	}

	private static UpdateRelease Release(int major, int minor, int patch)
	{
		var version = new StableReleaseVersion(major, minor, patch);
		var tag = $"v{version}";
		return new UpdateRelease(
			version,
			tag,
			new Uri($"https://github.com/s-titov-82/pact-mission-control/releases/tag/{tag}"),
			new UpdateAsset(
				$"pact-mission-control-{version}-win-x64-setup.exe",
				new Uri($"https://github.com/download/{tag}/setup.exe"),
				42),
			new UpdateAsset(
				"SHA256SUMS.txt",
				new Uri($"https://github.com/download/{tag}/SHA256SUMS.txt"),
				65));
	}

	private static TaskCompletionSource<GitHubReleaseResponse> NewCompletion() =>
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static async Task PumpAsync()
	{
		for (var iteration = 0; iteration < 20; iteration++)
		{
			await Task.Yield();
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		for (var iteration = 0; iteration < 10_000; iteration++)
		{
			if (condition())
			{
				return;
			}

			await Task.Yield();
		}

		condition().ShouldBeTrue("The asynchronous state change did not complete.");
	}

	private sealed class FakeReleaseClient(
		Func<int, CancellationToken, Task<GitHubReleaseResponse>> responseFactory)
		: IGitHubReleaseClient
	{
		private int _active;
		private int _callCount;
		private int _maximumConcurrency;

		public int CallCount => Volatile.Read(ref _callCount);
		public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

		public async Task<GitHubReleaseResponse> GetLatestStableAsync(
			StableReleaseVersion runningVersion,
			CancellationToken cancellationToken)
		{
			var call = Interlocked.Increment(ref _callCount);
			var active = Interlocked.Increment(ref _active);
			SetMaximum(active);
			try
			{
				return await responseFactory(call, cancellationToken);
			}
			finally
			{
				Interlocked.Decrement(ref _active);
			}
		}

		private void SetMaximum(int value)
		{
			while (true)
			{
				var current = Volatile.Read(ref _maximumConcurrency);
				if (value <= current
					|| Interlocked.CompareExchange(
						ref _maximumConcurrency,
						value,
						current) == current)
				{
					return;
				}
			}
		}
	}

	private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
	{
		private readonly Lock _sync = new();
		private readonly List<ManualTimer> _timers = [];
		private DateTimeOffset _utcNow = start;

		public int PendingTimerCount
		{
			get
			{
				lock (_sync)
				{
					return _timers.Count(timer => timer.IsPending);
				}
			}
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
			ManualTimer timer = new(this, callback, state);
			lock (_sync)
			{
				_timers.Add(timer);
				timer.ChangeCore(dueTime, period);
			}

			return timer;
		}

		public void Advance(TimeSpan amount)
		{
			List<(TimerCallback Callback, object? State)> callbacks = [];
			lock (_sync)
			{
				_utcNow = _utcNow.Add(amount);
				foreach (var timer in _timers.ToArray())
				{
					timer.CollectDueCallbacks(_utcNow, callbacks);
				}
			}

			foreach (var callback in callbacks)
			{
				callback.Callback(callback.State);
			}
		}

		private sealed class ManualTimer(
			ManualTimeProvider owner,
			TimerCallback callback,
			object? state) : ITimer
		{
			private DateTimeOffset? _dueAt;
			private TimeSpan _period = Timeout.InfiniteTimeSpan;
			private bool _disposed;

			public bool IsPending => !_disposed && _dueAt is not null;

			public bool Change(TimeSpan dueTime, TimeSpan period)
			{
				lock (owner._sync)
				{
					if (_disposed)
					{
						return false;
					}

					ChangeCore(dueTime, period);
					return true;
				}
			}

			public void Dispose()
			{
				lock (owner._sync)
				{
					_disposed = true;
					_dueAt = null;
					owner._timers.Remove(this);
				}
			}

			public ValueTask DisposeAsync()
			{
				Dispose();
				return ValueTask.CompletedTask;
			}

			public void ChangeCore(TimeSpan dueTime, TimeSpan period)
			{
				_period = period;
				_dueAt = dueTime == Timeout.InfiniteTimeSpan
					? null
					: owner._utcNow.Add(dueTime);
			}

			public void CollectDueCallbacks(
				DateTimeOffset now,
				List<(TimerCallback Callback, object? State)> callbacks)
			{
				if (_disposed || _dueAt is null || _dueAt > now)
				{
					return;
				}

				callbacks.Add((callback, state));
				_dueAt = _period == Timeout.InfiniteTimeSpan
					? null
					: now.Add(_period);
			}
		}
	}
}

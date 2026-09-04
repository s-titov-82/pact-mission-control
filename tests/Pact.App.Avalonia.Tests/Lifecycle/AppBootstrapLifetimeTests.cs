using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Lifecycle;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Lifecycle;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Successful process-lease ownership is transferred to AppBootstrap, which is awaited and disposed by each test.")]
public sealed class AppBootstrapLifetimeTests
{
	[Test]
	public async Task Startup_is_single_flight()
	{
		await using BootstrapFixture fixture = new();
		var invocationCount = 0;

		var first = fixture.Bootstrap.StartShellAsync(_ =>
		{
			invocationCount++;
			return Task.CompletedTask;
		});
		var second = fixture.Bootstrap.StartShellAsync(_ =>
		{
			invocationCount++;
			return Task.CompletedTask;
		});

		await Task.WhenAll(first, second);

		first.ShouldBeSameAs(second);
		invocationCount.ShouldBe(1);
	}

	[Test]
	public async Task Shutdown_cancels_and_joins_incomplete_startup_before_cleanup()
	{
		await using BootstrapFixture fixture = new();
		TaskCompletionSource started =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource canceled =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		var startup = fixture.Bootstrap.StartShellAsync(async token =>
		{
			started.SetResult();
			try
			{
				await Task.Delay(Timeout.InfiniteTimeSpan, token);
			}
			catch (OperationCanceledException)
			{
				canceled.SetResult();
				throw;
			}
		});

		await started.Task;
		var shutdown = fixture.Bootstrap.ShutdownAsync();

		await canceled.Task;
		await shutdown;

		startup.IsCanceled.ShouldBeTrue();
		fixture.Tracking.DisposeCount.ShouldBe(1);
	}

	[Test]
	public async Task Self_terminating_startup_can_request_shutdown_without_awaiting_itself()
	{
		await using BootstrapFixture fixture = new();

		var startup = fixture.Bootstrap.StartShellAsync(
			async _ => await fixture.Bootstrap.ShutdownAsync());

		await startup.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		fixture.Tracking.DisposeCount.ShouldBe(1);
	}

	[Test]
	public async Task Services_cannot_be_created_after_shutdown_begins()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			AppDataProcessLease.TryAcquire(root, out var lease).ShouldBeTrue();
			await using AppBootstrap bootstrap = new(
				new AppDataProfile("test", root),
				lease!,
				_ => new ServiceCollection().BuildServiceProvider());

			await bootstrap.ShutdownAsync();

			Should.Throw<ObjectDisposedException>(() => _ = bootstrap.Services);
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	[Test]
	public async Task Shutdown_detaches_and_seals_before_draining_and_disposing_producer()
	{
		await using BootstrapFixture fixture = new();
		TaskCompletionSource release =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup operations = new(static (_, _) => Task.CompletedTask);
		FakeEventProducer producer = new();
		var acceptedCount = 0;
		Task? drain = null;
		producer.Raised += HandleRaised;
		fixture.Bootstrap.RegisterShellShutdown(
			beginShutdown: () =>
			{
				producer.Raised -= HandleRaised;
				drain = operations.CompleteAndDrainAsync();
			},
			shutdown: async () =>
			{
				await drain!;
				await producer.DisposeAsync();
			});

		producer.Raise();
		acceptedCount.ShouldBe(1);

		var shutdown = fixture.Bootstrap.ShutdownAsync();
		fixture.Bootstrap.LifetimeToken.IsCancellationRequested.ShouldBeTrue();
		producer.IsDetached.ShouldBeTrue();

		producer.Raise();
		acceptedCount.ShouldBe(1);
		producer.DisposeCount.ShouldBe(0);
		fixture.Tracking.DisposeCount.ShouldBe(0);

		release.SetResult();
		await shutdown;

		producer.DisposeCount.ShouldBe(1);
		fixture.Tracking.DisposeCount.ShouldBe(1);
		return;

		void HandleRaised(object? sender, EventArgs args)
		{
			operations.TryRun(
				"held-operation",
				async () =>
				{
					acceptedCount++;
					await release.Task;
				})
				.ShouldBeTrue();
		}
	}

	private sealed class BootstrapFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private string _root => _temporaryDirectory.Path;

		public BootstrapFixture()
		{
			AppDataProcessLease.TryAcquire(_root, out var lease).ShouldBeTrue();
			Bootstrap = new AppBootstrap(
				new AppDataProfile("test", _root),
				lease!,
				_ =>
				{
					ServiceCollection services = new();
					services.AddSingleton<TrackingDisposable>();
					var provider = services.BuildServiceProvider();
					Tracking = provider.GetRequiredService<TrackingDisposable>();
					return provider;
				});
			_ = Bootstrap.Services;
		}

		public AppBootstrap Bootstrap { get; }
		public TrackingDisposable Tracking { get; private set; } = null!;

		public async ValueTask DisposeAsync()
		{
			await Bootstrap.DisposeAsync();
			await _temporaryDirectory.DisposeAsync();
		}
	}

	private static void DeleteRoot(string root)
	{
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Performance",
		"CA1812:Avoid uninstantiated internal classes",
		Justification = "The DI container constructs this test singleton through reflection.")]
	private sealed class TrackingDisposable : IAsyncDisposable
	{
		public int DisposeCount { get; private set; }

		public ValueTask DisposeAsync()
		{
			DisposeCount++;
			return ValueTask.CompletedTask;
		}
	}

	private sealed class FakeEventProducer : IAsyncDisposable
	{
		private EventHandler? _raised;

		public event EventHandler? Raised
		{
			add => _raised += value;
			remove
			{
				_raised -= value;
				IsDetached = _raised is null;
			}
		}

		public bool IsDetached { get; private set; }
		public int DisposeCount { get; private set; }

		public void Raise() => _raised?.Invoke(this, EventArgs.Empty);

		public ValueTask DisposeAsync()
		{
			DisposeCount++;
			return ValueTask.CompletedTask;
		}
	}
}

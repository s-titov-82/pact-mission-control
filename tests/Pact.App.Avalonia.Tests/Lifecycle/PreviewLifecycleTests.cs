using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Tests.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Lifecycle;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Successful process-lease ownership is transferred to AppBootstrap, which is awaited and disposed by each test.")]
public sealed class PreviewLifecycleTests
{
	[Test]
	public async Task Bootstrap_disposes_real_singletons_once_and_releases_the_lease()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			AppDataProcessLease.TryAcquire(root, out var lease).ShouldBeTrue();
			await using AppBootstrap bootstrap = new(
				new AppDataProfile("test", root),
				lease!,
				BuildTrackingServices);
			var tracking =
				bootstrap.Services.GetRequiredService<TrackingSingleton>();
			var shellShutdownCount = 0;
			bootstrap.RegisterShellShutdown(() =>
			{
				shellShutdownCount++;
				return Task.CompletedTask;
			});

			await bootstrap.ShutdownAsync();
			await bootstrap.ShutdownAsync();

			shellShutdownCount.ShouldBe(1);
			tracking.DisposeCount.ShouldBe(1);
			AppDataProcessLease.TryAcquire(root, out var reacquired).ShouldBeTrue();
			reacquired!.Dispose();
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	[Test]
	public async Task Shell_unregisters_pages_but_container_remains_the_only_disposer()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		try
		{
			AppDataProcessLease.TryAcquire(root, out var lease).ShouldBeTrue();
			await using AppBootstrap bootstrap = new(
				new AppDataProfile("test", root),
				lease!,
				BuildTrackingServices);
			var services = bootstrap.Services;
			var paths = services.GetRequiredService<AppPaths>();
			var coordinator =
				services.GetRequiredService<WebMonitorCoordinator>();
			var tracking =
				services.GetRequiredService<TrackingSingleton>();
			MainWindowViewModel viewModel = new(
				new JsonProjectStore(paths),
				new ProjectNotesStore(paths));
			await using ShellControllerTestBuilder builder = new(
				viewModel,
				new SettingsFileStore(paths),
				paths,
				new FakeTerminalWebViewHost(),
				() => new FakeTerminalBackend());
			builder
				.WithSnapshotReader(new WebMonitorSnapshotReader(
					services.GetRequiredService<WebMonitorSnapshotStore>()))
				.WithWebMonitorCoordinator(coordinator)
				.WithUiTaskDispatcher(new ImmediateUiTaskDispatcher())
				.WithEventTasks(new ObservedTaskGroup(
					static (_, _) => Task.CompletedTask));
			await using var shell = builder.Build();

			await shell.ShutdownAsync();
			await coordinator.SetRulesAsync([], CancellationToken.None);

			tracking.DisposeCount.ShouldBe(0);
			await bootstrap.ShutdownAsync();
			tracking.DisposeCount.ShouldBe(1);
			AppDataProcessLease.TryAcquire(root, out var reacquired).ShouldBeTrue();
			reacquired!.Dispose();
		}
		finally
		{
			DeleteRoot(root);
		}
	}

	private static ServiceProvider BuildTrackingServices(AppDataProfile profile)
	{
		ServiceCollection services = new();
		AppPaths paths = new(profile.RootDirectory);
		services.AddSingleton(paths);
		services.AddSingleton<WebMonitorSnapshotStore>();
		services.AddSingleton(provider => new WebMonitorCoordinator(
			provider.GetRequiredService<WebMonitorSnapshotStore>(),
			TimeProvider.System,
			static action => action()));
		services.AddSingleton<TrackingSingleton>();
		return services.BuildServiceProvider();
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
	private sealed class TrackingSingleton : IAsyncDisposable
	{
		public int DisposeCount { get; private set; }

		public ValueTask DisposeAsync()
		{
			DisposeCount++;
			return ValueTask.CompletedTask;
		}
	}
}

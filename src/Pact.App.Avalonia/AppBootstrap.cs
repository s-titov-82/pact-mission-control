using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Diagnostics;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia;

internal sealed class AppBootstrap : IDisposable, IAsyncDisposable
{
	private readonly AppDataProcessLease _lease;
	private readonly Func<AppDataProfile, ServiceProvider> _buildServices;
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly AsyncLocal<bool> _insideShellInitialization = new();
	private ServiceProvider? _services;
	private int _disposed;
	private readonly Lock _shutdownGate = new();
	private Action? _beginShellShutdown;
	private Func<Task>? _shellShutdown;
	private Task? _shellInitialization;
	private Task? _shutdownTask;

	public AppBootstrap(AppDataProfile profile, AppDataProcessLease lease, EngineProbeRunner? probeRunner = null)
		: this(
			profile,
			lease,
			CompositionRoot.BuildServiceProvider,
			probeRunner)
	{
	}

	internal AppBootstrap(
		AppDataProfile profile,
		AppDataProcessLease lease,
		Func<AppDataProfile, ServiceProvider> buildServices,
		EngineProbeRunner? probeRunner = null)
	{
		Profile = profile;
		_lease = lease;
		_buildServices = buildServices
			?? throw new ArgumentNullException(nameof(buildServices));
		ProbeRunner = probeRunner;
	}

	public AppDataProfile Profile { get; }
	public EngineProbeRunner? ProbeRunner { get; }
	public ServiceProvider Services
	{
		get
		{
			lock (_shutdownGate)
			{
				ObjectDisposedException.ThrowIf(_disposed != 0, this);
				return _services ??= BuildServices();
			}
		}
	}
	internal CancellationToken LifetimeToken => _lifetimeCancellation.Token;

	public void RegisterShellShutdown(Func<Task> shutdown) =>
		RegisterShellShutdown(static () => { }, shutdown);

	public void RegisterShellShutdown(Action beginShutdown, Func<Task> shutdown)
	{
		ArgumentNullException.ThrowIfNull(beginShutdown);
		ArgumentNullException.ThrowIfNull(shutdown);
		lock (_shutdownGate)
		{
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			_beginShellShutdown = beginShutdown;
			_shellShutdown = shutdown;
		}
	}

	public Task StartShellAsync(Func<CancellationToken, Task> initializeAsync)
	{
		ArgumentNullException.ThrowIfNull(initializeAsync);
		lock (_shutdownGate)
		{
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			return _shellInitialization ??=
				RunShellInitializationAsync(initializeAsync, _lifetimeCancellation.Token);
		}
	}

	public void RequestStop()
	{
		lock (_shutdownGate)
		{
			_lifetimeCancellation.Cancel();
		}
	}

	public Task ShutdownAsync()
	{
		lock (_shutdownGate)
		{
			return _shutdownTask ??= ShutdownCoreAsync();
		}
	}

	public void Dispose()
	{
		ShutdownAsync().GetAwaiter().GetResult();
		_lifetimeCancellation.Dispose();
	}

	public async ValueTask DisposeAsync()
	{
		await ShutdownAsync();
		_lifetimeCancellation.Dispose();
	}

	private ServiceProvider BuildServices()
	{
		AppStartupHousekeeping.Run(new AppPaths(Profile.RootDirectory));
		return _buildServices(Profile);
	}

	private async Task ShutdownCoreAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		RequestStop();
		await AppShutdownSequence.RunAsync(
			() =>
			{
				_beginShellShutdown?.Invoke();
				return Task.CompletedTask;
			},
			AwaitShellInitializationAsync,
			() => _shellShutdown?.Invoke() ?? Task.CompletedTask,
			async () =>
			{
				if (_services is not null)
				{
					await _services.DisposeAsync();
				}
			},
			() =>
			{
				DataRootHousekeeping.ClearSessionTemp(new AppPaths(Profile.RootDirectory));
				return Task.CompletedTask;
			},
			() => { _lease.Dispose(); return Task.CompletedTask; });
	}

	private async Task RunShellInitializationAsync(
		Func<CancellationToken, Task> initializeAsync,
		CancellationToken cancellationToken)
	{
		_insideShellInitialization.Value = true;
		try
		{
			await initializeAsync(cancellationToken);
		}
		finally
		{
			_insideShellInitialization.Value = false;
		}
	}

	private async Task AwaitShellInitializationAsync()
	{
		if (_insideShellInitialization.Value)
		{
			// The engine-probe startup path initiates its own shutdown after it has captured all
			// runtime evidence. Awaiting the current task here would deadlock that self-terminating
			// path; its remaining work uses only the already-captured evidence.
			return;
		}

		Task? initialization;
		lock (_shutdownGate)
		{
			initialization = _shellInitialization;
		}

		if (initialization is null)
		{
			return;
		}

		try
		{
			await initialization;
		}
		catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
		{
			// Closing during startup is an expected lifetime outcome.
		}
	}
}

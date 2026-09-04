using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Diagnostics;
using Pact.Presentation.Services;

namespace Pact.App.Avalonia.Views;

internal sealed partial class MainWindow
{
	private static readonly TimeSpan SubscriptionUsageRefreshTimeout = TimeSpan.FromMinutes(1);
	private readonly CancellationTokenSource _usageRefreshCancellation = new();
	private readonly SemaphoreSlim _usageRefreshGate = new(1, 1);
	private Task? _usageRefreshTask;

	private void StartSubscriptionUsagePolling()
	{
		if (_usageRefreshTask is not null)
		{
			return;
		}

		var cancellationToken = _usageRefreshCancellation.Token;
		_usageRefreshTask = SubscriptionUsagePollingLoop.RunAsync(
			RefreshSubscriptionUsageOnceAsync,
			SubscriptionUsageRefreshTimeout,
			() => SubscriptionUsageRefreshPolicy.GetNextRefreshInterval(
				EngineProbeController.ViewModel.SubscriptionUsages),
			Task.Delay,
			exception => _eventTasks.TryRun(
				"subscription-usage-failure-log",
				() => AppLog.AppendAsync(
					App.Bootstrap.Profile.RootDirectory,
					"Subscription usage refresh failed",
					exception)),
			cancellationToken);
	}

	private async Task RefreshSubscriptionUsageOnceAsync(CancellationToken cancellationToken)
	{
		await _usageRefreshGate.WaitAsync(cancellationToken);
		try
		{
			var usageRefresh =
				App.Services.GetRequiredService<SubscriptionUsageRefreshService>();
			await usageRefresh.RefreshAsync(
				EngineProbeController.ViewModel.ShellProfiles.ToArray(),
				EngineProbeController.ViewModel.SubscriptionUsages,
				cancellationToken);
		}
		finally
		{
			_usageRefreshGate.Release();
		}
	}
}

using System.Collections.Specialized;
using System.ComponentModel;

namespace Pact.App.Avalonia.Controllers;

internal sealed partial class AvaloniaMainShellController
{
	private readonly HashSet<INotifyPropertyChanged> _startupProperties = [];
	private readonly HashSet<INotifyCollectionChanged> _startupCollections = [];

	private async Task<bool> TryRestoreStartupStateAsync(CancellationToken cancellationToken)
	{
		try
		{
			var state = await _sessionRestorationStore.LoadAsync(cancellationToken);
			if (state is null)
			{
				return false;
			}
			LastRestorationSummary = await _softRestartRestorer.RestoreAsync(state, cancellationToken);
			if (!state.OrchestratorWasRunning)
			{
				await _orchestrator.StopAsync();
			}
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			RecordDiagnostic("startup-checkpoint-load-failed", $"category={exception.GetType().Name}");
			return false;
		}
	}

	private void ObserveStartupState()
	{
		HashSet<INotifyPropertyChanged> properties = [ViewModel.OrchestratorSlot];
		HashSet<INotifyCollectionChanged> collections = [ViewModel.Workspaces, ViewModel.RootTabs.TreeItems];
		foreach (var workspace in ViewModel.Workspaces)
		{
			collections.Add(workspace.TreeItems);
			properties.Add(workspace);
			foreach (var source in workspace.TreeItems.OfType<INotifyPropertyChanged>())
			{
				properties.Add(source);
			}
		}
		foreach (var source in ViewModel.RootTabs.TreeItems.OfType<INotifyPropertyChanged>())
		{
			properties.Add(source);
		}
		if (ViewModel.OrchestratorSlot.Session is { } session)
		{
			properties.Add(session);
		}
		foreach (var source in _startupProperties.Except(properties).ToArray())
		{
			source.PropertyChanged -= OnStartupPropertyChanged;
			_startupProperties.Remove(source);
		}
		foreach (var source in properties)
		{
			if (_startupProperties.Add(source))
			{
				source.PropertyChanged += OnStartupPropertyChanged;
			}
		}
		foreach (var source in _startupCollections.Except(collections).ToArray())
		{
			source.CollectionChanged -= OnStartupCollectionChanged;
			_startupCollections.Remove(source);
		}
		foreach (var source in collections)
		{
			if (_startupCollections.Add(source))
			{
				source.CollectionChanged += OnStartupCollectionChanged;
			}
		}
	}

	private void OnStartupCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
	{
		ObserveStartupState();
		_startupCheckpoint.Publish();
	}

	private void OnStartupPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName is "Session")
		{
			ObserveStartupState();
		}
		if (args.PropertyName is "Record" or "Session" or "IsRunning" or "IsEnabled" or "IsManuallyPaused" or "IsBrowserLoaded")
		{
			_startupCheckpoint.Publish();
		}
	}

	private void DetachStartupState()
	{
		foreach (var source in _startupProperties)
		{
			source.PropertyChanged -= OnStartupPropertyChanged;
		}
		foreach (var source in _startupCollections)
		{
			source.CollectionChanged -= OnStartupCollectionChanged;
		}
		_startupProperties.Clear();
		_startupCollections.Clear();
	}
}

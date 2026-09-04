using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Pact.Presentation.ViewModels;

/// <summary>Bindable state of the singular orchestrator tier pinned above ROOT.</summary>
public sealed class OrchestratorSlotViewModel : INotifyPropertyChanged
{
	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>User-visible tier title.</summary>
	public string Title { get; } = "Orchestrator";

	/// <summary>The slot's terminal projection after its first start attempt.</summary>
	public SessionViewModel? Session { get; private set; }

	/// <summary>Whether the attached orchestrator terminal is the shell's current terminal.</summary>
	public bool IsCurrent => Session?.IsCurrentTerminal == true;

	/// <summary>Current lifecycle or provisioning state.</summary>
	public string StateText
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	} = "Not provisioned";

	/// <summary>Whether the configured slot has enough data to start.</summary>
	public bool IsProvisioned
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CanStart));
		}
	}

	/// <summary>Whether the master switch permits this slot to run.</summary>
	public bool IsEnabled
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CanStart));
		}
	}

	/// <summary>Whether the slot process currently runs.</summary>
	public bool IsRunning
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(CanStart));
			OnPropertyChanged(nameof(CanStop));
		}
	}

	/// <summary>Whether the pinned row should offer Start.</summary>
	public bool CanStart => IsProvisioned && IsEnabled && !IsRunning;

	/// <summary>Whether the pinned row should offer Stop.</summary>
	public bool CanStop => IsRunning;

	/// <summary>Applies one atomic lifecycle projection from the shell.</summary>
	public void Apply(
		bool isProvisioned,
		bool isEnabled,
		bool isRunning,
		string stateText)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stateText);
		IsProvisioned = isProvisioned;
		IsEnabled = isEnabled;
		IsRunning = isRunning;
		StateText = stateText;
	}

	/// <summary>Attaches the one terminal projection owned by this singular slot.</summary>
	public void AttachSession(SessionViewModel session)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (ReferenceEquals(Session, session))
		{
			return;
		}

		Session?.PropertyChanged -= OnSessionPropertyChanged;

		Session = session;
		Session.PropertyChanged += OnSessionPropertyChanged;
		OnPropertyChanged(nameof(Session));
		OnPropertyChanged(nameof(IsCurrent));
	}

	private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
	{
		if (eventArgs.PropertyName == nameof(SessionViewModel.IsCurrentTerminal))
		{
			OnPropertyChanged(nameof(IsCurrent));
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

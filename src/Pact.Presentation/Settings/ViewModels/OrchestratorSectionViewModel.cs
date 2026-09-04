using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Input;
using Pact.Core.Orchestrator;
using Pact.Infrastructure.Orchestrator;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Edits and explicitly provisions the single dedicated Hermes orchestrator slot.</summary>
public sealed class OrchestratorSectionViewModel : SettingsSectionViewModelBase
{
	private const string ProfileName = "pact";
	private readonly OrchestratorStore _store;
	private readonly HermesProvisioner _provisioner;
	private readonly string _hermesHome;
	private readonly string _endpointUrl;
	private OrchestratorRecord _record = OrchestratorRecord.CreateDefault();
	private bool _loading;

	/// <summary>Creates the orchestrator settings section over its store and provisioner.</summary>
	public OrchestratorSectionViewModel(
		OrchestratorStore store,
		HermesProvisioner provisioner,
		string hermesHome,
		string endpointUrl)
		: base(
			SettingsSection.Orchestrator,
			"Orchestrator",
			"Dedicated Hermes session, Pact tools, and workstation lock routine.",
			"orchestrator.json",
			ResolvePath(store))
	{
		ArgumentNullException.ThrowIfNull(provisioner);
		ArgumentException.ThrowIfNullOrWhiteSpace(hermesHome);
		ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
		_store = store;
		_provisioner = provisioner;
		_hermesHome = hermesHome;
		_endpointUrl = endpointUrl;
		InitializeCommand = new AsyncRelayCommand(
			() => InitializeAsync(CancellationToken.None));
		ReissueCredentialCommand = new AsyncRelayCommand(
			() => ReissueCredentialAsync(CancellationToken.None));
	}

	/// <summary>Whether Pact should run the dedicated slot.</summary>
	public bool Enabled
	{
		get;
		set => SetEditableField(ref field, value);
	}

	/// <summary>Whether Windows lock and unlock events should prompt the slot.</summary>
	public bool LockDetectionEnabled
	{
		get;
		set => SetEditableField(ref field, value);
	}

	/// <summary>Prompt submitted when Windows reports that the workstation locked.</summary>
	public string LockPrompt
	{
		get;
		set => SetEditableField(ref field, value);
	} = string.Empty;

	/// <summary>Prompt submitted when Windows reports that the workstation unlocked.</summary>
	public string UnlockPrompt
	{
		get;
		set => SetEditableField(ref field, value);
	} = string.Empty;

	/// <summary>Per-artifact output from the most recent explicit provisioning action.</summary>
	public ObservableCollection<string> ProvisionLog { get; } = [];

	/// <summary>Command bound to the explicit Initialize button.</summary>
	public IAsyncRelayCommand InitializeCommand { get; }

	/// <summary>Command bound to the explicit credential-reissue button.</summary>
	public IAsyncRelayCommand ReissueCredentialCommand { get; }

	/// <inheritdoc />
	public override async Task LoadAsync(CancellationToken cancellationToken)
	{
		_loading = true;
		try
		{
			_record = await _store.LoadAsync(cancellationToken);
			Enabled = _record.Enabled;
			LockDetectionEnabled = _record.LockDetectionEnabled;
			LockPrompt = _record.LockPrompt;
			UnlockPrompt = _record.UnlockPrompt;
			StatusText = _record.IsProvisioned
				? "Orchestrator profile is initialized."
				: "Orchestrator profile is not initialized.";
			ClearDirty();
		}
		finally
		{
			_loading = false;
		}
	}

	/// <inheritdoc />
	public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		_record = _record with
		{
			Enabled = Enabled,
			LockDetectionEnabled = LockDetectionEnabled,
			LockPrompt = LockPrompt,
			UnlockPrompt = UnlockPrompt
		};
		await _store.SaveAsync(_record, cancellationToken);
		StatusText = "Saved Orchestrator.";
		ClearDirty();
		return true;
	}

	/// <summary>Creates the Hermes profile and stores a usable slot only after every step succeeds.</summary>
	public async Task InitializeAsync(CancellationToken cancellationToken)
	{
		var current = await _store.LoadAsync(cancellationToken);
		var credential = string.IsNullOrWhiteSpace(current.Credential)
			? CreateCredential()
			: current.Credential;
		await ProvisionAndSaveAsync(current, credential, cancellationToken);
	}

	/// <summary>Re-provisions the profile with a fresh credential, preserving the old one on failure.</summary>
	public Task ReissueCredentialAsync(CancellationToken cancellationToken) =>
		ProvisionAndSaveAsync(_record, CreateCredential(), cancellationToken);

	private async Task ProvisionAndSaveAsync(
		OrchestratorRecord current,
		string credential,
		CancellationToken cancellationToken)
	{
		ProvisionLog.Clear();
		var steps = await _provisioner.ProvisionAsync(
			_hermesHome,
			ProfileName,
			_endpointUrl,
			credential,
			cancellationToken);
		foreach (var step in steps)
		{
			ProvisionLog.Add($"{step.Name}: {step.Outcome} — {step.Detail}");
		}

		if (steps.Any(step => step.Outcome == ProvisionOutcome.Failed))
		{
			StatusText = "Orchestrator initialization failed.";
			return;
		}

		_record = current with
		{
			LaunchCommand = $"hermes -p {ProfileName}",
			WorkingDirectory = ResolveWorkingDirectory(_hermesHome),
			Credential = credential
		};
		await _store.SaveAsync(_record, cancellationToken);
		_loading = true;
		try
		{
			Enabled = _record.Enabled;
			LockDetectionEnabled = _record.LockDetectionEnabled;
			LockPrompt = _record.LockPrompt;
			UnlockPrompt = _record.UnlockPrompt;
			ClearDirty();
		}
		finally
		{
			_loading = false;
		}

		StatusText = "Orchestrator profile initialized.";
	}

	private void SetEditableField<T>(ref T field, T value)
	{
		if (SetField(ref field, value) && !_loading)
		{
			MarkDirty();
		}
	}

	private static string ResolvePath(OrchestratorStore store)
	{
		ArgumentNullException.ThrowIfNull(store);
		return store.Path;
	}

	// The slot runs from the user's home rather than from wherever the Hermes root happens to
	// live, which on Windows is an application-data directory.
	private static string ResolveWorkingDirectory(string hermesHome) =>
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
			? home
			: Directory.GetParent(hermesHome)?.FullName ?? hermesHome;

	private static string CreateCredential() =>
		Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

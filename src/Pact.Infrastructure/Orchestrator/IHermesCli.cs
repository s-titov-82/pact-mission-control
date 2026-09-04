namespace Pact.Infrastructure.Orchestrator;

/// <summary>Result of asking Hermes to create and validate a profile.</summary>
/// <param name="Succeeded">Whether Hermes exited successfully.</param>
/// <param name="ProfilePath">Path of the profile Hermes created.</param>
/// <param name="Output">Captured diagnostic output.</param>
public sealed record HermesCliResult(
	bool Succeeded,
	string ProfilePath,
	string Output);

/// <summary>Minimal external Hermes command boundary used during provisioning.</summary>
public interface IHermesCli
{
	/// <summary>Reports whether the Hermes executable is available.</summary>
	bool IsInstalled();

	/// <summary>Asks Hermes itself to create and validate a named profile.</summary>
	Task<HermesCliResult> CreateProfileAsync(
		string profileName,
		CancellationToken cancellationToken);
}

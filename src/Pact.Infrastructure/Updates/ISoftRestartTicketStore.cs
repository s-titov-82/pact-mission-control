using Pact.Core.Updates;

namespace Pact.Infrastructure.Updates;

/// <summary>Owns exact-path creation, helper outcome updates, and one-time ticket consumption.</summary>
public interface ISoftRestartTicketStore
{
	/// <summary>Atomically creates one new Pending ticket and returns its exact path.</summary>
	Task<string> WriteNewAsync(
		SoftRestartTicket ticket,
		CancellationToken cancellationToken);

	/// <summary>Atomically replaces a Pending ticket with the helper's terminal outcome.</summary>
	Task UpdateOutcomeAsync(
		string restartId,
		SoftRestartOutcome outcome,
		CancellationToken cancellationToken);

	/// <summary>
	/// Consumes only the ticket derived from the supplied id. Invalid tickets are quarantined.
	/// </summary>
	Task<SoftRestartTicket?> ConsumeExactAsync(
		string restartId,
		StableReleaseVersion runningVersion,
		CancellationToken cancellationToken);
}

using Pact.Core.Updates;

namespace Pact.Presentation.Updates;

/// <summary>
/// Carries one immutable update-status snapshot to presentation subscribers.
/// </summary>
public sealed class UpdateStatusChangedEventArgs : EventArgs
{
	/// <summary>Creates event data for the supplied status snapshot.</summary>
	/// <param name="status">The newly published status.</param>
	public UpdateStatusChangedEventArgs(UpdateStatus status)
	{
		ArgumentNullException.ThrowIfNull(status);
		Status = status;
	}

	/// <summary>Gets the newly published status.</summary>
	public UpdateStatus Status { get; }
}

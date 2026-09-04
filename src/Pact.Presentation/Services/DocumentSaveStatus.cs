namespace Pact.Presentation.Services;

/// <summary>Canonical persistence state of an editable Markdown buffer.</summary>
public enum DocumentSaveState
{
	/// <summary>The current buffer is persisted.</summary>
	Clean,

	/// <summary>The current buffer has local changes waiting to be persisted.</summary>
	Dirty,

	/// <summary>A persistence operation is in progress.</summary>
	Saving,

	/// <summary>Local changes conflict with a newer backing-store revision.</summary>
	Conflict,

	/// <summary>The last persistence attempt failed and the local buffer remains pending.</summary>
	Failed
}

/// <summary>
/// Immutable save projection, including the original failure for diagnostics when saving fails.
/// </summary>
public sealed record DocumentSaveStatus(
	DocumentSaveState State,
	string? ErrorMessage = null,
	Exception? Exception = null);
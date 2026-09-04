namespace Pact.Presentation.Services;

/// <summary>
/// Contract shared by app-owned notes and project-owned Markdown files in the editor surface.
/// </summary>
public interface IMarkdownEditorDocument
{
	/// <summary>Raised when the editor must replace its full text buffer.</summary>
	event EventHandler? TextReplaced;

	/// <summary>Raised after the canonical persistence state changes.</summary>
	event EventHandler<DocumentSaveStatus>? SaveStatusChanged;

	/// <summary>Current editor buffer.</summary>
	string Text { get; }

	/// <summary>Whether the initial backing-store read has completed.</summary>
	bool IsLoaded { get; }

	/// <summary>Current persistence state of the editor buffer.</summary>
	DocumentSaveStatus SaveStatus { get; }

	/// <summary>Loads the document once.</summary>
	Task LoadAsync(CancellationToken cancellationToken);

	/// <summary>Accepts a new editor buffer and schedules persistence.</summary>
	void SetText(string text);

	/// <summary>Persists pending edits immediately.</summary>
	Task FlushAsync(CancellationToken cancellationToken);

	/// <summary>Checks for a newer backing-store revision.</summary>
	Task CheckForExternalChangeAsync(CancellationToken cancellationToken);

	/// <summary>Discards local edits in favor of the backing-store revision.</summary>
	Task ReloadFromDiskAsync(CancellationToken cancellationToken);

	/// <summary>Resolves a conflict by saving the local buffer.</summary>
	Task SaveMineAsync(CancellationToken cancellationToken);
}
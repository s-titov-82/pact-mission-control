using System.Security.Cryptography;
using System.Text;

namespace Pact.Core.AgentControl;

/// <summary>Computes opaque content revisions for project Notes buffers.</summary>
public static class ProjectNotesRevision
{
	/// <summary>Computes a deterministic revision from the exact supplied text.</summary>
	/// <param name="text">Current Notes text, without line-ending normalization.</param>
	/// <returns>A lowercase hexadecimal SHA-256 digest.</returns>
	public static string Compute(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return Convert.ToHexStringLower(
			SHA256.HashData(Encoding.UTF8.GetBytes(text)));
	}
}

/// <summary>Captures the current project Notes text and its opaque revision.</summary>
/// <param name="Text">Exact current Notes text.</param>
/// <param name="Revision">Opaque revision computed from <paramref name="Text"/>.</param>
public sealed record ProjectNotesSnapshot(string Text, string Revision)
{
	/// <summary>Creates a snapshot and computes its revision from the exact text.</summary>
	/// <param name="text">Exact current Notes text.</param>
	/// <returns>A snapshot with a matching opaque revision.</returns>
	public static ProjectNotesSnapshot FromText(string text) =>
		new(text, ProjectNotesRevision.Compute(text));
}

/// <summary>Describes the outcome of a revision-aware Notes mutation.</summary>
public enum ProjectNotesMutationStatus
{
	/// <summary>The buffer changed and the new content was persisted.</summary>
	Applied,

	/// <summary>The expected revision was stale and the buffer was not changed.</summary>
	Conflict,

	/// <summary>The buffer changed, but its immediate persistence attempt failed.</summary>
	AppliedButNotPersisted
}

/// <summary>Returns the current Notes snapshot and the result of a mutation attempt.</summary>
/// <param name="Snapshot">Snapshot current after the operation.</param>
/// <param name="Status">Mutation outcome.</param>
public sealed record ProjectNotesMutationResult(
	ProjectNotesSnapshot Snapshot,
	ProjectNotesMutationStatus Status);

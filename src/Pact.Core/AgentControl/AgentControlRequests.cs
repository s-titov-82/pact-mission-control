namespace Pact.Core.AgentControl;

/// <summary>Owner resolved from the caller's token rather than supplied by the agent.</summary>
/// <param name="IsRoot">Whether the calling session is a project-independent ROOT tab.</param>
/// <param name="ProjectId">Owning project, or <see langword="null"/> for ROOT.</param>
public sealed record AgentControlOwner(bool IsRoot, string? ProjectId);

/// <summary>A request to start a review loop against the calling session's project.</summary>
/// <param name="ScenarioId">Scenario definition id.</param>
/// <param name="ReviewProfileId">Reviewer launch profile id.</param>
/// <param name="Target">Path, branch reference, or pasted text under review.</param>
/// <param name="MaxIterations">Optional positive pass-budget override.</param>
public sealed record RequestReviewRequest(
	string ScenarioId,
	string ReviewProfileId,
	string Target,
	int? MaxIterations);

/// <summary>A request to append text without replacing existing project notes.</summary>
/// <param name="Text">Non-blank text to append.</param>
public sealed record AppendNoteRequest(string Text);

/// <summary>A revision-aware request to replace all existing project Notes text.</summary>
/// <param name="Text">Complete replacement text; an empty string deletes all content.</param>
/// <param name="ExpectedRevision">Opaque revision returned by a preceding Notes read.</param>
public sealed record ReplaceNoteRequest(string Text, string ExpectedRevision);

/// <summary>A request to create a saved browser tab under the caller's owner.</summary>
/// <param name="Url">Absolute HTTP(S) address.</param>
/// <param name="Title">Optional tab label.</param>
public sealed record OpenWebTabRequest(string Url, string? Title);

/// <summary>A refusal returned as a tool error rather than a transport failure.</summary>
/// <param name="Code">Stable machine-readable reason.</param>
/// <param name="Message">Explanation suitable for the calling agent.</param>
public sealed record AgentControlFailure(string Code, string Message);

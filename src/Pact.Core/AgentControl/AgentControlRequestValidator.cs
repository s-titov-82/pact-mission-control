namespace Pact.Core.AgentControl;

/// <summary>
/// Validates ownership and request shape before coordinators are touched. Named settings are
/// resolved later by their runtime owners.
/// </summary>
public static class AgentControlRequestValidator
{
	private const string OwnerNotAProject = "owner-not-a-project";
	private const string InvalidArgument = "invalid-argument";

	/// <summary>Validates that the authenticated owner may access project Notes.</summary>
	public static AgentControlFailure? ValidateProjectNotesOwner(AgentControlOwner owner)
	{
		ArgumentNullException.ThrowIfNull(owner);
		return RequireProjectOwner(owner, "access project notes");
	}

	/// <summary>Validates a review request.</summary>
	public static AgentControlFailure? Validate(AgentControlOwner owner, RequestReviewRequest request)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(request);

		var ownerFailure = RequireProjectOwner(owner, "start a review");
		if (ownerFailure is not null)
		{
			return ownerFailure;
		}

		if (string.IsNullOrWhiteSpace(request.ScenarioId)
			|| string.IsNullOrWhiteSpace(request.ReviewProfileId)
			|| string.IsNullOrWhiteSpace(request.Target))
		{
			return new AgentControlFailure(
				InvalidArgument,
				"scenarioId, reviewProfileId, and target are all required and must be non-blank.");
		}

		return request.MaxIterations is <= 0
			? new AgentControlFailure(
				InvalidArgument,
				"maxIterations must be greater than zero when supplied.")
			: null;
	}

	/// <summary>Validates a project-note append.</summary>
	public static AgentControlFailure? Validate(AgentControlOwner owner, AppendNoteRequest request)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(request);

		var ownerFailure = RequireProjectOwner(owner, "append to project notes");
		return ownerFailure ?? (string.IsNullOrWhiteSpace(request.Text)
			? new AgentControlFailure(InvalidArgument, "text must be non-blank.")
			: null);
	}

	/// <summary>Validates a revision-aware complete project Notes replacement.</summary>
	public static AgentControlFailure? Validate(AgentControlOwner owner, ReplaceNoteRequest request)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(request);

		var ownerFailure = RequireProjectOwner(owner, "replace project notes");
		return ownerFailure ?? (string.IsNullOrWhiteSpace(request.ExpectedRevision)
			? new AgentControlFailure(InvalidArgument, "expectedRevision is required and must be non-blank.")
			: null);
	}

	/// <summary>Validates a browser-tab request; both project and ROOT owners may open tabs.</summary>
	public static AgentControlFailure? Validate(AgentControlOwner owner, OpenWebTabRequest request)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(request);

		return !Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
			|| (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
			? new AgentControlFailure(
				InvalidArgument,
				"url must be an absolute http or https address.")
			: null;
	}

	private static AgentControlFailure? RequireProjectOwner(AgentControlOwner owner, string action) =>
		owner.IsRoot || string.IsNullOrWhiteSpace(owner.ProjectId)
			? new AgentControlFailure(
				OwnerNotAProject,
				$"This session is a ROOT tab and has no project, so it cannot {action}.")
			: null;
}

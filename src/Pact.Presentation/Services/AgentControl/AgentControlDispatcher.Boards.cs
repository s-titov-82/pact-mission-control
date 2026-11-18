using System.Text.Json.Nodes;
using Pact.Core.Boards;

namespace Pact.Presentation.Services.AgentControl;

public sealed partial class AgentControlDispatcher
{
	/// <summary>Whether a caller has project capabilities, used to filter its tool catalogue.</summary>
	public bool IsProjectSession(string? sessionId) => sessionId is not null
		&& _host.TryGetOwner(sessionId, out var owner) && !owner.IsRoot && owner.ProjectId is not null;

	/// <summary>Validates board arguments and routes exclusively to the authenticated project.</summary>
	public async Task<AgentControlResult> ExecuteBoardToolAsync(string sessionId, string toolName, JsonNode arguments, CancellationToken cancellationToken)
	{
		if (!_host.TryGetOwner(sessionId, out var owner))
		{
			return UnknownSession();
		}
		if (owner.IsRoot || owner.ProjectId is null)
		{
			return AgentControlResult.Refused("no-project", "Board tools require a project session.");
		}
		try
		{
			var request = BoardTools.Parse(toolName, arguments);
			if (request.Command is { } command)
			{
				if (command is UpdateCard { Patch.AssignedSessionId.Value: { } assigned }
					&& (!_host.TryGetOwner(assigned, out var assignedOwner) || assignedOwner.IsRoot || assignedOwner.ProjectId != owner.ProjectId))
				{
					return AgentControlResult.Refused("invalid-argument", "The assigned session must belong to this project.");
				}
				var result = await _host.MutateProjectBoardAsync(owner.ProjectId, command, new(sessionId, sessionId), cancellationToken).ConfigureAwait(false);
				return AgentControlResult.Ok(Serialize(BoardToolResponses.Mutation(result)));
			}
			var snapshot = await _host.ReadProjectBoardAsync(owner.ProjectId, cancellationToken).ConfigureAwait(false);
			return AgentControlResult.Ok(Serialize(BoardToolResponses.Read(snapshot, request)));
		}
		catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException)
		{
			return AgentControlResult.Refused("invalid-argument", exception.Message);
		}
		catch (KeyNotFoundException exception)
		{
			return AgentControlResult.Refused("not-found", exception.Message);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
		{
			return AgentControlResult.Refused("storage-error", exception.Message);
		}
	}
}

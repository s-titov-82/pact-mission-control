using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>Reads MCP tool arguments and words the error a caller gets for a bad one.</summary>
public static class AgentControlToolArguments
{
	/// <summary>Reads a string argument the caller must supply.</summary>
	public static bool TryGetRequiredString(
		JsonNode arguments,
		string name,
		out string value,
		out AgentControlResultData error)
	{
		value = string.Empty;
		if (arguments is not JsonObject argumentObject)
		{
			error = NotAnObject();
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = ArgumentError(name, "is required");
			return false;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<string>(out value!))
		{
			error = ArgumentError(name, "must be a string");
			return false;
		}

		error = null!;
		return true;
	}

	/// <summary>Reads a string argument the caller may leave out.</summary>
	public static bool TryGetOptionalString(
		JsonNode arguments,
		string name,
		out string? value,
		out AgentControlResultData error)
	{
		value = null;
		if (arguments is not JsonObject argumentObject)
		{
			error = NotAnObject();
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = null!;
			return true;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<string>(out value))
		{
			error = ArgumentError(name, "must be a string");
			return false;
		}

		error = null!;
		return true;
	}

	/// <summary>Reads an integer argument the caller may leave out.</summary>
	public static bool TryGetOptionalInt32(
		JsonNode arguments,
		string name,
		out int? value,
		out AgentControlResultData error)
	{
		value = null;
		if (arguments is not JsonObject argumentObject)
		{
			error = NotAnObject();
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = null!;
			return true;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<int>(out var parsed))
		{
			error = ArgumentError(name, "must be an integer");
			return false;
		}

		value = parsed;
		error = null!;
		return true;
	}

	private static AgentControlResultData NotAnObject() =>
		new("Tool arguments must be a JSON object.", IsError: true);

	private static AgentControlResultData ArgumentError(string name, string reason) =>
		new($"Argument '{name}' {reason}.", IsError: true);
}

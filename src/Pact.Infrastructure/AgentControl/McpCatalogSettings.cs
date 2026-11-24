using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Agents;

namespace Pact.Infrastructure.AgentControl;

/// <summary>A server registered in Pact for explicit selection by a launch profile.</summary>
/// <param name="Id">Stable identifier used as the suffix of the name in the agent.</param>
/// <param name="Url">Streamable HTTP endpoint, or null for a stdio server.</param>
/// <param name="Command">Executable for a stdio server, or null for HTTP.</param>
/// <param name="Arguments">Literal arguments to the stdio executable.</param>
/// <param name="BearerTokenVariable">Name of the environment variable supplying an HTTP bearer token.</param>
public sealed record McpCatalogServer(string Id, string? Url, string? Command, string[] Arguments, string? BearerTokenVariable)
{
	/// <summary>Additional environment variables to inherit when launching a stdio server.</summary>
	public string[] EnvironmentVariables { get; init; } = [];

	/// <summary>Fields from versions of the catalog newer than this one.</summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement>? Extra { get; init; }

	/// <summary>The name to find in the agent's MCP list.</summary>
	[JsonIgnore]
	public string AgentName => "pact_catalog_" + Id;
}

/// <summary>An explicit selection for one profile, optionally restricted to one project.</summary>
/// <param name="ProfileId">Stable launch-profile identifier.</param>
/// <param name="Kind">Agent kind, so reuse of the ID for another agent never transfers the rule.</param>
/// <param name="ProjectDirectory">Absolute project root, or null for the profile default.</param>
/// <param name="ServerId">Identifier in the Pact catalog.</param>
/// <param name="Enabled">Whether to connect when this selection applies.</param>
public sealed record McpCatalogSelection(string ProfileId, AgentKind Kind, string? ProjectDirectory, string ServerId, bool Enabled)
{
	/// <summary>Fields from versions of the catalog newer than this one.</summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Pact-owned server definitions and the defaults and exceptions selected by the user.</summary>
public sealed record McpCatalogSettings
{
	/// <summary>Server definitions approved in Pact.</summary>
	public McpCatalogServer[] Servers { get; init; } = [];
	/// <summary>Selections by profile and exact project root.</summary>
	public McpCatalogSelection[] Selections { get; init; } = [];
	/// <summary>Fields from versions of the catalog newer than this one.</summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Catalog contents as seen when preparing an Apply preview.</summary>
/// <param name="Settings">Parsed settings with unknown JSON properties preserved.</param>
/// <param name="Content">The exact file contents used to detect a stale preview.</param>
public sealed record McpCatalogSnapshot(McpCatalogSettings Settings, string? Content);

/// <summary>Safe metadata about a server declared in an agent-owned configuration file.</summary>
/// <param name="Name">Name used by the native agent.</param>
/// <param name="Transport">Transport label inferred without retaining the definition.</param>
/// <param name="SourcePath">Absolute path to the declaring file.</param>
public sealed record NativeMcpServer(string Name, string Transport, string SourcePath);

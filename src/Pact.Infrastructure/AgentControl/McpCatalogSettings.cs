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

	/// <summary>When present, reuse the original native declaration on profiles that already load it.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public McpNativeServerOrigin? NativeOrigin { get; init; }

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
/// <param name="ProjectDirectory">Absolute project or folder path, or null for the profile default.</param>
/// <param name="ServerId">Identifier in the Pact catalog.</param>
/// <param name="Enabled">Whether to connect when this selection applies.</param>
public sealed record McpCatalogSelection(string ProfileId, AgentKind Kind, string? ProjectDirectory, string ServerId, bool Enabled)
{
	/// <summary>Applies this choice to the folder and its descendants, independently of any exact project choice.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool IsFolder { get; init; }

	/// <summary>Targets the agent's native server name instead of a Pact catalog ID.</summary>
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
	public bool IsNative { get; init; }

	/// <summary>Fields from versions of the catalog newer than this one.</summary>
	[JsonExtensionData]
	public Dictionary<string, JsonElement>? Extra { get; init; }
}

/// <summary>Pact-owned server definitions and the defaults and exceptions selected by the user.</summary>
public sealed record McpCatalogSettings
{
	/// <summary>Server definitions approved in Pact.</summary>
	public McpCatalogServer[] Servers { get; init; } = [];
	/// <summary>Selections by profile, optionally scoped to a project or folder.</summary>
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
public sealed record NativeMcpServer(string Name, string Transport, string SourcePath)
{
	/// <summary>The exact project that declares the server, or null for its user declaration.</summary>
	public string? ProjectDirectory { get; init; }

	/// <summary>The native switch for this declaration, or null when its settings could not be read.</summary>
	public bool? IsEnabled { get; init; } = true;
	/// <summary>Native settings files with rules that block this declaration in the inspected scope.</summary>
	public string? BlockingSettings { get; init; }
	/// <summary>Whether enabling this declaration can clear all identified blocks with its own native switch.</summary>
	public bool CanEnable { get; init; } = true;

	/// <summary>Whether a project-only Codex declaration has explicit trust in the profile's config.</summary>
	public bool IsProjectConfigurationTrusted { get; init; } = true;

	/// <summary>Identifies the declaration independently of its editable configuration and availability.</summary>
	public bool MatchesDeclaration(NativeMcpServer? other) => other is not null && Name == other.Name &&
		StringComparer.OrdinalIgnoreCase.Equals(SourcePath, other.SourcePath) &&
		StringComparer.OrdinalIgnoreCase.Equals(ProjectDirectory, other.ProjectDirectory);
}

/// <summary>Identifies the source of a shared MCP definition without storing the declaring file's contents.</summary>
/// <param name="Kind">Agent kind that owns the source.</param>
/// <param name="Name">Name under which the owning agent loads the server.</param>
/// <param name="SourcePath">Absolute path to the declaring file.</param>
/// <param name="ProjectDirectory">Exact declaring project, or null for the user declaration.</param>
public sealed record McpNativeServerOrigin(AgentKind Kind, string Name, string SourcePath, string? ProjectDirectory);

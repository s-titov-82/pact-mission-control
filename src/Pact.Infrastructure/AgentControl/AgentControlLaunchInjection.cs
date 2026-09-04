using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Launch additions that connect one session's agent to the Pact endpoint.</summary>
/// <param name="Arguments">Raw values appended after the launch route is selected.</param>
/// <param name="EnvironmentVariables">Variables the child process needs, including any credential.</param>
public sealed record LaunchInjection(
	IReadOnlyList<string> Arguments,
	IReadOnlyDictionary<string, string> EnvironmentVariables);

/// <summary>
/// Materializes the carriers explicitly requested by a profile's agent-control argument template.
/// </summary>
public static class AgentControlLaunchInjection
{
	private const string ConfigPathPlaceholder = "{configPath}";
	private const string EndpointUrlPlaceholder = "{endpointUrl}";
	private const string TokenEnvironmentPlaceholder = "{tokenEnvVar}";
	private const string TokenEnvironmentVariable = "PACT_AGENT_CONTROL_TOKEN";
	private const string SessionEnvironmentVariable = "PACT_SESSION_ID";

	/// <summary>
	/// Builds launch arguments and environment without inferring behavior from the agent kind.
	/// </summary>
	/// <remarks>
	/// A blank template is an explicit opt-out. A configuration file is written only when
	/// <c>{configPath}</c> occurs. Either carrier keeps the credential out of the command line and
	/// out of the file: <c>{configPath}</c> writes a document that reads the credential from the
	/// environment, and <c>{tokenEnvVar}</c> names that same variable in the arguments. Both put
	/// the credential in the child environment.
	/// </remarks>
	public static LaunchInjection Create(
		IReadOnlyList<string>? agentControlArgumentTemplate,
		IReadOnlyList<string>? instructionArguments,
		string configurationDirectory,
		string sessionId,
		Uri endpoint,
		string? token)
	{
		IReadOnlyList<string> agentControlArguments = agentControlArgumentTemplate ?? [];
		if (agentControlArguments.Count == 0)
		{
			return new LaunchInjection(
				instructionArguments?.ToArray() ?? [],
				new Dictionary<string, string>(StringComparer.Ordinal));
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(configurationDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(endpoint);
		ArgumentException.ThrowIfNullOrWhiteSpace(token);

		List<string> arguments = new(agentControlArguments.Count
			+ (instructionArguments?.Count ?? 0));
		Dictionary<string, string> environment = new(StringComparer.Ordinal)
		{
			[SessionEnvironmentVariable] = sessionId
		};

		foreach (string templateArgument in agentControlArguments)
		{
			string argument = templateArgument;
			if (argument.Contains(ConfigPathPlaceholder, StringComparison.Ordinal))
			{
				var configPath = WriteConfiguration(configurationDirectory, endpoint);
				argument = argument.Replace(
					ConfigPathPlaceholder,
					configPath,
					StringComparison.Ordinal);
				// The document names the variable rather than the credential, so the file itself
				// holds no secret and every session reads its own token from its own environment.
				environment[TokenEnvironmentVariable] = token;
			}

			if (argument.Contains(EndpointUrlPlaceholder, StringComparison.Ordinal))
			{
				argument = argument.Replace(
					EndpointUrlPlaceholder,
					endpoint.AbsoluteUri,
					StringComparison.Ordinal);
			}

			if (argument.Contains(TokenEnvironmentPlaceholder, StringComparison.Ordinal))
			{
				argument = argument.Replace(
					TokenEnvironmentPlaceholder,
					TokenEnvironmentVariable,
					StringComparison.Ordinal);
				environment[TokenEnvironmentVariable] = token;
			}

			arguments.Add(argument);
		}

		if (instructionArguments is not null)
		{
			arguments.AddRange(instructionArguments);
		}

		return new LaunchInjection(arguments, environment);
	}

	/// <remarks>
	/// One document serves every session: it declares the remote transport, the endpoint, and the
	/// name of the variable carrying the credential, so nothing in it is session-specific or
	/// secret. The declared type matters — an agent that cannot tell a remote server from a local
	/// one rejects the configuration.
	/// </remarks>
	private static string WriteConfiguration(string stagingDirectory, Uri endpoint)
	{
		Directory.CreateDirectory(stagingDirectory);
		var path = Path.Combine(stagingDirectory, "pact-mcp.json");
		var temporaryPath = Path.Combine(stagingDirectory, $".pact-mcp-{Guid.NewGuid():N}.tmp");
		var json = new JsonObject
		{
			["mcpServers"] = new JsonObject
			{
				["pact"] = new JsonObject
				{
					["type"] = "http",
					["url"] = endpoint.AbsoluteUri,
					["headers"] = new JsonObject
					{
						["Authorization"] = $"Bearer ${{{TokenEnvironmentVariable}}}"
					}
				}
			}
		}.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

		try
		{
			File.WriteAllText(temporaryPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
			File.Move(temporaryPath, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}

		return path;
	}
}

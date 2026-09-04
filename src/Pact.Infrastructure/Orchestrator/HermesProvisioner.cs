using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace Pact.Infrastructure.Orchestrator;

/// <summary>Outcome of one independently reportable Hermes provisioning step.</summary>
public enum ProvisionOutcome
{
	/// <summary>The artifact was created.</summary>
	Created,

	/// <summary>The artifact already matched the requested state.</summary>
	AlreadyPresent,

	/// <summary>The artifact was updated.</summary>
	Updated,

	/// <summary>The prior user-owned content was backed up.</summary>
	BackedUp,

	/// <summary>The step was intentionally skipped.</summary>
	Skipped,

	/// <summary>The step failed and dependent work stopped.</summary>
	Failed
}

/// <summary>One independently visible Hermes provisioning action.</summary>
/// <param name="Name">Stable artifact or action name.</param>
/// <param name="Outcome">Result category.</param>
/// <param name="Detail">Human-readable result detail.</param>
public sealed record ProvisionStep(
	string Name,
	ProvisionOutcome Outcome,
	string Detail);

/// <summary>
/// Provisions Pact-owned parts of a Hermes profile while preserving all unowned configuration.
/// </summary>
public sealed class HermesProvisioner
{
	private const string McpUrlKey = "PACT_MCP_URL";
	private const string McpTokenKey = "PACT_MCP_TOKEN";
	private readonly IHermesCli _cli;

	/// <summary>Creates a provisioner over the Hermes-owned profile creation boundary.</summary>
	public HermesProvisioner(IHermesCli cli)
	{
		ArgumentNullException.ThrowIfNull(cli);
		_cli = cli;
	}

	/// <summary>Creates or updates the Pact Hermes profile and reports every artifact separately.</summary>
	public async Task<IReadOnlyList<ProvisionStep>> ProvisionAsync(
		string hermesHome,
		string profileName,
		string endpointUrl,
		string credential,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(hermesHome);
		ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
		ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
		ArgumentException.ThrowIfNullOrWhiteSpace(credential);

		List<ProvisionStep> steps = [];
		if (!_cli.IsInstalled())
		{
			steps.Add(new ProvisionStep(
				"profile",
				ProvisionOutcome.Failed,
				"hermes is not installed; see https://github.com/NousResearch/hermes-agent"));
			return steps;
		}

		var profileDirectory = Path.Combine(hermesHome, "profiles", profileName);
		if (!Directory.Exists(profileDirectory))
		{
			var result = await _cli.CreateProfileAsync(profileName, cancellationToken)
				.ConfigureAwait(false);
			if (!result.Succeeded)
			{
				steps.Add(new ProvisionStep(
					"profile",
					ProvisionOutcome.Failed,
					result.Output));
				return steps;
			}

			if (!Directory.Exists(profileDirectory))
			{
				steps.Add(new ProvisionStep(
					"profile",
					ProvisionOutcome.Failed,
					$"Hermes reported success but profile '{profileDirectory}' was not created."));
				return steps;
			}

			steps.Add(new ProvisionStep(
				"profile",
				ProvisionOutcome.Created,
				$"Hermes created profile '{profileName}'."));
		}
		else
		{
			steps.Add(new ProvisionStep(
				"profile",
				ProvisionOutcome.AlreadyPresent,
				$"Hermes profile '{profileName}' already exists."));
		}

		var configSucceeded = await ProvisionConfigAsync(
			profileDirectory,
			steps,
			cancellationToken).ConfigureAwait(false);
		if (!configSucceeded)
		{
			return steps;
		}

		await ProvisionTemplateAsync(
			profileDirectory,
			"SOUL.md",
			HermesTemplates.SoulMarkdown,
			steps,
			cancellationToken).ConfigureAwait(false);

		var skillDirectory = Path.Combine(
			profileDirectory,
			"skills",
			"pact-status-report");
		Directory.CreateDirectory(skillDirectory);
		await ProvisionTemplateAsync(
			skillDirectory,
			"SKILL.md",
			HermesTemplates.StatusReportSkill,
			steps,
			cancellationToken,
			stepName: "pact-status-report").ConfigureAwait(false);

		await ProvisionEnvironmentAsync(
			profileDirectory,
			endpointUrl,
			credential,
			steps,
			cancellationToken).ConfigureAwait(false);
		return steps;
	}

	private static async Task<bool> ProvisionConfigAsync(
		string profileDirectory,
		List<ProvisionStep> steps,
		CancellationToken cancellationToken)
	{
		var path = Path.Combine(profileDirectory, "config.yaml");
		try
		{
			var stream = new YamlStream();
			string? original = null;
			if (File.Exists(path))
			{
				original = await File.ReadAllTextAsync(path, cancellationToken)
					.ConfigureAwait(false);
				if (!string.IsNullOrWhiteSpace(original))
				{
					using StringReader reader = new(original);
					stream.Load(reader);
				}
			}

			var root = GetOrCreateRoot(stream);
			if (original is not null && !HasOwnedPactBlock(root))
			{
				var backup = Backup(path);
				steps.Add(new ProvisionStep(
					"config.yaml",
					ProvisionOutcome.BackedUp,
					$"Original configuration backed up to '{backup}'."));
			}

			SetPactBlock(root);
			using StringWriter writer = new(CultureInfo.InvariantCulture);
			stream.Save(writer, assignAnchors: false);
			await File.WriteAllTextAsync(path, writer.ToString(), cancellationToken)
				.ConfigureAwait(false);
			steps.Add(new ProvisionStep(
				"config.yaml",
				original is null ? ProvisionOutcome.Created : ProvisionOutcome.Updated,
				"Configured the Pact MCP server."));
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException)
		{
			steps.Add(new ProvisionStep(
				"config.yaml",
				ProvisionOutcome.Failed,
				ex.Message));
			return false;
		}
	}

	private static YamlMappingNode GetOrCreateRoot(YamlStream stream)
	{
		if (stream.Documents.Count == 0)
		{
			YamlMappingNode root = [];
			stream.Add(new YamlDocument(root));
			return root;
		}

		if (stream.Documents[0].RootNode is not YamlMappingNode mapping)
		{
			throw new YamlDotNet.Core.YamlException(
				"Hermes config.yaml root must be a mapping.");
		}

		return mapping;
	}

	private static bool HasOwnedPactBlock(YamlMappingNode root)
	{
		return TryGetMapping(root, "mcp_servers", out var servers)
			&& TryGetMapping(servers, "pact", out var pact)
			&& TryGetScalar(pact, "url", out var url)
			&& url == "${PACT_MCP_URL}"
			&& TryGetMapping(pact, "headers", out var headers)
			&& TryGetScalar(headers, "Authorization", out var authorization)
			&& authorization == "Bearer ${PACT_MCP_TOKEN}";
	}

	private static void SetPactBlock(YamlMappingNode root)
	{
		var servers = GetOrCreateMapping(root, "mcp_servers");
		YamlMappingNode headers = new();
		headers.Add("Authorization", "Bearer ${PACT_MCP_TOKEN}");
		YamlMappingNode pact = new();
		pact.Add("url", "${PACT_MCP_URL}");
		pact.Add("headers", headers);
		SetChild(servers, "pact", pact);
	}

	private static YamlMappingNode GetOrCreateMapping(
		YamlMappingNode parent,
		string key)
	{
		if (TryGetMapping(parent, key, out var existing))
		{
			return existing;
		}

		YamlMappingNode created = [];
		SetChild(parent, key, created);
		return created;
	}

	private static bool TryGetMapping(
		YamlMappingNode parent,
		string key,
		out YamlMappingNode mapping)
	{
		if (TryGetChild(parent, key, out var child) && child is YamlMappingNode value)
		{
			mapping = value;
			return true;
		}

		mapping = null!;
		return false;
	}

	private static bool TryGetScalar(
		YamlMappingNode parent,
		string key,
		out string value)
	{
		if (TryGetChild(parent, key, out var child) && child is YamlScalarNode scalar)
		{
			value = scalar.Value ?? string.Empty;
			return true;
		}

		value = string.Empty;
		return false;
	}

	private static bool TryGetChild(
		YamlMappingNode parent,
		string key,
		out YamlNode child)
	{
		foreach (var pair in parent.Children)
		{
			if (pair.Key is YamlScalarNode scalar && scalar.Value == key)
			{
				child = pair.Value;
				return true;
			}
		}

		child = null!;
		return false;
	}

	private static void SetChild(YamlMappingNode parent, string key, YamlNode value)
	{
		var existingKey = parent.Children.Keys
			.OfType<YamlScalarNode>()
			.FirstOrDefault(candidate => candidate.Value == key);
		if (existingKey is null)
		{
			parent.Add(key, value);
		}
		else
		{
			parent.Children[existingKey] = value;
		}
	}

	private static async Task ProvisionTemplateAsync(
		string directory,
		string fileName,
		string template,
		List<ProvisionStep> steps,
		CancellationToken cancellationToken,
		string? stepName = null)
	{
		var name = stepName ?? fileName;
		var path = Path.Combine(directory, fileName);
		var outcome = ProvisionOutcome.Created;
		if (File.Exists(path))
		{
			var existing = await File.ReadAllTextAsync(path, cancellationToken)
				.ConfigureAwait(false);
			if (existing == template)
			{
				steps.Add(new ProvisionStep(
					name,
					ProvisionOutcome.AlreadyPresent,
					$"'{fileName}' already matches the Pact template."));
				return;
			}

			var backup = Backup(path);
			steps.Add(new ProvisionStep(
				name,
				ProvisionOutcome.BackedUp,
				$"Existing '{fileName}' backed up to '{backup}'."));
			outcome = ProvisionOutcome.Updated;
		}

		Directory.CreateDirectory(directory);
		await File.WriteAllTextAsync(path, template, cancellationToken)
			.ConfigureAwait(false);
		steps.Add(new ProvisionStep(name, outcome, $"Installed '{fileName}'."));
	}

	private static async Task ProvisionEnvironmentAsync(
		string profileDirectory,
		string endpointUrl,
		string credential,
		List<ProvisionStep> steps,
		CancellationToken cancellationToken)
	{
		var path = Path.Combine(profileDirectory, ".env");
		var existed = File.Exists(path);
		var lines = existed
			? (await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false)).ToList()
			: [];
		SetEnvironmentValue(lines, McpUrlKey, endpointUrl);
		SetEnvironmentValue(lines, McpTokenKey, credential);
		await File.WriteAllLinesAsync(path, lines, cancellationToken).ConfigureAwait(false);
		steps.Add(new ProvisionStep(
			".env",
			existed ? ProvisionOutcome.Updated : ProvisionOutcome.Created,
			"Configured Pact endpoint variables."));
	}

	private static void SetEnvironmentValue(
		List<string> lines,
		string key,
		string value)
	{
		var prefix = $"{key}=";
		var index = lines.FindIndex(line =>
			line.StartsWith(prefix, StringComparison.Ordinal));
		var assignment = $"{prefix}{value}";
		if (index < 0)
		{
			lines.Add(assignment);
		}
		else
		{
			lines[index] = assignment;
		}
	}

	private static string Backup(string path)
	{
		var backup = string.Create(
			CultureInfo.InvariantCulture,
			$"{path}.{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}.bak");
		File.Copy(path, backup, overwrite: false);
		return backup;
	}
}

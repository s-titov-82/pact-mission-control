using System.Reflection;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.AgentControl;

/// <summary>
/// Materializes the current built-in Pact guidance under retained application-owned storage.
/// </summary>
public sealed class PactSkillPublisher
{
	private const string ResourcePrefix = "Pact.Infrastructure.AgentControl.PactSkills.";
	private readonly AppPaths _paths;

	/// <summary>Creates a publisher for the canonical paths below the supplied data root.</summary>
	public PactSkillPublisher(AppPaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	/// <summary>
	/// Atomically replaces both retained files and returns their paths only after both writes finish.
	/// </summary>
	public async Task<PactSkillPublication> PublishAsync(CancellationToken cancellationToken)
	{
		Assembly assembly = typeof(PactSkillPublisher).Assembly;
		string mcpContent = await ReadResourceAsync(
			assembly,
			$"{ResourcePrefix}PactMcpSkill.md",
			cancellationToken);
		string commonContent = await ReadResourceAsync(
			assembly,
			$"{ResourcePrefix}PactCommonSkill.md",
			cancellationToken);

		await AtomicFileWriter.WriteTextAsync(
			_paths.PactMcpSkillPath,
			Normalize(mcpContent),
			cancellationToken);
		await AtomicFileWriter.WriteTextAsync(
			_paths.PactCommonSkillPath,
			Normalize(commonContent),
			cancellationToken);

		return new PactSkillPublication(_paths.PactMcpSkillPath, _paths.PactCommonSkillPath);
	}

	private static async Task<string> ReadResourceAsync(
		Assembly assembly,
		string resourceName,
		CancellationToken cancellationToken)
	{
		await using Stream stream = assembly.GetManifestResourceStream(resourceName)
			?? throw new InvalidOperationException(
				$"Embedded Pact skill resource '{resourceName}' was not found.");
		using StreamReader reader = new(stream);
		return await reader.ReadToEndAsync(cancellationToken);
	}

	private static string Normalize(string content) =>
		content.ReplaceLineEndings("\n").TrimEnd() + "\n";
}

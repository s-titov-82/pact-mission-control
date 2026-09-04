using System.Text.Json;
using Pact.Core.Git;
using Pact.Core.Platform;

namespace Pact.Infrastructure.Tests.Git;

public sealed class ExternalGitHelperResolverTests : IDisposable
{
	private readonly List<TemporaryDirectory> _temporaryDirectories = [];

	[Test]
	public async Task ResolveAsync_returns_actions_for_existing_absolute_executable()
	{
		var root = CreateTempDirectory();
		var executable = Path.Combine(root, "helper.exe");
		await File.WriteAllTextAsync(executable, string.Empty);
		var path = Path.Combine(root, "git-helpers.json");
		await WriteDocumentAsync(path, new GitHelpersDocument(
		[
			new ExternalGitHelperDefinition(
				"custom",
				"Custom Helper",
				executable,
				WindowsRegistryProbe: null,
				Actions:
				[
					new ExternalGitHelperAction("history", "History", ["browse", "{root}"])
				])
		]));
		var resolver = CreateResolver(path);

		var actions = await resolver.ResolveAsync(CancellationToken.None);

		var action = actions.ShouldHaveSingleItem();
		action.HelperName.ShouldBe("Custom Helper");
		action.Slot.ShouldBe("history");
		action.Label.ShouldBe("History");
		action.Executable.ShouldBe(executable);
	}

	[Test]
	public async Task ResolveAsync_hides_helpers_when_executable_cannot_be_resolved()
	{
		var root = CreateTempDirectory();
		var path = Path.Combine(root, "git-helpers.json");
		await WriteDocumentAsync(path, new GitHelpersDocument(
		[
			new ExternalGitHelperDefinition(
				"missing",
				"Missing Helper",
				"agentterminal-missing-helper.exe",
				WindowsRegistryProbe: null,
				Actions:
				[
					new ExternalGitHelperAction("history", "History", ["browse", "{root}"])
				])
		]));
		var resolver = CreateResolver(path);

		var actions = await resolver.ResolveAsync(CancellationToken.None);

		actions.ShouldBeEmpty();
	}

	[Test]
	public async Task ResolveAsync_returns_empty_list_for_malformed_file()
	{
		var root = CreateTempDirectory();
		var path = Path.Combine(root, "git-helpers.json");
		await File.WriteAllTextAsync(path, "{not-json");
		var resolver = CreateResolver(path);

		var actions = await resolver.ResolveAsync(CancellationToken.None);

		actions.ShouldBeEmpty();
	}

	[Test]
	public async Task LoadCommandsAsync_returns_configured_commands()
	{
		var root = CreateTempDirectory();
		var path = Path.Combine(root, "git-helpers.json");
		await WriteDocumentAsync(path, new GitHelpersDocument(
			[],
			[new GitButtonCommandRecord("pull", "Pull", Command: "pull --rebase")]));
		var resolver = CreateResolver(path);

		var commands = await resolver.LoadCommandsAsync(CancellationToken.None);

		commands.Arguments("pull").ShouldBe(["pull", "--rebase"]);
	}

	[Test]
	public async Task LoadCommandsAsync_returns_defaults_for_missing_array_or_malformed_file()
	{
		var root = CreateTempDirectory();
		var withoutCommands = Path.Combine(root, "git-helpers.json");
		await WriteDocumentAsync(withoutCommands, new GitHelpersDocument([]));
		var malformed = Path.Combine(root, "broken.json");
		await File.WriteAllTextAsync(malformed, "{not-json");

		var fromMissing = await CreateResolver(withoutCommands)
			.LoadCommandsAsync(CancellationToken.None);
		var fromMalformed = await CreateResolver(malformed)
			.LoadCommandsAsync(CancellationToken.None);

		fromMissing.Arguments("pull").ShouldBe(["pull", "--no-rebase"]);
		fromMalformed.Arguments("pull").ShouldBe(["pull", "--no-rebase"]);
	}

	private static async Task WriteDocumentAsync(string path, GitHelpersDocument document) => await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(document, SettingsFileStore.JsonOptions));

	private string CreateTempDirectory()
	{
		var directory = TemporaryDirectory.Create();
		_temporaryDirectories.Add(directory);
		return directory.Path;
	}

	public void Dispose() => _temporaryDirectories.ForEach(static directory => directory.Dispose());

	private static ExternalGitHelperResolver CreateResolver(string path) =>
		new(path, NullExecutableLocator.Instance);

	private sealed class NullExecutableLocator : IExecutableLocator
	{
		public static NullExecutableLocator Instance { get; } = new();

		public string? FindOnPath(string executableName) => null;
	}
}

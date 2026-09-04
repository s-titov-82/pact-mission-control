using System.Text.Json;
using Pact.Core.Git;

namespace Pact.Core.Tests.Git;

public sealed class ExternalGitHelperDefinitionTests
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	[Test]
	public void GitHelpersDocument_round_trips_with_camel_case_json()
	{
		GitHelpersDocument document = new(
		[
			new ExternalGitHelperDefinition(
				"tortoisegit",
				"TortoiseGit",
				"TortoiseGitProc.exe",
				new WindowsRegistryProbe(@"SOFTWARE\TortoiseGit", "ProcPath"),
				[
					new ExternalGitHelperAction("history", "History", ["/command:log", "/path:{root}"]),
					new ExternalGitHelperAction("resolve", "Resolve", ["/command:resolve", "/path:{root}"])
				])
		]);

		var json = JsonSerializer.Serialize(document, JsonOptions);
		var restored = JsonSerializer.Deserialize<GitHelpersDocument>(json, JsonOptions)!;
		var helper = restored.Helpers.ShouldHaveSingleItem();

		helper.Id.ShouldBe("tortoisegit");
		helper.Name.ShouldBe("TortoiseGit");
		helper.Executable.ShouldBe("TortoiseGitProc.exe");
		helper.WindowsRegistryProbe.ShouldNotBeNull();
		helper.WindowsRegistryProbe.Key.ShouldBe(@"SOFTWARE\TortoiseGit");
		helper.WindowsRegistryProbe.Value.ShouldBe("ProcPath");
		helper.Actions.Count.ShouldBe(2);
		helper.Actions[0].Slot.ShouldBe("history");
		helper.Actions[0].Label.ShouldBe("History");
		helper.Actions[0].Arguments.ShouldBe(["/command:log", "/path:{root}"]);
		helper.Actions[1].Slot.ShouldBe("resolve");
		helper.Actions[1].Label.ShouldBe("Resolve");
		helper.Actions[1].Arguments.ShouldBe(["/command:resolve", "/path:{root}"]);
	}

	[Test]
	public void SubstituteArguments_replaces_root_and_branch_inside_each_argument_without_splitting()
	{
		ExternalGitHelperAction action = new(
			"history",
			"History",
			["/command:log", "/path:{root}", "/rev:{branch}", "literal-{branch}-{root}"]);

		var arguments = ExternalGitHelperDefinition.SubstituteArguments(
			action,
			@"D:\Repos\Project With Spaces",
			"feature/x");

		arguments.Count.ShouldBe(4);
		arguments.ShouldBe(["/command:log", @"/path:D:\Repos\Project With Spaces", "/rev:feature/x", @"literal-feature/x-D:\Repos\Project With Spaces"]);
	}

	[Test]
	public void Unknown_slot_values_deserialize_as_strings_for_forward_compatibility()
	{
		var json = """
            {
              "helpers": [
                {
                  "id": "future",
                  "name": "Future",
                  "executable": "future.exe",
                  "actions": [
                    { "slot": "future-slot", "label": "Future", "arguments": ["{root}"] }
                  ]
                }
              ]
            }
            """;

		var document = JsonSerializer.Deserialize<GitHelpersDocument>(json, JsonOptions)!;
		var action = document.Helpers.ShouldHaveSingleItem().Actions.ShouldHaveSingleItem();

		action.Slot.ShouldBe("future-slot");
		document.Helpers
			.SelectMany(helper => helper.Actions)
			.Any(action => action.Slot is "history" or "custom")
			.ShouldBeFalse();
	}
}
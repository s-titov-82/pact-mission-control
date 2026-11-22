using System.Text.Json.Nodes;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillRenameReferencesTests
{
	[Test]
	public void PreviewMovesCodexAndPactPathRulesAndPreservesUnrelatedNameRules()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var native = Path.Combine(temp.Path, ".codex");
		var old = Directory.CreateDirectory(Path.Combine(native, "skills", "old")).FullName;
		var next = Path.Combine(native, "skills", "next");
		var oldFile = Path.Combine(old, "SKILL.md");
		var newFile = Path.Combine(next, "SKILL.md");
		var config = Path.Combine(native, "config.toml");
		var content = $"# keep\n[[skills.config]]\npath='{oldFile}' # installed\nenabled=false\n[[skills.config]]\nname='other'\nenabled=true\n";
		File.WriteAllText(config, content);
		var policy = Path.Combine(Directory.CreateDirectory(Path.Combine(paths.SettingsDirectory, "Skills", "Policies")).FullName, "profile.json");
		var project = Path.Combine(Directory.CreateDirectory(Path.Combine(paths.SettingsDirectory, "Skills", "ProjectRules")).FullName, "project.json");
		var key = oldFile.ToUpperInvariant();
		File.WriteAllText(policy, new JsonObject { ["children"] = new JsonObject { [key] = false }, ["materialized"] = new JsonObject { [key] = true } }.ToJsonString());
		File.WriteAllText(project, new JsonObject { [oldFile] = true }.ToJsonString());

		var edits = SkillRenameReferences.Preview(paths, [], [], new Dictionary<string, string> { [old] = next }, [], "stable", temp.Path)
			.ToDictionary(edit => edit.Path, edit => edit.Updated!);

		edits.Count.ShouldBe(3);
		File.ReadAllText(config).ShouldBe(content);
		edits[config].ShouldContain("# installed");
		var nativeRules = CodexSkillRules.Read(edits[config]);
		nativeRules.Count.ShouldBe(2);
		nativeRules[0].ShouldBe(new CodexSkillRule("path", newFile, false));
		nativeRules[1].ShouldBe(new CodexSkillRule("name", "other", true));
		var doc = JsonNode.Parse(edits[policy])!;
		doc["children"]![key].ShouldBeNull();
		doc["children"]![newFile.ToUpperInvariant()]!.GetValue<bool>().ShouldBeFalse();
		doc["materialized"]![newFile.ToUpperInvariant()]!.GetValue<bool>().ShouldBeTrue();
		JsonNode.Parse(edits[project])![newFile]!.GetValue<bool>().ShouldBeTrue();
	}

	[Test]
	public void ClaudeRetainsACommandUsedByAProjectAndDetectsConflictingDestinationSettings()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var native = Path.Combine(temp.Path, ".claude");
		var old = Directory.CreateDirectory(Path.Combine(native, "skills", "old")).FullName;
		var next = Path.Combine(native, "skills", "next");
		var project = Path.Combine(temp.Path, "project");
		var other = Directory.CreateDirectory(Path.Combine(project, ".claude", "skills", "old"));
		File.WriteAllText(Path.Combine(other.FullName, "SKILL.md"), "another package");
		var config = Path.Combine(native, "settings.json");
		var local = Path.Combine(project, ".claude", "settings.local.json");
		const string settings = """{"skillOverrides":{"old":"off","other":"user-invocable-only"},"additional":17}""";
		File.WriteAllText(config, settings);
		File.WriteAllText(local, settings);

		var moves = new Dictionary<string, string> { [old] = next };
		var edits = SkillRenameReferences.Preview(paths, [], [project], moves, [], "unchanged", temp.Path);
		edits.Count.ShouldBe(2);
		foreach (var edit in edits)
		{
			var doc = JsonNode.Parse(edit.Updated!)!;
			doc["skillOverrides"]!["old"]!.GetValue<string>().ShouldBe("off");
			doc["skillOverrides"]!["next"]!.GetValue<string>().ShouldBe("off");
			doc["skillOverrides"]!["other"]!.GetValue<string>().ShouldBe("user-invocable-only");
			doc["additional"]!.GetValue<int>().ShouldBe(17);
		}

		File.WriteAllText(local, """{"skillOverrides":{"old":"off","next":"on"}}""");
		Should.Throw<IOException>(() => SkillRenameReferences.Preview(paths, [], [project], moves, [], "unchanged", temp.Path))
			.Message.ShouldContain(local);
		File.ReadAllText(config).ShouldBe(settings);
	}
}
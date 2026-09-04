using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class WebMonitoringRulesSectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _dir => _temporaryDirectory.Path;

	private SettingsFileStore Store => new(_dir);

	private string FilePath => new AppPaths(_dir).WebMonitorRulesPath;

	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task Load_maps_all_fields_and_save_preserves_unknown_rule_and_extractor_properties()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		var teamCity = root[0]!.AsObject();
		teamCity["urlPattern"] = "^https://CHANGE-ME-ci.example.test/(?:.*)$";
		teamCity["customRuleProperty"] = "keep-rule";
		teamCity["activity"]!["customExtractorProperty"] = "keep-activity";
		teamCity["revision"]!["customExtractorProperty"] = "keep-revision";
		await File.WriteAllTextAsync(
			FilePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = CreateSection();
		await section.LoadAsync(CancellationToken.None);

		var item =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		item.Id.ShouldBe("teamcity-builds-example");
		item.Title.ShouldBe("TeamCity builds");
		item.Enabled.ShouldBeFalse();
		item.UrlPattern.ShouldBe("^https://CHANGE-ME-ci.example.test/(?:.*)$");
		item.PollIntervalSecondsText.ShouldBe("30");
		item.HasActivityExtractor.ShouldBeTrue();
		item.ActivityExtractor.Selector.ShouldBe(".build.running");
		item.ActivityExtractor.Source.ShouldBe(WebMonitorValueSource.Count);
		item.HasRevisionExtractor.ShouldBeTrue();
		item.RevisionExtractor.Selector.ShouldBe(".build.finished:first-child");
		item.RevisionExtractor.Source.ShouldBe(WebMonitorValueSource.Text);
		item.RevisionExtractor.MatchPattern.ShouldBe(@"Build #(\d+)");
		item.RevisionExtractor.CaptureGroupText.ShouldBe("1");
		item.HasChangeMeMarker.ShouldBeTrue();

		item.Title = "Renamed TeamCity";
		item.ActivityExtractor.Selector = ".build.running.updated";
		item.RevisionExtractor.MatchPattern = @"Build (\d+)";

		section.IsDirty.ShouldBeTrue();
		item.TabHeader.ShouldBe("Renamed TeamCity •");
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();
		item.TabHeader.ShouldBe("Renamed TeamCity");

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!
			.AsArray()[0]!.AsObject();
		((string?)saved["customRuleProperty"]).ShouldBe("keep-rule");
		((string?)saved["activity"]!["customExtractorProperty"]).ShouldBe("keep-activity");
		((string?)saved["revision"]!["customExtractorProperty"]).ShouldBe("keep-revision");
		((string?)saved["activity"]!["selector"]).ShouldBe(".build.running.updated");
		((string?)saved["revision"]!["matchPattern"]).ShouldBe(@"Build (\d+)");
	}

	[Test]
	public async Task Idless_object_is_unrecognized_and_round_trips_untouched_without_blocking_valid_rules()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		JsonObject sentinel = new()
		{
			["futureRuleKind"] = "sentinel",
			["nested"] = new JsonObject
			{
				["unknownFlag"] = true,
				["unknownValues"] = new JsonArray("one", "two")
			}
		};
		root.Insert(1, sentinel);
		await File.WriteAllTextAsync(
			FilePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = CreateSection();
		await section.LoadAsync(CancellationToken.None);

		section.Items.Count.ShouldBe(3);
		var unknown =
			section.Items[1].ShouldBeOfType<UnrecognizedItemViewModel>();
		unknown.IsRecognized.ShouldBeFalse();
		var valid =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		valid.Title = "Still valid";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		JsonNode.DeepEquals(saved[1], sentinel).ShouldBeTrue();
		((string?)saved[0]!["title"]).ShouldBe("Still valid");
	}

	[TestCase("id")]
	[TestCase("enabled")]
	[TestCase("activity.selector")]
	public async Task Wrong_typed_rule_field_is_unrecognized_and_does_not_block_valid_sibling_save(
		string wrongTypedField)
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		var sentinel = root[0]!.DeepClone().AsObject();
		sentinel["id"] = "future-rule";
		switch (wrongTypedField)
		{
			case "id":
				sentinel["id"] = 123;
				break;
			case "enabled":
				sentinel["enabled"] = "false";
				break;
			case "activity.selector":
				sentinel["activity"]!["selector"] = 123;
				break;
		}

		root.Insert(1, sentinel);
		await File.WriteAllTextAsync(
			FilePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = CreateSection();
		await section.LoadAsync(CancellationToken.None);

		section.Items.Count.ShouldBe(3);
		var unknown =
			section.Items[1].ShouldBeOfType<UnrecognizedItemViewModel>();
		unknown.IsRecognized.ShouldBeFalse();
		var valid =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		valid.Title = $"Valid sibling after {wrongTypedField}";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		JsonNode.DeepEquals(saved[1], sentinel).ShouldBeTrue();
		((string?)saved[0]!["title"]).ShouldBe($"Valid sibling after {wrongTypedField}");
	}

	[Test]
	public async Task Duplicate_or_blank_ids_block_save()
	{
		var section = await LoadSectionAsync();
		var first =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		var second =
			section.Items[1].ShouldBeOfType<WebMonitorRuleItemViewModel>();

		second.Id = first.Id;
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("unique");

		second.Id = "unique";
		first.Id = " ";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("id");
	}

	[Test]
	public async Task Disabled_marker_example_saves_but_enabled_marker_is_rejected()
	{
		var section = await LoadSectionAsync();
		var item =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();

		item.Enabled.ShouldBeFalse();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		item.Enabled = true;
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("CHANGE-ME-");
	}

	[TestCase("url")]
	[TestCase("activity")]
	[TestCase("revision")]
	public async Task Invalid_regular_expression_blocks_save(string invalidField)
	{
		var section = await LoadSectionAsync();
		var item =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();

		switch (invalidField)
		{
			case "url":
				item.UrlPattern = "[";
				break;
			case "activity":
				item.ActivityExtractor.Source = WebMonitorValueSource.Text;
				item.ActivityExtractor.MatchPattern = "[";
				break;
			case "revision":
				item.RevisionExtractor.MatchPattern = "[";
				break;
		}

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("regular expression");
	}

	[Test]
	public async Task Extractor_presence_and_every_nested_field_round_trip()
	{
		var section = await LoadSectionAsync();
		var item =
			section.Items[1].ShouldBeOfType<WebMonitorRuleItemViewModel>();

		item.HasActivityExtractor = true;
		item.ActivityExtractor.Selector = ".busy";
		item.ActivityExtractor.Source = WebMonitorValueSource.Attribute;
		item.ActivityExtractor.AttributeName = "data-state";
		item.ActivityExtractor.MatchPattern = "running-(\\d+)";
		item.ActivityExtractor.CaptureGroupText = "1";
		item.RevisionExtractor.Selector = ".revision";
		item.RevisionExtractor.Source = WebMonitorValueSource.Attribute;
		item.RevisionExtractor.AttributeName = "data-revision";
		item.RevisionExtractor.MatchPattern = "(\\d+)";
		item.RevisionExtractor.CaptureGroupText = "1";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var reloaded = CreateSection();
		await reloaded.LoadAsync(CancellationToken.None);
		var saved =
			reloaded.Items[1].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		saved.HasActivityExtractor.ShouldBeTrue();
		saved.ActivityExtractor.Selector.ShouldBe(".busy");
		saved.ActivityExtractor.Source.ShouldBe(WebMonitorValueSource.Attribute);
		saved.ActivityExtractor.AttributeName.ShouldBe("data-state");
		saved.ActivityExtractor.MatchPattern.ShouldBe("running-(\\d+)");
		saved.ActivityExtractor.CaptureGroupText.ShouldBe("1");
		saved.RevisionExtractor.Selector.ShouldBe(".revision");
		saved.RevisionExtractor.Source.ShouldBe(WebMonitorValueSource.Attribute);
		saved.RevisionExtractor.AttributeName.ShouldBe("data-revision");
	}

	[Test]
	public async Task Disabled_activity_saves_null_for_runtime_and_restores_full_backup_when_reenabled()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		root[0]!["activity"]!["futureActivity"] = "keep-activity";
		await File.WriteAllTextAsync(
			FilePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var section = CreateSection();
		await section.LoadAsync(CancellationToken.None);
		var item =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		item.ActivityExtractor.Selector = ".edited-activity";
		item.ActivityExtractor.Source = WebMonitorValueSource.Attribute;
		item.ActivityExtractor.AttributeName = "data-state";
		item.ActivityExtractor.MatchPattern = "^running$";
		item.HasActivityExtractor = false;

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var disabled = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!
			.AsArray()[0]!.AsObject();
		disabled["activity"].ShouldBeNull();
		var backup = disabled["$pactDisabledActivity"]!.AsObject();
		((string?)backup["selector"]).ShouldBe(".edited-activity");
		((string?)backup["source"]).ShouldBe("attribute");
		((string?)backup["attributeName"]).ShouldBe("data-state");
		((string?)backup["matchPattern"]).ShouldBe("^running$");
		((string?)backup["futureActivity"]).ShouldBe("keep-activity");
		var runtimeDisabled =
			await Store.LoadWebMonitorRulesAsync(CancellationToken.None);
		runtimeDisabled[0].Activity.ShouldBeNull();

		var reloaded = CreateSection();
		await reloaded.LoadAsync(CancellationToken.None);
		var restored =
			reloaded.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		restored.HasActivityExtractor.ShouldBeFalse();
		restored.ActivityExtractor.Selector.ShouldBe(".edited-activity");
		restored.ActivityExtractor.Source.ShouldBe(WebMonitorValueSource.Attribute);
		restored.ActivityExtractor.AttributeName.ShouldBe("data-state");
		restored.ActivityExtractor.MatchPattern.ShouldBe("^running$");

		restored.HasActivityExtractor = true;
		(await reloaded.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var enabled = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!
			.AsArray()[0]!.AsObject();
		enabled["$pactDisabledActivity"].ShouldBeNull();
		((string?)enabled["activity"]!["futureActivity"]).ShouldBe("keep-activity");
		var runtimeEnabled =
			await Store.LoadWebMonitorRulesAsync(CancellationToken.None);
		var runtimeActivity =
			runtimeEnabled[0].Activity.ShouldNotBeNull();
		runtimeActivity.Selector.ShouldBe(".edited-activity");
		runtimeActivity.Source.ShouldBe(WebMonitorValueSource.Attribute);
	}

	[Test]
	public async Task Disabled_revision_saves_null_for_runtime_and_restores_full_backup_when_reenabled()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		root[0]!["revision"]!["futureRevision"] = "keep-revision";
		await File.WriteAllTextAsync(
			FilePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var section = CreateSection();
		await section.LoadAsync(CancellationToken.None);
		var item =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		item.RevisionExtractor.Selector = ".edited-revision";
		item.RevisionExtractor.Source = WebMonitorValueSource.Attribute;
		item.RevisionExtractor.AttributeName = "data-revision";
		item.RevisionExtractor.MatchPattern = "build-(\\d+)";
		item.RevisionExtractor.CaptureGroupText = "1";
		item.HasRevisionExtractor = false;

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var disabled = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!
			.AsArray()[0]!.AsObject();
		disabled["revision"].ShouldBeNull();
		var backup = disabled["$pactDisabledRevision"]!.AsObject();
		((string?)backup["selector"]).ShouldBe(".edited-revision");
		((string?)backup["source"]).ShouldBe("attribute");
		((string?)backup["attributeName"]).ShouldBe("data-revision");
		((string?)backup["matchPattern"]).ShouldBe("build-(\\d+)");
		((int?)backup["captureGroup"]).ShouldBe(1);
		((string?)backup["futureRevision"]).ShouldBe("keep-revision");
		var runtimeDisabled =
			await Store.LoadWebMonitorRulesAsync(CancellationToken.None);
		runtimeDisabled[0].Revision.ShouldBeNull();

		var reloaded = CreateSection();
		await reloaded.LoadAsync(CancellationToken.None);
		var restored =
			reloaded.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		restored.HasRevisionExtractor.ShouldBeFalse();
		restored.RevisionExtractor.Selector.ShouldBe(".edited-revision");
		restored.RevisionExtractor.Source.ShouldBe(WebMonitorValueSource.Attribute);
		restored.RevisionExtractor.AttributeName.ShouldBe("data-revision");
		restored.RevisionExtractor.MatchPattern.ShouldBe("build-(\\d+)");
		restored.RevisionExtractor.CaptureGroupText.ShouldBe("1");

		restored.HasRevisionExtractor = true;
		(await reloaded.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var enabled = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!
			.AsArray()[0]!.AsObject();
		enabled["$pactDisabledRevision"].ShouldBeNull();
		((string?)enabled["revision"]!["futureRevision"]).ShouldBe("keep-revision");
		var runtimeEnabled =
			await Store.LoadWebMonitorRulesAsync(CancellationToken.None);
		var runtimeRevision =
			runtimeEnabled[0].Revision.ShouldNotBeNull();
		runtimeRevision.Selector.ShouldBe(".edited-revision");
		runtimeRevision.Source.ShouldBe(WebMonitorValueSource.Attribute);
	}

	[Test]
	public async Task Malformed_disabled_extractor_backup_is_preserved_and_blocks_form_save()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		root[1]!["activity"] = null;
		root[1]!["$pactDisabledActivity"] = "future-backup-format";
		await File.WriteAllTextAsync(
			FilePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var section = CreateSection();

		await section.LoadAsync(CancellationToken.None);

		var item =
			section.Items[1].ShouldBeOfType<WebMonitorRuleItemViewModel>();
		item.HasActivityExtractor.ShouldBeFalse();
		((string?)item.Node["$pactDisabledActivity"]).ShouldBe("future-backup-format");
		section.IsDirty.ShouldBeFalse();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("$pactDisabledActivity");
		((string?)item.Node["$pactDisabledActivity"]).ShouldBe("future-backup-format");
	}

	[Test]
	public async Task Unknown_source_string_round_trips_until_user_selects_supported_value()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsArray();
		root[0]!["revision"]!["source"] = "future-source";
		await File.WriteAllTextAsync(
			FilePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		var section = CreateSection();
		await section.LoadAsync(CancellationToken.None);
		var item =
			section.Items[0].ShouldBeOfType<WebMonitorRuleItemViewModel>();

		item.RevisionExtractor.Source.ShouldBeNull();
		item.HasUnsupportedRevisionSource.ShouldBeTrue();
		(item.RevisionSourceWarning ?? string.Empty).ShouldContain("future-source");
		((string?)item.Node["revision"]!["source"]).ShouldBe("future-source");
		section.IsDirty.ShouldBeFalse();

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("future-source");
		((string?)item.Node["revision"]!["source"]).ShouldBe("future-source");

		item.RevisionExtractor.Source = WebMonitorValueSource.Text;
		section.IsDirty.ShouldBeTrue();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!
			.AsArray()[0]!.AsObject();
		((string?)saved["revision"]!["source"]).ShouldBe("text");
	}

	[Test]
	public async Task TestSelectedItemAsync_uses_current_fields_and_does_not_dirty_or_save_section()
	{
		var calls = 0;
		WebMonitorRule? testedRule = null;
		var section = CreateSection((rule, _) =>
		{
			calls++;
			testedRule = rule;
			return Task.FromResult(new WebMonitorTestResult(
				UrlMatched: true,
				Activity: true,
				Revision: "1842",
				Error: null));
		});
		await section.LoadAsync(CancellationToken.None);
		var selected =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();
		var before = await File.ReadAllTextAsync(FilePath);

		await section.TestSelectedItemAsync(CancellationToken.None);

		calls.ShouldBe(1);
		testedRule.ShouldNotBeNull();
		testedRule.Id.ShouldBe(selected.Id);
		(section.TestResultMessage ?? string.Empty).ShouldContain("matched");
		(section.TestResultMessage ?? string.Empty).ShouldContain("true");
		(section.TestResultMessage ?? string.Empty).ShouldContain("1842");
		section.IsDirty.ShouldBeFalse();
		(await File.ReadAllTextAsync(FilePath)).ShouldBe(before);
	}

	[Test]
	public async Task TestSelectedItemAsync_reports_delegate_error_without_changing_dirty_state()
	{
		var section = CreateSection((_, _) =>
			Task.FromResult(new WebMonitorTestResult(
				UrlMatched: false,
				Activity: null,
				Revision: null,
				Error: "No loaded web tab is selected.")));
		await section.LoadAsync(CancellationToken.None);

		await section.TestSelectedItemAsync(CancellationToken.None);

		section.TestResultMessage.ShouldBe("No loaded web tab is selected.");
		section.IsDirty.ShouldBeFalse();
	}

	[Test]
	public async Task TestSelectedItemAsync_preserves_existing_dirty_state()
	{
		var section = await LoadSectionAsync();
		var selected =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();
		selected.Title = "Unsaved title";

		await section.TestSelectedItemAsync(CancellationToken.None);

		section.IsDirty.ShouldBeTrue();
		selected.IsItemDirty.ShouldBeTrue();
		selected.TabHeader.ShouldBe("Unsaved title •");
	}

	private async Task<WebMonitoringRulesSectionViewModel> LoadSectionAsync()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = CreateSection();
		await section.LoadAsync(CancellationToken.None);
		return section;
	}

	private WebMonitoringRulesSectionViewModel CreateSection(
		Func<WebMonitorRule, CancellationToken, Task<WebMonitorTestResult>>? test = null)
	{
		test ??= static (_, _) => Task.FromResult(
			new WebMonitorTestResult(false, null, null, "Testing is not configured."));
		return new WebMonitoringRulesSectionViewModel(Store, test);
	}
}

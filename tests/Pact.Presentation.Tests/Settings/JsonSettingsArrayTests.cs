using System.Text.Json.Nodes;
using Pact.Presentation.Settings.Mapping;

namespace Pact.Presentation.Tests.Settings;

public class JsonSettingsArrayTests
{
	[Test]
	public void Parse_bare_array_exposes_object_items()
	{
		var arr = JsonSettingsArray.Parse(/*lang=json,strict*/ """[{"id":"a"},{"id":"b"}]""");
		arr.Items.Count.ShouldBe(2);
		((string?)arr.Items[0]["id"]).ShouldBe("a");
	}

	[Test]
	public void Unknown_properties_and_non_object_elements_survive_round_trip()
	{
		var json = /*lang=json,strict*/ """[{"id":"a","customField":{"x":1}},"stray",{"id":"b"}]""";
		var arr = JsonSettingsArray.Parse(json);
		arr.Items[0]["id"] = "renamed";
		var result = arr.ToJsonString();
		var reparsed = JsonNode.Parse(result)!.AsArray();
		reparsed.Count.ShouldBe(3);
		((int?)reparsed[0]!["customField"]!["x"]).ShouldBe(1);
		reparsed[1]!.GetValue<string>().ShouldBe("stray");
		((string?)reparsed[0]!["id"]).ShouldBe("renamed");
	}

	[Test]
	public void Object_root_with_array_property_unwraps_and_rewraps()
	{
		var json = /*lang=json,strict*/ """{"helpers":[{"id":"tg"}],"note":"keep me"}""";
		var arr = JsonSettingsArray.Parse(json, "helpers");
		arr.Items.ShouldHaveSingleItem();
		arr.AddNew()["id"] = "new";
		var reparsed = JsonNode.Parse(arr.ToJsonString())!.AsObject();
		((string?)reparsed["note"]).ShouldBe("keep me");
		reparsed["helpers"]!.AsArray().Count.ShouldBe(2);
	}

	[Test]
	public void AddNew_and_Remove_mutate_items()
	{
		var arr = JsonSettingsArray.Parse("[]");
		var added = arr.AddNew();
		added["id"] = "x";
		arr.Items.ShouldHaveSingleItem();
		arr.Remove(added);
		arr.Items.ShouldBeEmpty();
	}

	[Test]
	public void Parse_throws_JsonException_on_wrong_root_shape()
	{
		Should.Throw<Exception>(() => JsonSettingsArray.Parse(/*lang=json,strict*/ """{"a":1}""", null));
		Should.Throw<Exception>(() => JsonSettingsArray.Parse("not json"));
	}

	[Test]
	public void Move_swaps_with_the_next_slot_and_survives_round_trip()
	{
		var arr = JsonSettingsArray.Parse(/*lang=json,strict*/ """[{"id":"a"},{"id":"b"},{"id":"c"}]""");
		var first = arr.Items[0];

		arr.Move(first, 1);

		arr.Items.Select(item => (string?)item["id"]).ShouldBe(["b", "a", "c"]);
		var reparsed = JsonNode.Parse(arr.ToJsonString())!.AsArray();
		reparsed.Select(item => (string?)item!["id"]).ShouldBe(["b", "a", "c"]);
	}

	[Test]
	public void Move_by_negative_delta_swaps_with_the_previous_slot()
	{
		var arr = JsonSettingsArray.Parse(/*lang=json,strict*/ """[{"id":"a"},{"id":"b"},{"id":"c"}]""");
		var last = arr.Items[2];

		arr.Move(last, -1);

		arr.Items.Select(item => (string?)item["id"]).ShouldBe(["a", "c", "b"]);
	}

	[Test]
	public void Move_out_of_bounds_or_unknown_item_is_a_no_op()
	{
		var arr = JsonSettingsArray.Parse(/*lang=json,strict*/ """[{"id":"a"},{"id":"b"}]""");
		var first = arr.Items[0];
		var last = arr.Items[1];

		arr.Move(first, -1); // already at index 0
		arr.Move(last, 1); // already at the last index
		arr.Move(new JsonObject(), 1); // not in the array

		arr.Items.Select(item => (string?)item["id"]).ShouldBe(["a", "b"]);
	}
}
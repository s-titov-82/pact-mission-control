using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.Mapping;

/// <summary>
/// Wraps a settings file's JSON: a bare top-level array, or an object with one array
/// property (e.g. git-helpers "helpers"). Keeps the parsed <see cref="JsonNode"/> tree as
/// the single source of truth so unknown properties and non-object elements survive
/// round-tripping verbatim.
/// </summary>
public sealed class JsonSettingsArray
{
	private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

	private readonly JsonNode _root;
	private readonly JsonArray _array;

	private JsonSettingsArray(JsonNode root, JsonArray array)
	{
		_root = root;
		_array = array;
	}

	/// <summary>
	/// Parses <paramref name="json"/>. When <paramref name="arrayPropertyName"/> is null,
	/// the root must be a JSON array. Otherwise the root must be a JSON object holding that
	/// array property (an empty array is created if the property is absent).
	/// </summary>
	public static JsonSettingsArray Parse(string json, string? arrayPropertyName = null)
	{
		var root = JsonNode.Parse(json);

		if (arrayPropertyName is null)
		{
			if (root is not JsonArray array)
			{
				throw new JsonException("Expected the JSON root to be an array.");
			}

			return new JsonSettingsArray(array, array);
		}

		if (root is not JsonObject obj)
		{
			throw new JsonException("Expected the JSON root to be an object.");
		}

		var arrayNode = obj[arrayPropertyName];
		if (arrayNode is not JsonArray array2)
		{
			if (arrayNode is not null)
			{
				throw new JsonException($"Expected property '{arrayPropertyName}' to be an array.");
			}

			array2 = new JsonArray();
			obj[arrayPropertyName] = array2;
		}

		return new JsonSettingsArray(obj, array2);
	}

	/// <summary>
	/// A second array view over another property of the same object root (e.g. git-helpers
	/// "commands" next to "helpers"). Both views share the document, so saving either
	/// <see cref="ToJsonString"/> writes all edits atomically.
	/// </summary>
	public JsonSettingsArray SiblingArray(string arrayPropertyName)
	{
		if (_root is not JsonObject obj)
		{
			throw new InvalidOperationException("Sibling arrays require an object root.");
		}

		var arrayNode = obj[arrayPropertyName];
		if (arrayNode is not JsonArray array)
		{
			if (arrayNode is not null)
			{
				throw new JsonException($"Expected property '{arrayPropertyName}' to be an array.");
			}

			array = new JsonArray();
			obj[arrayPropertyName] = array;
		}

		return new JsonSettingsArray(obj, array);
	}

	/// <summary>Object elements of the underlying array, in order.</summary>
	public IReadOnlyList<JsonObject> Items => _array.OfType<JsonObject>().ToList();

	/// <summary>Total element count, including non-object elements preserved verbatim.</summary>
	public int RawElementCount => _array.Count;

	/// <summary>Appends a new empty object to the array and returns it.</summary>
	public JsonObject AddNew()
	{
		var item = new JsonObject();
		_array.Add(item);
		return item;
	}

	/// <summary>Removes <paramref name="item"/> from the array.</summary>
	public void Remove(JsonObject item) => _array.Remove(item);

	/// <summary>
	/// Moves <paramref name="item"/> by <paramref name="delta"/> raw array slots (e.g. -1 to swap
	/// with its predecessor, +1 with its successor). A no-op when the item is not found or the
	/// move would go out of bounds.
	/// </summary>
	public void Move(JsonObject item, int delta)
	{
		var currentIndex = _array.IndexOf(item);
		if (currentIndex < 0)
		{
			return;
		}

		var targetIndex = currentIndex + delta;
		if (targetIndex < 0 || targetIndex >= _array.Count)
		{
			return;
		}

		_array.RemoveAt(currentIndex);
		_array.Insert(targetIndex, item);
	}

	/// <summary>
	/// Serializes the whole tree (rewrapping the object root when <c>arrayPropertyName</c>
	/// was used at parse time), with indented formatting.
	/// </summary>
	public string ToJsonString() => _root.ToJsonString(SerializeOptions);
}
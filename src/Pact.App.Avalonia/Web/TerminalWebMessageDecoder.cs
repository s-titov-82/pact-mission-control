using System.Text.Json;

namespace Pact.App.Avalonia.Web;

internal abstract record TerminalWebMessage
{
	internal sealed record Ready : TerminalWebMessage;

	internal sealed record Input(string SessionId, string Data) : TerminalWebMessage;

	internal sealed record Resize(string SessionId, int Columns, int Rows) : TerminalWebMessage;

	internal sealed record ScreenSnapshot(string SessionId, string Text, bool Stable) : TerminalWebMessage;

	internal sealed record SelectionChanged(string SessionId, bool HasSelection) : TerminalWebMessage;

	internal sealed record SelectionCompleted(string SessionId, double X, double Y, long Revision) : TerminalWebMessage;

	internal sealed record SelectionDismissed(string SessionId) : TerminalWebMessage;

	internal sealed record LinkRequested(string SessionId, string Url) : TerminalWebMessage;

	internal sealed record PasteRequested : TerminalWebMessage;

	internal sealed record BusyOverlayAction : TerminalWebMessage;

	internal sealed record CopySelection(
		string SessionId,
		string Text,
		double? X,
		double? Y,
		long? Revision) : TerminalWebMessage;

	internal sealed record SelectedTextResponse(string Text) : TerminalWebMessage;
}

internal static class TerminalWebMessageDecoder
{
	internal static TerminalWebMessage? TryDecode(string json)
	{
		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object
				|| !TryGetString(root, "type", out var type))
			{
				return null;
			}

			return type switch
			{
				"ready" => new TerminalWebMessage.Ready(),
				"input" => DecodeInput(root),
				"resize" => DecodeResize(root),
				"screenSnapshot" => DecodeScreenSnapshot(root),
				"selectionChanged" => DecodeSelectionChanged(root),
				"selectionCompleted" => DecodeSelectionCompleted(root),
				"selectionDismissed" => DecodeSelectionDismissed(root),
				"linkRequested" => DecodeLinkRequested(root),
				"pasteRequested" => new TerminalWebMessage.PasteRequested(),
				"busyOverlayAction" => new TerminalWebMessage.BusyOverlayAction(),
				"copySelection" => DecodeCopySelection(root),
				"selectedTextResponse" => DecodeSelectedTextResponse(root),
				_ => null,
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static TerminalWebMessage.Input? DecodeInput(JsonElement root) =>
		TryGetString(root, "sessionId", out var sessionId)
			&& TryGetString(root, "data", out var data)
			? new TerminalWebMessage.Input(sessionId, data)
			: null;

	private static TerminalWebMessage.Resize? DecodeResize(JsonElement root)
	{
		if (!TryGetString(root, "sessionId", out var sessionId)
			|| !root.TryGetProperty("cols", out var columnsElement)
			|| !columnsElement.TryGetInt32(out var columns)
			|| columns <= 0
			|| !root.TryGetProperty("rows", out var rowsElement)
			|| !rowsElement.TryGetInt32(out var rows)
			|| rows <= 0)
		{
			return null;
		}

		return new TerminalWebMessage.Resize(sessionId, columns, rows);
	}

	private static TerminalWebMessage.LinkRequested? DecodeLinkRequested(JsonElement root) =>
		TryGetString(root, "sessionId", out var sessionId)
			&& TryGetString(root, "url", out var url)
			? new TerminalWebMessage.LinkRequested(sessionId, url)
			: null;

	private static TerminalWebMessage.ScreenSnapshot? DecodeScreenSnapshot(JsonElement root)
	{
		if (!TryGetString(root, "sessionId", out var sessionId)
			|| !TryGetString(root, "text", out var text))
		{
			return null;
		}

		var stable = true;
		if (root.TryGetProperty("stable", out var stableElement))
		{
			if (stableElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
			{
				return null;
			}

			stable = stableElement.GetBoolean();
		}

		return new TerminalWebMessage.ScreenSnapshot(sessionId, text, stable);
	}

	private static TerminalWebMessage.SelectionChanged? DecodeSelectionChanged(JsonElement root)
	{
		if (!TryGetString(root, "sessionId", out var sessionId)
			|| !root.TryGetProperty("hasSelection", out var hasSelection)
			|| hasSelection.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
		{
			return null;
		}

		return new TerminalWebMessage.SelectionChanged(sessionId, hasSelection.GetBoolean());
	}

	private static TerminalWebMessage.SelectionCompleted? DecodeSelectionCompleted(JsonElement root)
	{
		if (!TryGetString(root, "sessionId", out var sessionId)
			|| !TryDecodeAnchor(root, required: true, out var x, out var y, out var revision))
		{
			return null;
		}

		return new TerminalWebMessage.SelectionCompleted(sessionId, x!.Value, y!.Value, revision!.Value);
	}

	private static TerminalWebMessage.SelectionDismissed? DecodeSelectionDismissed(JsonElement root) =>
		TryGetString(root, "sessionId", out var sessionId)
			? new TerminalWebMessage.SelectionDismissed(sessionId)
			: null;

	private static TerminalWebMessage.CopySelection? DecodeCopySelection(JsonElement root)
	{
		if (!TryGetString(root, "sessionId", out var sessionId)
			|| !TryGetString(root, "data", out var text)
			|| !TryDecodeAnchor(root, required: false, out var x, out var y, out var revision))
		{
			return null;
		}

		return new TerminalWebMessage.CopySelection(sessionId, text, x, y, revision);
	}

	private static TerminalWebMessage.SelectedTextResponse? DecodeSelectedTextResponse(JsonElement root) =>
		TryGetString(root, "data", out var text)
			? new TerminalWebMessage.SelectedTextResponse(text)
			: null;

	private static bool TryGetString(JsonElement root, string propertyName, out string value)
	{
		if (root.TryGetProperty(propertyName, out var element)
			&& element.ValueKind == JsonValueKind.String)
		{
			value = element.GetString() ?? string.Empty;
			return true;
		}

		value = string.Empty;
		return false;
	}

	private static bool TryDecodeAnchor(
		JsonElement root,
		bool required,
		out double? x,
		out double? y,
		out long? revision)
	{
		var hasX = root.TryGetProperty("x", out var xElement);
		var hasY = root.TryGetProperty("y", out var yElement);
		var hasRevision = root.TryGetProperty("revision", out var revisionElement);
		if (!hasX && !hasY && !hasRevision)
		{
			x = null;
			y = null;
			revision = null;
			return !required;
		}

		if (!hasX
			|| !hasY
			|| !hasRevision
			|| !TryGetFiniteDouble(xElement, out var finiteX)
			|| !TryGetFiniteDouble(yElement, out var finiteY)
			|| !revisionElement.TryGetInt64(out var nonNegativeRevision)
			|| nonNegativeRevision < 0)
		{
			x = null;
			y = null;
			revision = null;
			return false;
		}

		x = finiteX;
		y = finiteY;
		revision = nonNegativeRevision;
		return true;
	}

	private static bool TryGetFiniteDouble(JsonElement element, out double value)
	{
		if (element.ValueKind == JsonValueKind.Number
			&& element.TryGetDouble(out value)
			&& double.IsFinite(value))
		{
			return true;
		}

		value = default;
		return false;
	}
}

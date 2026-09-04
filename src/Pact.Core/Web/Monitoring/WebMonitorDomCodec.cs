using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pact.Core.Web.Monitoring;

/// <summary>
/// Encodes validated monitor queries for the application-owned DOM evaluator and normalizes its typed result.
/// </summary>
public static class WebMonitorDomCodec
{
	private const string Evaluator = """
        (query => {
            const read = extractor => {
                const elements = document.querySelectorAll(extractor.selector);
                let value = null;
                if (elements.length > 0) {
                    if (extractor.source === "Text") {
                        value = elements[0].textContent;
                    } else if (extractor.source === "Attribute") {
                        value = elements[0].getAttribute(extractor.attributeName);
                    }
                }

                let match = null;
                if (value !== null && extractor.matchPattern !== null) {
                    match = new RegExp(extractor.matchPattern).exec(value);
                }

                return { count: elements.length, value, match };
            };

            return {
                documentUrl: window.location.href,
                observation: query === null
                    ? null
                    : {
                        activity: query.activity === null ? null : read(query.activity),
                        revision: query.revision === null ? null : read(query.revision)
                    }
            };
        })
        """;

	private static readonly JsonSerializerOptions QueryJsonOptions = CreateQueryJsonOptions();

	/// <summary>
	/// Builds one fixed DOM evaluator invocation whose only variable source is a JSON-serialized typed query.
	/// </summary>
	/// <param name="query">The DOM query, or <see langword="null"/> for a URL-only probe.</param>
	/// <returns>An application-owned JavaScript expression ready for browser evaluation.</returns>
	public static string BuildScript(WebMonitorDomQuery? query) =>
		$"{Evaluator}({JsonSerializer.Serialize(query, QueryJsonOptions)})";

	/// <summary>
	/// Decodes a browser evaluation without exposing returned page content through parsing diagnostics.
	/// </summary>
	/// <param name="query">The query used to produce the result, or <see langword="null"/> for a URL-only probe.</param>
	/// <param name="scriptResult">The JSON result returned by the browser engine.</param>
	/// <returns>The actual document URL and normalized observation.</returns>
	/// <exception cref="InvalidOperationException">
	/// Thrown when the browser result is missing, malformed, or does not contain an absolute document URL.
	/// </exception>
	public static WebMonitorEvaluation DecodeEvaluation(
		WebMonitorDomQuery? query,
		string? scriptResult)
	{
		if (string.IsNullOrWhiteSpace(scriptResult))
		{
			throw InvalidEvaluation();
		}

		try
		{
			using var outer = JsonDocument.Parse(scriptResult);
			if (outer.RootElement.ValueKind != JsonValueKind.String)
			{
				return DecodeRoot(query, outer.RootElement);
			}

			var nestedJson = outer.RootElement.GetString();
			if (string.IsNullOrWhiteSpace(nestedJson))
			{
				throw InvalidEvaluation();
			}

			using var nested = JsonDocument.Parse(nestedJson);
			return DecodeRoot(query, nested.RootElement);
		}
		catch (Exception exception) when (
			exception is JsonException or InvalidOperationException or FormatException)
		{
			throw InvalidEvaluation();
		}
	}

	private static WebMonitorEvaluation DecodeRoot(
		WebMonitorDomQuery? query,
		JsonElement root)
	{
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("documentUrl", out var documentUrlElement)
			|| documentUrlElement.ValueKind != JsonValueKind.String
			|| !Uri.TryCreate(documentUrlElement.GetString(), UriKind.Absolute, out var documentUrl))
		{
			throw InvalidEvaluation();
		}

		if (query is null)
		{
			return new WebMonitorEvaluation(documentUrl, Observation: null);
		}

		if (!root.TryGetProperty("observation", out var observationElement)
			|| observationElement.ValueKind != JsonValueKind.Object)
		{
			throw InvalidEvaluation();
		}

		var activity = query.Activity is null
			? query.ActivityWhenExtractorMissing
			: DecodeActivity(
				query.Activity,
				DecodeExtractor(observationElement, "activity"));
		var revision = query.Revision is null
			? null
			: DecodeRevision(
				query.Revision,
				DecodeExtractor(observationElement, "revision"));

		return new WebMonitorEvaluation(
			documentUrl,
			new WebMonitorObservation(activity, revision));
	}

	private static bool DecodeActivity(
		WebMonitorExtractor extractor,
		RawExtractor raw) =>
		extractor.Source switch
		{
			WebMonitorValueSource.Exists or WebMonitorValueSource.Count => raw.Count > 0,
			WebMonitorValueSource.Text or WebMonitorValueSource.Attribute =>
				raw.Count > 0 && raw.Value is not null && raw.Match is not null,
			_ => throw InvalidEvaluation()
		};

	private static string? DecodeRevision(
		WebMonitorExtractor extractor,
		RawExtractor raw)
	{
		if (raw.Count == 0 || raw.Value is null)
		{
			return null;
		}

		if (extractor.MatchPattern is null)
		{
			return raw.Value.Trim();
		}

		if (raw.Match is null)
		{
			return null;
		}

		if (extractor.CaptureGroup is not int captureGroup)
		{
			return raw.Value.Trim();
		}

		return captureGroup >= 0 && captureGroup < raw.Match.Length
			? raw.Match[captureGroup]?.Trim()
			: null;
	}

	private static RawExtractor DecodeExtractor(
		JsonElement observation,
		string propertyName)
	{
		if (!observation.TryGetProperty(propertyName, out var extractorElement)
			|| extractorElement.ValueKind != JsonValueKind.Object
			|| !extractorElement.TryGetProperty("count", out var countElement)
			|| !countElement.TryGetInt32(out var count)
			|| count < 0
			|| !extractorElement.TryGetProperty("value", out var valueElement)
			|| valueElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)
			|| !extractorElement.TryGetProperty("match", out var matchElement)
			|| matchElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
		{
			throw InvalidEvaluation();
		}

		string?[]? match = null;
		if (matchElement.ValueKind == JsonValueKind.Array)
		{
			match = new string?[matchElement.GetArrayLength()];
			var index = 0;
			foreach (var groupElement in matchElement.EnumerateArray())
			{
				if (groupElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
				{
					throw InvalidEvaluation();
				}

				match[index++] = groupElement.GetString();
			}
		}

		return new RawExtractor(count, valueElement.GetString(), match);
	}

	private static JsonSerializerOptions CreateQueryJsonOptions()
	{
		JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
		options.Converters.Add(new JsonStringEnumConverter());
		return options;
	}

	private static InvalidOperationException InvalidEvaluation() =>
		new("Web monitor DOM evaluation returned an invalid result.");

	private readonly record struct RawExtractor(
		int Count,
		string? Value,
		string?[]? Match);
}
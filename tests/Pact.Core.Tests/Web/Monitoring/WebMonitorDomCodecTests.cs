using Pact.Core.Web.Monitoring;

namespace Pact.Core.Tests.Web.Monitoring;

public sealed class WebMonitorDomCodecTests
{
	[Test]
	public void BuildScript_JsonEscapesSelectorsAndPatternsInsideOwnedEvaluator()
	{
		const string selector = """[data-path="C:\builds\current"]""";
		const string pattern = """^status\\("running"\)$""";
		var query = Query(
			activity: new WebMonitorExtractor(
				selector,
				WebMonitorValueSource.Attribute,
				"data-state",
				pattern,
				CaptureGroup: null));

		var script = WebMonitorDomCodec.BuildScript(query);

		script.ShouldContain("window.location.href");
		script.ShouldContain("document.querySelectorAll");
		script.ShouldContain("new RegExp");
		script.ShouldNotContain(selector);
		script.ShouldNotContain(pattern);
		script.ShouldNotContain("eval(");
		CountOccurrences(script, "document.querySelectorAll").ShouldBe(1);
	}

	[TestCase(WebMonitorValueSource.Exists, 1, true)]
	[TestCase(WebMonitorValueSource.Exists, 0, false)]
	[TestCase(WebMonitorValueSource.Count, 3, true)]
	[TestCase(WebMonitorValueSource.Count, 0, false)]
	public void DecodeEvaluation_NormalizesExistenceAndCountActivity(
		WebMonitorValueSource source,
		int count,
		bool expected)
	{
		var query = Query(
			activity: new WebMonitorExtractor(".activity", source, null, null, null));
		var result = EvaluationResult(
			activity: $$"""{"count":{{count}},"value":null,"match":null}""",
			revision: "null");

		var evaluation = WebMonitorDomCodec.DecodeEvaluation(query, result);

		evaluation.DocumentUrl.ShouldBe(new Uri("https://example.test/build/42?tab=log#tail"));
		evaluation.Observation!.Activity.ShouldBe(expected);
	}

	[TestCase(WebMonitorValueSource.Text)]
	[TestCase(WebMonitorValueSource.Attribute)]
	public void DecodeEvaluation_NormalizesTextAndAttributeActivity(WebMonitorValueSource source)
	{
		var query = Query(
			activity: new WebMonitorExtractor(
				".activity",
				source,
				source == WebMonitorValueSource.Attribute ? "data-state" : null,
				"^running$",
				CaptureGroup: null));

		var matching = WebMonitorDomCodec.DecodeEvaluation(
			query,
			EvaluationResult(
				activity: /*lang=json,strict*/ """{"count":1,"value":"running","match":["running"]}""",
				revision: "null"));
		var notMatching = WebMonitorDomCodec.DecodeEvaluation(
			query,
			EvaluationResult(
				activity: /*lang=json,strict*/ """{"count":1,"value":"idle","match":null}""",
				revision: "null"));

		matching.Observation!.Activity.ShouldBe(true);
		notMatching.Observation!.Activity.ShouldBe(false);
	}

	[Test]
	public void DecodeEvaluation_UsesKnownFalseWhenActivityExtractorIsAbsent()
	{
		var query = Query(
			activity: null,
			revision: new WebMonitorExtractor(".revision", WebMonitorValueSource.Text, null, null, null));

		var evaluation = WebMonitorDomCodec.DecodeEvaluation(
			query,
			EvaluationResult(
				activity: "null",
				revision: /*lang=json,strict*/ """{"count":1,"value":" 1842 ","match":null}"""));

		evaluation.Observation.ShouldBe(new WebMonitorObservation(false, "1842"));
	}

	[Test]
	public void DecodeEvaluation_UsesFalseWhenConfiguredActivityElementIsMissing()
	{
		var query = Query(
			activity: new WebMonitorExtractor(
				".activity",
				WebMonitorValueSource.Text,
				null,
				"^running$",
				CaptureGroup: null));

		var evaluation = WebMonitorDomCodec.DecodeEvaluation(
			query,
			EvaluationResult(
				activity: /*lang=json,strict*/ """{"count":0,"value":null,"match":null}""",
				revision: "null"));

		evaluation.Observation!.Activity.ShouldBe(false);
	}

	[TestCase(WebMonitorValueSource.Text, null)]
	[TestCase(WebMonitorValueSource.Attribute, "data-build")]
	public void DecodeEvaluation_NormalizesRevisionTextAndAttribute(
		WebMonitorValueSource source,
		string? attributeName)
	{
		var query = Query(
			revision: new WebMonitorExtractor(
				".revision",
				source,
				attributeName,
				MatchPattern: null,
				CaptureGroup: null));

		var evaluation = WebMonitorDomCodec.DecodeEvaluation(
			query,
			EvaluationResult(
				activity: "null",
				revision: /*lang=json,strict*/ """{"count":1,"value":"  build-1842  ","match":null}"""));

		evaluation.Observation!.Revision.ShouldBe("build-1842");
	}

	[Test]
	public void DecodeEvaluation_SelectsAndTrimsConfiguredRevisionCaptureGroup()
	{
		var query = Query(
			revision: new WebMonitorExtractor(
				".revision",
				WebMonitorValueSource.Text,
				AttributeName: null,
				MatchPattern: "^build-(\\d+)$",
				CaptureGroup: 1));

		var evaluation = WebMonitorDomCodec.DecodeEvaluation(
			query,
			EvaluationResult(
				activity: "null",
				revision: /*lang=json,strict*/ """{"count":1,"value":"build-1842","match":["build-1842"," 1842 "]}"""));

		evaluation.Observation!.Revision.ShouldBe("1842");
	}

	[TestCase(/*lang=json,strict*/ """{"count":0,"value":null,"match":null}""")]
	[TestCase(/*lang=json,strict*/ """{"count":1,"value":"build-1842","match":null}""")]
	public void DecodeEvaluation_UsesNullWhenRevisionCannotBeExtracted(string rawRevision)
	{
		var query = Query(
			revision: new WebMonitorExtractor(
				".revision",
				WebMonitorValueSource.Text,
				AttributeName: null,
				MatchPattern: "^job-(\\d+)$",
				CaptureGroup: 1));

		var evaluation = WebMonitorDomCodec.DecodeEvaluation(
			query,
			EvaluationResult(activity: "null", revision: rawRevision));

		evaluation.Observation!.Revision.ShouldBeNull();
	}

	[Test]
	public void DecodeEvaluation_UrlOnlyProbeReturnsActualDocumentUrlWithoutObservation()
	{
		const string result =
								 /*lang=json,strict*/
								 """{"documentUrl":"https://example.test/spa/next?filter=open#discussion","observation":null}""";

		var evaluation = WebMonitorDomCodec.DecodeEvaluation(query: null, result);

		evaluation.DocumentUrl.ShouldBe(
			new Uri("https://example.test/spa/next?filter=open#discussion"));
		evaluation.Observation.ShouldBeNull();
	}

	[TestCase(null)]
	[TestCase("")]
	[TestCase("not-json-secret-page-text")]
	[TestCase(/*lang=json,strict*/ """{"observation":null}""")]
	[TestCase(/*lang=json,strict*/ """{"documentUrl":"relative-secret-page-text","observation":null}""")]
	[TestCase(/*lang=json,strict*/ """{"documentUrl":"https://example.test","observation":"secret-page-text"}""")]
	public void DecodeEvaluation_RejectsMalformedResultsWithoutLeakingPageContent(string? result)
	{
		var query = Query(
			activity: new WebMonitorExtractor(".activity", WebMonitorValueSource.Exists, null, null, null));

		var exception = Should.Throw<InvalidOperationException>(
			() => WebMonitorDomCodec.DecodeEvaluation(query, result));

		exception.Message.ShouldContain("Web monitor DOM evaluation");
		exception.Message.ShouldNotContain("secret", Case.Insensitive);
		exception.InnerException.ShouldBeNull();
	}

	private static WebMonitorDomQuery Query(
		WebMonitorExtractor? activity = null,
		WebMonitorExtractor? revision = null) =>
		new(activity, revision, ActivityWhenExtractorMissing: false);

	private static string EvaluationResult(string activity, string revision) =>
		$$"""
        {
          "documentUrl": "https://example.test/build/42?tab=log#tail",
          "observation": {
            "activity": {{activity}},
            "revision": {{revision}}
          }
        }
        """;

	private static int CountOccurrences(string value, string substring)
	{
		var count = 0;
		var index = 0;
		while ((index = value.IndexOf(substring, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += substring.Length;
		}

		return count;
	}
}
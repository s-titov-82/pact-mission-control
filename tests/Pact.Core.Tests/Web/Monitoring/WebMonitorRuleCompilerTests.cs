using System.Security.Cryptography;
using System.Text;
using Pact.Core.Web.Monitoring;

namespace Pact.Core.Tests.Web.Monitoring;

public sealed class WebMonitorRuleCompilerTests
{
	[Test]
	public void Compile_DisabledTeamCityRule_DoesNotMatchWhenEquivalentEnabledRuleMatches()
	{
		var enabledRule = CreateValidRule();
		Uri matchingUrl = new("https://teamcity.example/builds?branch=main#overview");

		WebMonitorRuleCompiler.Compile(enabledRule).Matches(matchingUrl).ShouldBeTrue();
		WebMonitorRuleCompiler.Compile(enabledRule with { Enabled = false })
			.Matches(matchingUrl)
			.ShouldBeFalse();
	}

	[Test]
	public void Compile_WithoutActivityExtractor_UsesFalseActivityFallback()
	{
		var rule = CreateValidRule() with { Activity = null };

		var compiled = WebMonitorRuleCompiler.Compile(rule);

		compiled.Query.Activity.ShouldBeNull();
		compiled.Query.ActivityWhenExtractorMissing.ShouldBeFalse();
		compiled.Query.Revision.ShouldBe(rule.Revision);
	}

	[Test]
	public void Validate_EnabledChangeMeRule_IsInvalid()
	{
		var rule = CreateTeamCityExample() with { Enabled = true };

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("CHANGE-ME-", StringComparison.Ordinal));
	}

	[Test]
	public void Validate_InvalidUrlRegex_IsInvalid()
	{
		var rule = CreateValidRule() with { UrlPattern = "(" };

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("URL", StringComparison.OrdinalIgnoreCase));
	}

	[TestCase(true)]
	[TestCase(false)]
	public void Validate_InvalidExtractorRegex_IsInvalid(bool activity)
	{
		var rule = CreateValidRule();
		rule = activity
			? rule with { Activity = rule.Activity! with { MatchPattern = "(" } }
			: rule with { Revision = rule.Revision! with { MatchPattern = "(" } };

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("regular expression", StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public void Validate_DotNetOnlyExtractorRegex_IsInvalid()
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = "(?i)^running$",
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("ECMAScript", StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public void Validate_DotNetBalancingGroupExtractorRegex_IsInvalid()
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = "(?<close>a)(?<open-close>b)",
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("ECMAScript", StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public void Validate_EcmaScriptNamedGroupStartingWithUnderscore_IsValid()
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = "^(?<_state>running)$",
				CaptureGroup = 1
			}
		};

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeTrue();
	}

	[Test]
	public void Validate_EcmaScriptLookbehind_IsValid()
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = "(?<=state:)running",
				CaptureGroup = null
			}
		};

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeTrue();
	}

	[Test]
	public void Validate_DuplicateNamedCaptureGroups_AreInvalid()
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = "(?<state>running)|(?<state>queued)",
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains(
			"portable ECMAScript subset",
			StringComparison.OrdinalIgnoreCase));
	}

	[TestCase(@"\Afoo")]
	[TestCase(@"foo\Z")]
	[TestCase(@"foo\z")]
	[TestCase(@"\Gfoo")]
	[TestCase(@"(?<state>running)\k'state'")]
	[TestCase(@"(?'state'running)")]
	public void Validate_DotNetOnlyExtractorSyntax_IsInvalid(string pattern)
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = pattern,
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains(
			"portable ECMAScript subset",
			StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public void Validate_CharacterClassSubtraction_IsInvalid()
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = "[a-z-[aeiou]]",
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains(
			"portable ECMAScript subset",
			StringComparison.OrdinalIgnoreCase));
	}

	[TestCase("^[a-z]+$")]
	[TestCase(@"^\[state\]$")]
	public void Validate_PortableCharacterClassSyntax_IsValid(string pattern)
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = pattern,
				CaptureGroup = null
			}
		};

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeTrue();
	}

	[TestCase(@"\a")]
	[TestCase(@"\e")]
	[TestCase(@"\q")]
	[TestCase(@"\123(a)")]
	[TestCase(@"\07")]
	[TestCase(@"\p{L}")]
	[TestCase(@"\u{1F600}")]
	[TestCase(@"\")]
	[TestCase(@"\c")]
	[TestCase(@"\c1")]
	[TestCase(@"\xA")]
	[TestCase(@"\xGG")]
	[TestCase(@"\u123")]
	[TestCase(@"\u12GG")]
	[TestCase(@"(a)\12")]
	[TestCase(@"\1(a)")]
	public void Validate_NonPortableOrMalformedEscape_IsInvalid(string pattern)
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = pattern,
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains(
			"portable ECMAScript subset",
			StringComparison.OrdinalIgnoreCase));
	}

	[TestCase(@"\d\D\s\S\w\W\b\B")]
	[TestCase(@"\f\n\r\t\v")]
	[TestCase(@"\0")]
	[TestCase(@"\cA\cz")]
	[TestCase(@"\x41\u0042")]
	[TestCase(@"\^\$\\\.\*\+\?\(\)\[\]\{\}\|\/\-")]
	[TestCase(@"(a)\1")]
	[TestCase(@"(?<_state>a)\k<_state>")]
	public void Validate_PortableEscape_IsValid(string pattern)
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				MatchPattern = pattern,
				CaptureGroup = null
			}
		};

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeTrue();
	}

	[TestCase(true)]
	[TestCase(false)]
	public void Validate_AttributeWithoutAttributeName_IsInvalid(bool activity)
	{
		var rule = CreateValidRule();
		rule = activity
			? rule with { Activity = rule.Activity! with { AttributeName = null } }
			: rule with { Revision = rule.Revision! with { AttributeName = null } };

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("attribute name", StringComparison.OrdinalIgnoreCase));
	}

	[TestCase(WebMonitorValueSource.Exists)]
	[TestCase(WebMonitorValueSource.Count)]
	public void Validate_RevisionBooleanSource_IsInvalid(WebMonitorValueSource source)
	{
		var rule = CreateValidRule() with
		{
			Revision = CreateValidRule().Revision! with
			{
				Source = source,
				AttributeName = null,
				MatchPattern = null,
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("revision", StringComparison.OrdinalIgnoreCase));
	}

	[TestCase(WebMonitorValueSource.Text)]
	[TestCase(WebMonitorValueSource.Attribute)]
	public void Validate_ActivityTextualSourceWithoutMatchPattern_IsInvalid(WebMonitorValueSource source)
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				Source = source,
				AttributeName = source == WebMonitorValueSource.Attribute ? "data-state" : null,
				MatchPattern = null,
				CaptureGroup = null
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("match pattern", StringComparison.OrdinalIgnoreCase));
	}

	[TestCase(-1)]
	[TestCase(2)]
	public void Validate_CaptureGroupOutsideRegexGroups_IsInvalid(int captureGroup)
	{
		var rule = CreateValidRule() with
		{
			Revision = CreateValidRule().Revision! with
			{
				MatchPattern = @"Build #(\d+)",
				CaptureGroup = captureGroup
			}
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains("capture group", StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public void Validate_IntervalBelowMinimum_IsInvalid()
	{
		var rule = CreateValidRule() with
		{
			PollIntervalSeconds = WebMonitorRuleCompiler.MinimumPollIntervalSeconds - 1
		};

		var result = WebMonitorRuleCompiler.Validate(rule);

		result.IsValid.ShouldBeFalse();
		result.Errors.ShouldContain(error => error.Contains(
			WebMonitorRuleCompiler.MinimumPollIntervalSeconds.ToString(),
			StringComparison.Ordinal));
	}

	[Test]
	public void Validate_IntervalAtMinimum_IsValid()
	{
		var rule = CreateValidRule() with
		{
			PollIntervalSeconds = WebMonitorRuleCompiler.MinimumPollIntervalSeconds
		};

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeTrue();
	}

	[TestCase("Id")]
	[TestCase("Title")]
	[TestCase("UrlPattern")]
	public void Validate_BlankRequiredRuleField_IsInvalid(string field)
	{
		var rule = field switch
		{
			"Id" => CreateValidRule() with { Id = " " },
			"Title" => CreateValidRule() with { Title = " " },
			"UrlPattern" => CreateValidRule() with { UrlPattern = " " },
			_ => throw new ArgumentOutOfRangeException(nameof(field))
		};

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeFalse();
	}

	[TestCase(true)]
	[TestCase(false)]
	public void Validate_BlankExtractorSelector_IsInvalid(bool activity)
	{
		var rule = CreateValidRule();
		rule = activity
			? rule with { Activity = rule.Activity! with { Selector = " " } }
			: rule with { Revision = rule.Revision! with { Selector = " " } };

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeFalse();
	}

	[Test]
	public void Validate_WithoutAnyExtractor_IsInvalid()
	{
		var rule = CreateValidRule() with
		{
			Activity = null,
			Revision = null
		};

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeFalse();
	}

	[Test]
	public void Validate_ActivityOnlyRule_IsValid()
	{
		var rule = CreateValidRule() with { Revision = null };

		WebMonitorRuleCompiler.Validate(rule).IsValid.ShouldBeTrue();
	}

	[Test]
	public void Compile_ValidEnabledRule_MatchesAbsoluteUrl()
	{
		var compiled = WebMonitorRuleCompiler.Compile(CreateValidRule());

		compiled.Matches(new Uri("https://teamcity.example/builds?branch=main")).ShouldBeTrue();
	}

	[Test]
	public void Matches_RelativeUrl_IsRejected()
	{
		var compiled = WebMonitorRuleCompiler.Compile(CreateValidRule());

		Should.Throw<ArgumentException>(() => compiled.Matches(new Uri("/builds", UriKind.Relative)));
	}

	[Test]
	public void Normalize_FragmentOnlyChangesProduceSameUrl_AndMatchingIgnoresFragment()
	{
		var first = WebMonitorUrl.Normalize(
			new Uri("https://teamcity.example/builds?branch=main#overview"));
		var second = WebMonitorUrl.Normalize(
			new Uri("https://teamcity.example/builds?branch=main#history"));
		var compiled = WebMonitorRuleCompiler.Compile(CreateValidRule());

		second.AbsoluteUri.ShouldBe(first.AbsoluteUri);
		first.Fragment.ShouldBeEmpty();
		compiled.Matches(new Uri("https://teamcity.example/builds?branch=main#overview")).ShouldBeTrue();
		compiled.Matches(new Uri("https://teamcity.example/builds?branch=main#history")).ShouldBeTrue();
	}

	[Test]
	public void Matches_QueryStringRemainsSignificant()
	{
		var compiled = WebMonitorRuleCompiler.Compile(CreateValidRule());

		compiled.Matches(new Uri("https://teamcity.example/builds?branch=main")).ShouldBeTrue();
		compiled.Matches(new Uri("https://teamcity.example/builds?branch=release")).ShouldBeFalse();
		compiled.Matches(new Uri("https://teamcity.example/builds")).ShouldBeFalse();
	}

	[Test]
	public void Compile_FingerprintIsSha256OfCanonicalUtf8Semantics()
	{
		var rule = CreateValidRule();
		const string canonical =
			"{\"urlPattern\":\"^https://teamcity\\\\.example/builds\\\\?branch=main$\","
			+ "\"pollIntervalSeconds\":30,"
			+ "\"activity\":{\"selector\":\".build[data-state]\",\"source\":\"Attribute\","
			+ "\"attributeName\":\"data-state\",\"matchPattern\":\"^(running)-(active)$\","
			+ "\"captureGroup\":1},"
			+ "\"revision\":{\"selector\":\".build[data-id]\",\"source\":\"Attribute\","
			+ "\"attributeName\":\"data-id\",\"matchPattern\":\"^build-(\\\\d+)-(\\\\w+)$\","
			+ "\"captureGroup\":1}}";
		var expected = Convert.ToHexString(
				SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
			.ToLowerInvariant();

		var compiled = WebMonitorRuleCompiler.Compile(rule);

		compiled.Fingerprint.ShouldBe(expected);
		compiled.Fingerprint.Length.ShouldBe(64);
	}

	[Test]
	public void Compile_EveryExtractorSemanticFieldChangesFingerprint()
	{
		var rule = CreateValidRule();
		var baseline = WebMonitorRuleCompiler.Compile(rule).Fingerprint;
		WebMonitorRule[] changedRules =
		[
			rule with { UrlPattern = "^https://teamcity\\.example/other$" },
			rule with { PollIntervalSeconds = 31 },
			rule with { Activity = null },
			rule with { Activity = rule.Activity! with { Selector = ".other-activity" } },
			rule with { Activity = rule.Activity! with { Source = WebMonitorValueSource.Text } },
			rule with { Activity = rule.Activity! with { AttributeName = "data-other-state" } },
			rule with { Activity = rule.Activity! with { MatchPattern = "^(queued)-(active)$" } },
			rule with { Activity = rule.Activity! with { CaptureGroup = 2 } },
			rule with { Revision = null },
			rule with { Revision = rule.Revision! with { Selector = ".other-revision" } },
			rule with { Revision = rule.Revision! with { Source = WebMonitorValueSource.Text } },
			rule with { Revision = rule.Revision! with { AttributeName = "data-other-id" } },
			rule with { Revision = rule.Revision! with { MatchPattern = "^job-(\\d+)-(\\w+)$" } },
			rule with { Revision = rule.Revision! with { CaptureGroup = 2 } }
		];

		var changedFingerprints = changedRules
			.Select(changed => WebMonitorRuleCompiler.Compile(changed).Fingerprint)
			.ToArray();

		changedFingerprints.ShouldAllBe(fingerprint => fingerprint != baseline);
		changedFingerprints.Distinct(StringComparer.Ordinal).Count().ShouldBe(changedRules.Length);
	}

	[Test]
	public void Compile_TitleAndEnabledDoNotChangeFingerprint()
	{
		var rule = CreateValidRule();
		var baseline = WebMonitorRuleCompiler.Compile(rule).Fingerprint;

		var changedTitle = WebMonitorRuleCompiler.Compile(
			rule with { Title = "Renamed" }).Fingerprint;
		var changedEnabled = WebMonitorRuleCompiler.Compile(
			rule with { Enabled = false }).Fingerprint;

		changedTitle.ShouldBe(baseline);
		changedEnabled.ShouldBe(baseline);
	}

	[Test]
	public void Compile_QueryIsStructuredTypedData()
	{
		var rule = CreateValidRule() with
		{
			Activity = CreateValidRule().Activity! with
			{
				Selector = """.build[data-state="running"]""",
				MatchPattern = """^running\\active$""",
				CaptureGroup = null
			}
		};

		var compiled = WebMonitorRuleCompiler.Compile(rule);

		compiled.Query.ShouldBeOfType<WebMonitorDomQuery>();
		compiled.Query.Activity.ShouldBe(rule.Activity);
		compiled.Query.Revision.ShouldBe(rule.Revision);
		compiled.Query.GetType().GetProperties()
			.ShouldNotContain(property => property.Name.Contains("Script", StringComparison.OrdinalIgnoreCase));
	}

	private static WebMonitorRule CreateTeamCityExample() => new WebMonitorRule(
			"teamcity-builds-example",
			"TeamCity builds",
			Enabled: false,
			UrlPattern: "^https://CHANGE-ME-TEAMCITY/(?:.*)$",
			PollIntervalSeconds: 30,
			Activity: new WebMonitorExtractor(
				".build.running",
				WebMonitorValueSource.Count,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null),
			Revision: new WebMonitorExtractor(
				".build.finished:first-child",
				WebMonitorValueSource.Text,
				AttributeName: null,
				MatchPattern: @"Build #(\d+)",
				CaptureGroup: 1));

	private static WebMonitorRule CreateValidRule() => new WebMonitorRule(
			"teamcity-builds",
			"TeamCity builds",
			Enabled: true,
			UrlPattern: @"^https://teamcity\.example/builds\?branch=main$",
			PollIntervalSeconds: 30,
			Activity: new WebMonitorExtractor(
				".build[data-state]",
				WebMonitorValueSource.Attribute,
				AttributeName: "data-state",
				MatchPattern: "^(running)-(active)$",
				CaptureGroup: 1),
			Revision: new WebMonitorExtractor(
				".build[data-id]",
				WebMonitorValueSource.Attribute,
				AttributeName: "data-id",
				MatchPattern: @"^build-(\d+)-(\w+)$",
				CaptureGroup: 1));
}
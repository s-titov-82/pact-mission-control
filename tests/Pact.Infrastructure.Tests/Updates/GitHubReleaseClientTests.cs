using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Pact.Core.Updates;
using Pact.Infrastructure.Updates;

namespace Pact.Infrastructure.Tests.Updates;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "HttpResponseMessage ownership is transferred to the client under test, which disposes every response.")]
public sealed class GitHubReleaseClientTests
{
	private static readonly DateTimeOffset Now =
		new(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);

	[Test]
	public async Task Valid_release_uses_the_fixed_GitHub_contract()
	{
		using ClientFixture fixture = new(_ => JsonResponse(ValidRelease()));

		var response = await fixture.Client.GetLatestStableAsync(
			new StableReleaseVersion(1, 2, 2),
			CancellationToken.None);

		var available = response.ShouldBeOfType<GitHubReleaseResponse.Available>();
		available.Release.Version.ShouldBe(new StableReleaseVersion(1, 2, 3));
		available.Release.Tag.ShouldBe("v1.2.3");
		available.Release.Setup.Name.ShouldBe(
			"pact-mission-control-1.2.3-win-x64-setup.exe");
		available.Release.Checksums.Name.ShouldBe("SHA256SUMS.txt");
		fixture.Handler.Requests.ShouldHaveSingleItem();
		var request = fixture.Handler.Requests.Single();
		request.Method.ShouldBe(HttpMethod.Get);
		request.Uri.ShouldBe(new Uri(
			"https://api.github.com/repos/s-titov-82/pact-mission-control/releases/latest"));
		request.Accept.ShouldBe("application/vnd.github+json");
		request.ApiVersion.ShouldBe("2022-11-28");
		request.UserAgent.ShouldContain("Pact", Case.Insensitive);
	}

	[TestCase(true, false)]
	[TestCase(false, true)]
	public async Task Drafts_and_prereleases_are_invalid(bool draft, bool prerelease)
	{
		var release = ValidRelease();
		release["draft"] = draft;
		release["prerelease"] = prerelease;

		(await GetAsync(release)).ShouldBeOfType<GitHubReleaseResponse.Invalid>();
	}

	[TestCase("1.2.3")]
	[TestCase("v1.2.3-preview")]
	[TestCase("v1.2.3+sha")]
	[TestCase("v1.2")]
	[TestCase("v2147483648.0.0")]
	public async Task Noncanonical_tags_are_invalid(string tag)
	{
		var release = ValidRelease();
		release["tag_name"] = tag;

		(await GetAsync(release)).ShouldBeOfType<GitHubReleaseResponse.Invalid>();
	}

	[TestCase("v1.2.3")]
	[TestCase("v1.2.2")]
	[TestCase("v0.99.99")]
	public async Task Same_or_older_release_is_not_an_update(string tag)
	{
		var release = ValidRelease();
		release["tag_name"] = tag;

		(await GetAsync(release, new StableReleaseVersion(1, 2, 3)))
			.ShouldBeOfType<GitHubReleaseResponse.NoUpdate>();
	}

	[TestCase("missing-setup")]
	[TestCase("missing-checksums")]
	[TestCase("duplicate-setup")]
	[TestCase("duplicate-checksums")]
	[TestCase("wrong-setup-name")]
	[TestCase("wrong-checksum-case")]
	[TestCase("setup-not-uploaded")]
	[TestCase("checksums-not-uploaded")]
	[TestCase("setup-zero-size")]
	[TestCase("checksums-zero-size")]
	[TestCase("setup-wrong-download-tag")]
	[TestCase("checksums-insecure-url")]
	public async Task Required_asset_contract_is_strict(string mutation)
	{
		var release = ValidRelease();
		var assets = release["assets"]!.AsArray();
		var setup = assets[0]!.AsObject();
		var checksums = assets[1]!.AsObject();
		switch (mutation)
		{
			case "missing-setup":
				assets.RemoveAt(0);
				break;
			case "missing-checksums":
				assets.RemoveAt(1);
				break;
			case "duplicate-setup":
				assets.Add(setup.DeepClone());
				break;
			case "duplicate-checksums":
				assets.Add(checksums.DeepClone());
				break;
			case "wrong-setup-name":
				setup["name"] = "Pact-Mission-Control-Setup-1.2.3.exe";
				break;
			case "wrong-checksum-case":
				checksums["name"] = "sha256sums.txt";
				break;
			case "setup-not-uploaded":
				setup["state"] = "new";
				break;
			case "checksums-not-uploaded":
				checksums["state"] = "new";
				break;
			case "setup-zero-size":
				setup["size"] = 0;
				break;
			case "checksums-zero-size":
				checksums["size"] = 0;
				break;
			case "setup-wrong-download-tag":
				setup["browser_download_url"] =
					"https://github.com/s-titov-82/pact-mission-control/releases/download/v9.9.9/setup.exe";
				break;
			case "checksums-insecure-url":
				checksums["browser_download_url"] =
					"http://github.com/s-titov-82/pact-mission-control/releases/download/v1.2.3/SHA256SUMS.txt";
				break;
		}

		(await GetAsync(release)).ShouldBeOfType<GitHubReleaseResponse.Invalid>();
	}

	[TestCase("http://github.com/s-titov-82/pact-mission-control/releases/tag/v1.2.3")]
	[TestCase("https://example.test/s-titov-82/pact-mission-control/releases/tag/v1.2.3")]
	[TestCase("https://github.com/other/project/releases/tag/v1.2.3")]
	[TestCase("https://github.com/s-titov-82/pact-mission-control/releases/tag/v1.2.3?from=api")]
	[TestCase("not-a-uri")]
	public async Task Release_notes_must_be_the_expected_GitHub_HTTPS_page(string url)
	{
		var release = ValidRelease();
		release["html_url"] = url;

		(await GetAsync(release)).ShouldBeOfType<GitHubReleaseResponse.Invalid>();
	}

	[Test]
	public async Task RetryAfter_wins_over_primary_limit_reset()
	{
		var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
		{
			Content = new StringContent("{\"message\":\"forbidden\"}")
		};
		response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
			TimeSpan.FromMinutes(2));
		response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
		response.Headers.TryAddWithoutValidation(
			"X-RateLimit-Reset",
			Now.AddMinutes(20).ToUnixTimeSeconds().ToString());

		var result = await GetAsync(response);

		result.ShouldBeOfType<GitHubReleaseResponse.RateLimited>()
			.RetryAt.ShouldBe(Now.AddMinutes(2));
	}

	[TestCase(HttpStatusCode.Forbidden)]
	[TestCase(HttpStatusCode.TooManyRequests)]
	public async Task Exhausted_primary_limit_uses_the_reset_header(HttpStatusCode status)
	{
		var response = new HttpResponseMessage(status);
		response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
		response.Headers.TryAddWithoutValidation(
			"X-RateLimit-Reset",
			Now.AddMinutes(20).ToUnixTimeSeconds().ToString());

		var result = await GetAsync(response);

		result.ShouldBeOfType<GitHubReleaseResponse.RateLimited>()
			.RetryAt.ShouldBe(Now.AddMinutes(20));
	}

	[TestCase(HttpStatusCode.Forbidden)]
	[TestCase(HttpStatusCode.TooManyRequests)]
	public async Task Secondary_limit_gets_a_bounded_retry_instead_of_no_update(
		HttpStatusCode status)
	{
		var response = new HttpResponseMessage(status)
		{
			Content = new StringContent(
				"{\"message\":\"You have exceeded a secondary rate limit\"}")
		};

		var result = await GetAsync(response);

		var limited = result.ShouldBeOfType<GitHubReleaseResponse.RateLimited>();
		limited.RetryAt.ShouldBeGreaterThan(Now);
		limited.RetryAt.ShouldBeLessThanOrEqualTo(Now.AddHours(1));
		limited.Reason.ShouldContain("rate limit", Case.Insensitive);
	}

	[Test]
	public async Task Malformed_json_is_an_invalid_response()
	{
		var response = await GetAsync(JsonResponse("{"));

		response.ShouldBeOfType<GitHubReleaseResponse.Invalid>();
	}

	private static async Task<GitHubReleaseResponse> GetAsync(
		JsonObject release,
		StableReleaseVersion? running = null) =>
		await GetAsync(JsonResponse(release), running);

	private static async Task<GitHubReleaseResponse> GetAsync(
		HttpResponseMessage response,
		StableReleaseVersion? running = null)
	{
		using ClientFixture fixture = new(_ => response);
		return await fixture.Client.GetLatestStableAsync(
			running ?? new StableReleaseVersion(1, 2, 2),
			CancellationToken.None);
	}

	private static HttpResponseMessage JsonResponse(JsonObject value) =>
		JsonResponse(value.ToJsonString());

	private static HttpResponseMessage JsonResponse(string value) => new(HttpStatusCode.OK)
	{
		Content = new StringContent(value, Encoding.UTF8, "application/json")
	};

	private static JsonObject ValidRelease() => new()
	{
		["tag_name"] = "v1.2.3",
		["draft"] = false,
		["prerelease"] = false,
		["html_url"] =
			"https://github.com/s-titov-82/pact-mission-control/releases/tag/v1.2.3",
		["assets"] = new JsonArray(
			Asset(
				"pact-mission-control-1.2.3-win-x64-setup.exe",
				"https://github.com/s-titov-82/pact-mission-control/releases/download/v1.2.3/pact-mission-control-1.2.3-win-x64-setup.exe",
				42),
			Asset(
				"SHA256SUMS.txt",
				"https://github.com/s-titov-82/pact-mission-control/releases/download/v1.2.3/SHA256SUMS.txt",
				256))
	};

	private static JsonObject Asset(string name, string url, long size) => new()
	{
		["name"] = name,
		["browser_download_url"] = url,
		["size"] = size,
		["state"] = "uploaded"
	};

	private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}

	private sealed class ScriptedHandler(
		Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
		: HttpMessageHandler
	{
		public List<RequestSnapshot> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			Requests.Add(new RequestSnapshot(
				request.Method,
				request.RequestUri!,
				string.Join(",", request.Headers.Accept.Select(value => value.MediaType)),
				request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions)
					? string.Join(",", versions)
					: string.Empty,
				request.Headers.UserAgent.ToString()));
			return Task.FromResult(responseFactory(request));
		}
	}

	private sealed class ClientFixture : IDisposable
	{
		private readonly HttpClient _httpClient;

		public ClientFixture(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
		{
			Handler = new ScriptedHandler(responseFactory);
			_httpClient = new HttpClient(Handler, disposeHandler: false);
			Client = new GitHubReleaseClient(_httpClient, new FixedTimeProvider(Now));
		}

		public ScriptedHandler Handler { get; }
		public GitHubReleaseClient Client { get; }

		public void Dispose()
		{
			_httpClient.Dispose();
			Handler.Dispose();
		}
	}

	private sealed record RequestSnapshot(
		HttpMethod Method,
		Uri Uri,
		string Accept,
		string ApiVersion,
		string UserAgent);
}

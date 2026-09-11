using System.Net.Http.Headers;
using System.Text.Json;
using Pact.Core.Updates;

namespace Pact.Infrastructure.Updates;

/// <summary>
/// Discovers updates from the fixed public Pact GitHub repository.
/// </summary>
public sealed class GitHubReleaseClient : IGitHubReleaseClient
{
	private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromSeconds(15);
	private static readonly Uri LatestReleaseUri = new(
		"https://api.github.com/repos/s-titov-82/pact-mission-control/releases/latest");
	private const string RepositoryReleasePath =
		"/s-titov-82/pact-mission-control/releases/tag/";
	private const string RepositoryDownloadPath =
		"/s-titov-82/pact-mission-control/releases/download/";
	private readonly HttpClient _httpClient;
	private readonly TimeProvider _timeProvider;
	private readonly TimeSpan _operationTimeout;

	/// <summary>
	/// Creates a client with an injected HTTP transport and clock.
	/// </summary>
	/// <param name="httpClient">The dedicated bounded-timeout HTTP client.</param>
	/// <param name="timeProvider">The clock used to interpret retry headers.</param>
	public GitHubReleaseClient(HttpClient httpClient, TimeProvider timeProvider)
		: this(httpClient, timeProvider, DefaultOperationTimeout)
	{
	}

	internal GitHubReleaseClient(
		HttpClient httpClient,
		TimeProvider timeProvider,
		TimeSpan operationTimeout)
	{
		ArgumentNullException.ThrowIfNull(httpClient);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(operationTimeout, TimeSpan.Zero);
		_httpClient = httpClient;
		_timeProvider = timeProvider;
		_operationTimeout = operationTimeout;
	}

	/// <inheritdoc />
	public async Task<GitHubReleaseResponse> GetLatestStableAsync(
		StableReleaseVersion runningVersion,
		CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeoutSource = new(_operationTimeout, _timeProvider);
		using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			timeoutSource.Token);
		var operationToken = operationSource.Token;
		using HttpRequestMessage request = new(HttpMethod.Get, LatestReleaseUri);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
			"application/vnd.github+json"));
		request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
		request.Headers.UserAgent.Add(new ProductInfoHeaderValue(
			"Pact-Mission-Control",
			"1.0"));

		using var response = await _httpClient.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead,
			operationToken).ConfigureAwait(false);
		var body = response.Content is null
			? string.Empty
			: await response.Content.ReadAsStringAsync(operationToken)
				.ConfigureAwait(false);
		if (GitHubRateLimit.TryCreateResponse(
			response,
			body,
			_timeProvider,
			out var rateLimited))
		{
			return rateLimited!;
		}

		if (!response.IsSuccessStatusCode)
		{
			return Invalid($"GitHub returned HTTP {(int)response.StatusCode}.");
		}

		GitHubReleaseDocument? document;
		try
		{
			document = JsonSerializer.Deserialize<GitHubReleaseDocument>(body);
		}
		catch (JsonException)
		{
			return Invalid("GitHub returned malformed release metadata.");
		}

		if (document?.Draft is not false || document.Prerelease is not false)
		{
			return Invalid("GitHub returned a draft, prerelease, or incomplete release.");
		}

		if (!StableReleaseVersion.TryParseTag(document.Tag, out var version))
		{
			return Invalid("GitHub returned a noncanonical stable release tag.");
		}

		if (version <= runningVersion)
		{
			return new GitHubReleaseResponse.NoUpdate();
		}

		if (!TryGetReleaseNotesUri(document.ReleaseNotesUrl, document.Tag!, out var notesUri))
		{
			return Invalid("GitHub returned an invalid release-notes address.");
		}

		var setupName = $"pact-mission-control-{version}-win-x64-setup.exe";
		if (!TryGetRequiredAsset(document.Assets, document.Tag!, setupName, out var setup)
			|| !TryGetRequiredAsset(
				document.Assets,
				document.Tag!,
				"SHA256SUMS.txt",
				out var checksums))
		{
			return Invalid("GitHub returned missing, duplicate, or invalid release assets.");
		}

		return new GitHubReleaseResponse.Available(new UpdateRelease(
			version,
			document.Tag!,
			notesUri!,
			setup!,
			checksums!));
	}

	private static GitHubReleaseResponse.Invalid Invalid(string reason) => new(reason);

	private static bool TryGetReleaseNotesUri(
		string? value,
		string tag,
		out Uri? uri)
	{
		var expectedPath = RepositoryReleasePath + tag;
		return TryGetGitHubHttpsUri(value, expectedPath, exactPath: true, out uri);
	}

	private static bool TryGetRequiredAsset(
		IReadOnlyList<GitHubReleaseAssetDocument>? assets,
		string tag,
		string requiredName,
		out UpdateAsset? asset)
	{
		asset = null;
		var matches = assets?
			.Where(candidate => string.Equals(
				candidate.Name,
				requiredName,
				StringComparison.Ordinal))
			.Take(2)
			.ToArray();
		if (matches is not { Length: 1 })
		{
			return false;
		}

		var match = matches[0];
		if (!string.Equals(match.State, "uploaded", StringComparison.Ordinal)
			|| match.Size is not > 0
			|| !TryGetGitHubHttpsUri(
				match.DownloadUrl,
				$"{RepositoryDownloadPath}{tag}/{requiredName}",
				exactPath: true,
				out var downloadUri))
		{
			return false;
		}

		asset = new UpdateAsset(match.Name!, downloadUri!, match.Size.Value);
		return true;
	}

	private static bool TryGetGitHubHttpsUri(
		string? value,
		string expectedPath,
		bool exactPath,
		out Uri? uri)
	{
		uri = null;
		if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
			|| !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
			|| !string.Equals(candidate.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			|| !string.IsNullOrEmpty(candidate.UserInfo)
			|| !candidate.IsDefaultPort
			|| !string.IsNullOrEmpty(candidate.Query)
			|| !string.IsNullOrEmpty(candidate.Fragment))
		{
			return false;
		}

		var pathMatches = exactPath
			? string.Equals(candidate.AbsolutePath, expectedPath, StringComparison.Ordinal)
			: candidate.AbsolutePath.StartsWith(expectedPath, StringComparison.Ordinal);
		if (!pathMatches)
		{
			return false;
		}

		uri = candidate;
		return true;
	}
}

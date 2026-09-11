using System.Globalization;
using System.Net;

namespace Pact.Infrastructure.Updates;

internal static class GitHubRateLimit
{
	private static readonly TimeSpan SecondaryLimitDelay = TimeSpan.FromMinutes(1);

	public static bool TryCreateResponse(
		HttpResponseMessage response,
		string responseBody,
		TimeProvider timeProvider,
		out GitHubReleaseResponse.RateLimited? rateLimited)
	{
		var status = response.StatusCode;
		var isTooManyRequests = status == HttpStatusCode.TooManyRequests;
		var isForbiddenRateLimit = status == HttpStatusCode.Forbidden
			&& (response.Headers.RetryAfter is not null
				|| HasNoRemainingRequests(response)
				|| responseBody.Contains("rate limit", StringComparison.OrdinalIgnoreCase));
		if (!isTooManyRequests && !isForbiddenRateLimit)
		{
			rateLimited = null;
			return false;
		}

		var now = timeProvider.GetUtcNow();
		if (TryReadRetryAfter(response, now, out var retryAt))
		{
			rateLimited = new GitHubReleaseResponse.RateLimited(
				retryAt,
				"GitHub API rate limit requested a delayed retry.");
			return true;
		}

		if (HasNoRemainingRequests(response)
			&& TryReadReset(response, out retryAt))
		{
			rateLimited = new GitHubReleaseResponse.RateLimited(
				retryAt > now ? retryAt : now.Add(SecondaryLimitDelay),
				"GitHub API rate limit is exhausted.");
			return true;
		}

		rateLimited = new GitHubReleaseResponse.RateLimited(
			now.Add(SecondaryLimitDelay),
			"GitHub API secondary rate limit requested a delayed retry.");
		return true;
	}

	private static bool TryReadRetryAfter(
		HttpResponseMessage response,
		DateTimeOffset now,
		out DateTimeOffset retryAt)
	{
		var retryAfter = response.Headers.RetryAfter;
		if (retryAfter?.Delta is { } delay)
		{
			retryAt = now.Add(delay > TimeSpan.Zero ? delay : SecondaryLimitDelay);
			return true;
		}

		if (retryAfter?.Date is { } date)
		{
			retryAt = date > now ? date : now.Add(SecondaryLimitDelay);
			return true;
		}

		retryAt = default;
		return false;
	}

	private static bool HasNoRemainingRequests(HttpResponseMessage response) =>
		response.Headers.TryGetValues("X-RateLimit-Remaining", out var values)
		&& values.Any(value => string.Equals(value, "0", StringComparison.Ordinal));

	private static bool TryReadReset(
		HttpResponseMessage response,
		out DateTimeOffset retryAt)
	{
		retryAt = default;
		if (!response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
			|| !long.TryParse(
				values.FirstOrDefault(),
				NumberStyles.None,
				CultureInfo.InvariantCulture,
				out var unixSeconds))
		{
			return false;
		}

		try
		{
			retryAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
			return true;
		}
		catch (ArgumentOutOfRangeException)
		{
			return false;
		}
	}
}

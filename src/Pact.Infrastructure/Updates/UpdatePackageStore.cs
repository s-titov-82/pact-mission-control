using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pact.Core.Updates;

namespace Pact.Infrastructure.Updates;

/// <summary>
/// Streams the two fixed GitHub release assets into an owned version directory and publishes
/// only files whose sizes and Setup checksum have been verified.
/// </summary>
public sealed class UpdatePackageStore : IUpdatePackageStore
{
	private const int MaximumRedirects = 5;
	private const long MaximumChecksumBytes = 1024 * 1024;
	private const string RepositoryDownloadPath =
		"/s-titov-82/pact-mission-control/releases/download/";
	private readonly HttpClient _httpClient;
	private readonly UpdatePathPolicy _pathPolicy;
	private readonly Func<string, string?> _authenticodeStatusReader;

	/// <summary>Creates a package store with a redirect-visible HTTP transport.</summary>
	public UpdatePackageStore(HttpClient httpClient, UpdatePathPolicy pathPolicy)
		: this(httpClient, pathPolicy, ReadAuthenticodeStatus)
	{
	}

	internal UpdatePackageStore(
		HttpClient httpClient,
		UpdatePathPolicy pathPolicy,
		Func<string, string?> authenticodeStatusReader)
	{
		_httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
		_pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
		_authenticodeStatusReader = authenticodeStatusReader
			?? throw new ArgumentNullException(nameof(authenticodeStatusReader));
	}

	/// <inheritdoc />
	public async Task<PreparedUpdatePackage?> TryGetVerifiedAsync(
		UpdateRelease release,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(release);
		var paths = ResolvePaths(release);
		if (!File.Exists(paths.Setup) || !File.Exists(paths.Checksums))
		{
			return null;
		}

		try
		{
			if (new FileInfo(paths.Setup).Length != release.Setup.Size
				|| new FileInfo(paths.Checksums).Length != release.Checksums.Size
				|| release.Checksums.Size > MaximumChecksumBytes)
			{
				Cleanup(paths);
				return null;
			}

			byte[] manifestBytes = await File.ReadAllBytesAsync(
				paths.Checksums,
				cancellationToken).ConfigureAwait(false);
			var manifest = ReleaseChecksumManifest.Parse(manifestBytes);
			var expectedSha256 = manifest.GetRequiredSha256(release.Setup.Name);
			await using FileStream setup = new(
				paths.Setup,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 81920,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			byte[] actual = await SHA256.HashDataAsync(setup, cancellationToken)
				.ConfigureAwait(false);
			if (!HashMatches(expectedSha256, actual))
			{
				Cleanup(paths);
				return null;
			}

			return new PreparedUpdatePackage(
				release,
				paths.Setup,
				expectedSha256,
				_authenticodeStatusReader(paths.Setup));
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception) when (CleanupAndReturnTrue(paths))
		{
			return null;
		}
	}

	/// <inheritdoc />
	public async Task<PreparedUpdatePackage> DownloadAndVerifyAsync(
		UpdateRelease release,
		IProgress<long>? progress,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(release);
		if (await TryGetVerifiedAsync(release, cancellationToken).ConfigureAwait(false)
			is { } existing)
		{
			return existing;
		}

		var paths = ResolvePaths(release);
		Directory.CreateDirectory(paths.Directory);
		Cleanup(paths);
		try
		{
			if (release.Checksums.Size > MaximumChecksumBytes)
			{
				throw new InvalidDataException("The checksum asset is unexpectedly large.");
			}

			await DownloadAssetAsync(
				release,
				release.Checksums,
				paths.ChecksumsPartial,
				progress: null,
				cancellationToken).ConfigureAwait(false);
			byte[] manifestBytes = await File.ReadAllBytesAsync(
				paths.ChecksumsPartial,
				cancellationToken).ConfigureAwait(false);
			var manifest = ReleaseChecksumManifest.Parse(manifestBytes);
			var expectedSha256 = manifest.GetRequiredSha256(release.Setup.Name);
			File.Move(paths.ChecksumsPartial, paths.Checksums, overwrite: true);

			byte[] actualSha256 = await DownloadAssetAsync(
				release,
				release.Setup,
				paths.SetupPartial,
				progress,
				cancellationToken).ConfigureAwait(false);
			if (!HashMatches(expectedSha256, actualSha256))
			{
				throw new InvalidDataException("The downloaded Setup checksum does not match.");
			}
			File.Move(paths.SetupPartial, paths.Setup, overwrite: true);

			return new PreparedUpdatePackage(
				release,
				paths.Setup,
				expectedSha256,
				_authenticodeStatusReader(paths.Setup));
		}
		catch
		{
			Cleanup(paths);
			throw;
		}
	}

	private async Task<byte[]> DownloadAssetAsync(
		UpdateRelease release,
		UpdateAsset asset,
		string partialPath,
		IProgress<long>? progress,
		CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await SendFollowingHttpsRedirectsAsync(
			release,
			asset,
			cancellationToken).ConfigureAwait(false);
		if (response.Content.Headers.ContentLength is { } contentLength
			&& contentLength != asset.Size)
		{
			throw new InvalidDataException("The release asset length does not match GitHub metadata.");
		}

		await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
			.ConfigureAwait(false);
		await using FileStream destination = new(
			partialPath,
			FileMode.CreateNew,
			FileAccess.Write,
			FileShare.None,
			bufferSize: 81920,
			FileOptions.Asynchronous | FileOptions.SequentialScan);
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		var buffer = new byte[81920];
		var total = 0L;
		while (true)
		{
			var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
			if (read == 0)
			{
				break;
			}

			total += read;
			if (total > asset.Size)
			{
				throw new InvalidDataException("The release asset is larger than GitHub metadata.");
			}
			hash.AppendData(buffer.AsSpan(0, read));
			await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
				.ConfigureAwait(false);
			progress?.Report(total);
		}

		await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
		if (total != asset.Size)
		{
			throw new InvalidDataException("The release asset is truncated.");
		}

		return hash.GetHashAndReset();
	}

	private async Task<HttpResponseMessage> SendFollowingHttpsRedirectsAsync(
		UpdateRelease release,
		UpdateAsset asset,
		CancellationToken cancellationToken)
	{
		ValidateInitialAssetUri(release, asset);
		var current = asset.DownloadUri;
		for (var redirects = 0; redirects <= MaximumRedirects; redirects++)
		{
			using HttpRequestMessage request = new(HttpMethod.Get, current);
			HttpResponseMessage response = await _httpClient.SendAsync(
				request,
				HttpCompletionOption.ResponseHeadersRead,
				cancellationToken).ConfigureAwait(false);
			if (!IsRedirect(response.StatusCode))
			{
				if (!response.IsSuccessStatusCode)
				{
					response.Dispose();
					throw new HttpRequestException(
						$"Release download returned HTTP {(int)response.StatusCode}.");
				}
				return response;
			}

			var location = response.Headers.Location;
			response.Dispose();
			if (redirects == MaximumRedirects || location is null)
			{
				throw new InvalidDataException("The release asset redirect chain is invalid.");
			}
			current = location.IsAbsoluteUri ? location : new Uri(current, location);
			if (!IsHttpsUri(current))
			{
				throw new InvalidDataException("Release asset redirects must remain HTTPS.");
			}
		}

		throw new InvalidDataException("The release asset redirect chain is invalid.");
	}

	private static void ValidateInitialAssetUri(UpdateRelease release, UpdateAsset asset)
	{
		var expectedPath = $"{RepositoryDownloadPath}{release.Tag}/{asset.Name}";
		var uri = asset.DownloadUri;
		if (!IsHttpsUri(uri)
			|| !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			|| !uri.IsDefaultPort
			|| !string.IsNullOrEmpty(uri.UserInfo)
			|| !string.IsNullOrEmpty(uri.Query)
			|| !string.IsNullOrEmpty(uri.Fragment)
			|| !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
		{
			throw new InvalidDataException("The release asset URI is not a trusted Pact release path.");
		}
	}

	private PackagePaths ResolvePaths(UpdateRelease release)
	{
		var expectedTag = $"v{release.Version}";
		var expectedSetupName =
			$"pact-mission-control-{release.Version}-win-x64-setup.exe";
		if (!string.Equals(release.Tag, expectedTag, StringComparison.Ordinal)
			|| !string.Equals(release.Setup.Name, expectedSetupName, StringComparison.Ordinal)
			|| !string.Equals(release.Checksums.Name, "SHA256SUMS.txt", StringComparison.Ordinal)
			|| release.Setup.Size <= 0 || release.Checksums.Size <= 0
			|| !IsSafeFileName(release.Setup.Name)
			|| !IsSafeFileName(release.Checksums.Name)
			|| string.Equals(release.Setup.Name, release.Checksums.Name, StringComparison.Ordinal))
		{
			throw new InvalidDataException("The release contains invalid asset metadata.");
		}

		var directory = _pathPolicy.GetPackageDirectory(release.Version);
		var setup = _pathPolicy.EnsureOwnedPath(Path.Combine(directory, release.Setup.Name));
		var checksums = _pathPolicy.EnsureOwnedPath(
			Path.Combine(directory, release.Checksums.Name));
		return new PackagePaths(
			directory,
			setup,
			setup + ".partial",
			checksums,
			checksums + ".partial");
	}

	private static bool IsSafeFileName(string value) =>
		!string.IsNullOrWhiteSpace(value)
		&& value is not "." and not ".."
		&& string.Equals(value, value.Trim(), StringComparison.Ordinal)
		&& !value.EndsWith('.')
		&& string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal)
		&& value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

	private static bool HashMatches(string expectedSha256, byte[] actualSha256) =>
		CryptographicOperations.FixedTimeEquals(
			Convert.FromHexString(expectedSha256),
			actualSha256);

	private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
		HttpStatusCode.MovedPermanently
		or HttpStatusCode.Redirect
		or HttpStatusCode.RedirectMethod
		or HttpStatusCode.TemporaryRedirect
		or HttpStatusCode.PermanentRedirect;

	private static bool IsHttpsUri(Uri uri) =>
		uri.IsAbsoluteUri
		&& string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

	private static bool CleanupAndReturnTrue(PackagePaths paths)
	{
		Cleanup(paths);
		return true;
	}

	private static void Cleanup(PackagePaths paths)
	{
		DeleteIfExists(paths.SetupPartial);
		DeleteIfExists(paths.ChecksumsPartial);
		DeleteIfExists(paths.Setup);
		DeleteIfExists(paths.Checksums);
	}

	private static void DeleteIfExists(string path)
	{
		if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

#pragma warning disable SYSLIB0057
	private static string? ReadAuthenticodeStatus(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		try
		{
			using var certificate = X509Certificate.CreateFromSignedFile(path);
			return "Signed";
		}
		catch (CryptographicException)
		{
			return "NotSigned";
		}
	}
#pragma warning restore SYSLIB0057

	private sealed record PackagePaths(
		string Directory,
		string Setup,
		string SetupPartial,
		string Checksums,
		string ChecksumsPartial);
}

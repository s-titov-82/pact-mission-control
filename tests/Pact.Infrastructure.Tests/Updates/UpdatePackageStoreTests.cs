using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using Pact.Core.Updates;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Updates;

namespace Pact.Infrastructure.Tests.Updates;

[SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Response and handler ownership is transferred to the tracked test HttpClient.")]
public sealed class UpdatePackageStoreTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private readonly List<HttpClient> _clients = [];
	public void Dispose()
	{
		foreach (var client in _clients)
		{
			client.Dispose();
		}
		_temporaryDirectory.Dispose();
	}

	[Test]
	public async Task Download_streams_both_assets_verifies_hash_and_publishes_only_final_files()
	{
		byte[] setup = Encoding.UTF8.GetBytes("verified setup bytes");
		var release = CreateRelease(setup);
		SequenceHandler handler = new([
			Ok(ChecksumBytes(release, setup)),
			Ok(setup)
		]);
		UpdatePackageStore store = CreateStore(handler);

		var package = await store.DownloadAndVerifyAsync(
			release,
			progress: null,
			CancellationToken.None);

		File.ReadAllBytes(package.SetupPath).ShouldBe(setup);
		package.SetupSha256.ShouldBe(Sha256(setup));
		package.AuthenticodeStatus.ShouldBe("NotSigned");
		Directory.GetFiles(Path.GetDirectoryName(package.SetupPath)!, "*.partial")
			.ShouldBeEmpty();
		handler.Requests.ShouldBe([
			release.Checksums.DownloadUri,
			release.Setup.DownloadUri
		]);
	}

	[TestCase("truncated")]
	[TestCase("oversized")]
	[TestCase("hash")]
	public async Task Invalid_setup_is_rejected_and_all_partial_or_final_files_are_removed(
		string failure)
	{
		byte[] expected = Encoding.UTF8.GetBytes("expected setup");
		byte[] actual = failure switch
		{
			"truncated" => expected[..^1],
			"oversized" => [.. expected, (byte)'!'],
			_ => Encoding.UTF8.GetBytes("wrong payload!")
		};
		var release = CreateRelease(expected);
		SequenceHandler handler = new([
			Ok(ChecksumBytes(release, expected)),
			Ok(actual)
		]);
		UpdatePackageStore store = CreateStore(handler);

		await Should.ThrowAsync<InvalidDataException>(() =>
			store.DownloadAndVerifyAsync(release, null, CancellationToken.None));

		var packageDirectory = new UpdatePathPolicy(new AppPaths(_temporaryDirectory.Path))
			.GetPackageDirectory(release.Version);
		Directory.Exists(packageDirectory).ShouldBeTrue();
		Directory.GetFiles(packageDirectory).ShouldBeEmpty();
	}

	[Test]
	public async Task Content_length_mismatch_is_rejected_before_publishing()
	{
		byte[] setup = Encoding.UTF8.GetBytes("setup");
		var release = CreateRelease(setup);
		HttpResponseMessage badChecksum = Ok(ChecksumBytes(release, setup));
		badChecksum.Content.Headers.ContentLength = release.Checksums.Size + 1;
		UpdatePackageStore store = CreateStore(new SequenceHandler([badChecksum]));

		await Should.ThrowAsync<InvalidDataException>(() =>
			store.DownloadAndVerifyAsync(release, null, CancellationToken.None));
	}

	[Test]
	public async Task Https_redirects_are_followed_but_non_https_redirects_are_rejected()
	{
		byte[] setup = Encoding.UTF8.GetBytes("setup");
		var release = CreateRelease(setup);
		SequenceHandler accepted = new([
			Redirect("https://release-assets.githubusercontent.com/checksums"),
			Ok(ChecksumBytes(release, setup)),
			Redirect("https://release-assets.githubusercontent.com/setup"),
			Ok(setup)
		]);
		await CreateStore(accepted).DownloadAndVerifyAsync(
			release, null, CancellationToken.None);
		Directory.Delete(
			new UpdatePathPolicy(new AppPaths(_temporaryDirectory.Path))
				.GetPackageDirectory(release.Version),
			recursive: true);

		SequenceHandler rejected = new([Redirect("http://example.test/checksums")]);
		await Should.ThrowAsync<InvalidDataException>(() =>
			CreateStore(rejected).DownloadAndVerifyAsync(
				release, null, CancellationToken.None));
		rejected.Requests.Count.ShouldBe(1);
	}

	[Test]
	public async Task Initial_asset_uri_must_be_the_exact_trusted_release_path()
	{
		byte[] setup = Encoding.UTF8.GetBytes("setup");
		var valid = CreateRelease(setup);
		var invalid = valid with
		{
			Checksums = valid.Checksums with
			{
				DownloadUri = new Uri("https://example.test/SHA256SUMS.txt")
			}
		};
		SequenceHandler handler = new([]);

		await Should.ThrowAsync<InvalidDataException>(() =>
			CreateStore(handler).DownloadAndVerifyAsync(
				invalid, null, CancellationToken.None));
		handler.Requests.ShouldBeEmpty();
	}

	[Test]
	public async Task Cancellation_leaves_no_partial_or_final_package_files()
	{
		byte[] setup = Encoding.UTF8.GetBytes("setup");
		var release = CreateRelease(setup);
		using CancellationTokenSource cancellation = new();
		await cancellation.CancelAsync();
		UpdatePackageStore store = CreateStore(new SequenceHandler([
			Ok(ChecksumBytes(release, setup))
		]));

		await Should.ThrowAsync<OperationCanceledException>(() =>
			store.DownloadAndVerifyAsync(release, null, cancellation.Token));

		var directory = new UpdatePathPolicy(new AppPaths(_temporaryDirectory.Path))
			.GetPackageDirectory(release.Version);
		Directory.GetFiles(directory).ShouldBeEmpty();
	}

	[Test]
	public async Task Fully_rehashed_staged_setup_is_reused_without_network_access()
	{
		byte[] setup = Encoding.UTF8.GetBytes("reusable setup");
		var release = CreateRelease(setup);
		await CreateStore(new SequenceHandler([
			Ok(ChecksumBytes(release, setup)),
			Ok(setup)
		])).DownloadAndVerifyAsync(release, null, CancellationToken.None);
		SequenceHandler offline = new([]);

		var package = await CreateStore(offline).TryGetVerifiedAsync(
			release,
			CancellationToken.None);

		package.ShouldNotBeNull();
		package.SetupSha256.ShouldBe(Sha256(setup));
		offline.Requests.ShouldBeEmpty();
	}

	private UpdatePackageStore CreateStore(HttpMessageHandler handler)
	{
		HttpClient client = new(handler);
		_clients.Add(client);
		return new UpdatePackageStore(
			client,
			new UpdatePathPolicy(new AppPaths(_temporaryDirectory.Path)),
			static _ => "NotSigned");
	}

	private static UpdateRelease CreateRelease(byte[] setup)
	{
		StableReleaseVersion version = new(1, 3, 0);
		var tag = $"v{version}";
		byte[] checksums = Encoding.UTF8.GetBytes(
			$"{Sha256(setup)} *pact-mission-control-{version}-win-x64-setup.exe\n");
		return new UpdateRelease(
			version,
			tag,
			new Uri($"https://github.com/s-titov-82/pact-mission-control/releases/tag/{tag}"),
			new UpdateAsset(
				$"pact-mission-control-{version}-win-x64-setup.exe",
				new Uri($"https://github.com/s-titov-82/pact-mission-control/releases/download/{tag}/pact-mission-control-{version}-win-x64-setup.exe"),
				setup.Length),
			new UpdateAsset(
				"SHA256SUMS.txt",
				new Uri($"https://github.com/s-titov-82/pact-mission-control/releases/download/{tag}/SHA256SUMS.txt"),
				checksums.Length));
	}

	private static byte[] ChecksumBytes(UpdateRelease release, byte[] setup) =>
		Encoding.UTF8.GetBytes($"{Sha256(setup)} *{release.Setup.Name}\n");

	private static string Sha256(byte[] bytes) =>
		Convert.ToHexStringLower(SHA256.HashData(bytes));

	private static HttpResponseMessage Ok(byte[] content) => new(HttpStatusCode.OK)
	{
		Content = new ByteArrayContent(content)
	};

	private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Redirect)
	{
		Headers = { Location = new Uri(location) }
	};

	private sealed class SequenceHandler(Queue<HttpResponseMessage> responses) : HttpMessageHandler
	{
		public SequenceHandler(IEnumerable<HttpResponseMessage> responses)
			: this(new Queue<HttpResponseMessage>(responses))
		{
		}

		public List<Uri> Requests { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(
			HttpRequestMessage request,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Requests.Add(request.RequestUri!);
			if (responses.Count == 0)
			{
				throw new HttpRequestException("Unexpected request.");
			}

			return Task.FromResult(responses.Dequeue());
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				while (responses.TryDequeue(out var response))
				{
					response.Dispose();
				}
			}
			base.Dispose(disposing);
		}
	}
}

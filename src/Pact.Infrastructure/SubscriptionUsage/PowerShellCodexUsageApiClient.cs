using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Pact.Infrastructure.SubscriptionUsage;
/// <summary>
/// Reads Codex rate limits from the Codex app server, launched through PowerShell so
/// profile-defined command wrappers resolve.
/// </summary>
/// <remarks>
/// The app server speaks line-delimited JSON-RPC over stdio. Its stdin must stay open until the
/// answer arrives, and the process is killed once it does: it is a server, not a command, and
/// would otherwise keep running.
/// </remarks>
public sealed class PowerShellCodexUsageApiClient : ICodexUsageApiClient
{
	private const int InitializeRequestId = 1;
	private const int RateLimitsRequestId = 2;
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
	private readonly TimeSpan _timeout;

	/// <summary>Creates a client with the default timeout.</summary>
	public PowerShellCodexUsageApiClient()
		: this(DefaultTimeout)
	{
	}

	/// <summary>Creates a client with a specific timeout.</summary>
	/// <exception cref="ArgumentOutOfRangeException">The timeout is not positive.</exception>
	public PowerShellCodexUsageApiClient(TimeSpan timeout)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

		_timeout = timeout;
	}

	/// <inheritdoc />
	public async Task<CodexUsageApiResult> ReadRateLimitsAsync(
		string commandName,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_timeout);
		Process? process = null;
		try
		{
			ProcessStartInfo startInfo = new("pwsh")
			{
				CreateNoWindow = true,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,

				// A byte-order mark on the first line would make the app server reject the
				// handshake, so the request stream is plain UTF-8.
				StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
				StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
			};
			startInfo.ArgumentList.Add("-NoLogo");
			startInfo.ArgumentList.Add("-Command");
			startInfo.ArgumentList.Add($"{commandName} app-server --stdio");

			process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Unable to start pwsh.");

			await process.StandardInput.WriteLineAsync(BuildInitializeRequest()).ConfigureAwait(false);
			await process.StandardInput.WriteLineAsync(BuildRateLimitsRequest()).ConfigureAwait(false);
			await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);

			var response = await ReadResponseAsync(process, timeout.Token).ConfigureAwait(false);
			return response is null
				? new CodexUsageApiResult(
					Succeeded: false,
					ResponseJson: string.Empty,
					FailureMessage: "Codex app server closed without answering the usage request.",
					DateTimeOffset.UtcNow)
				: new CodexUsageApiResult(true, response, null, DateTimeOffset.UtcNow);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return new CodexUsageApiResult(
				Succeeded: false,
				ResponseJson: string.Empty,
				FailureMessage: "Codex usage request timed out.",
				DateTimeOffset.UtcNow);
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
		{
			return new CodexUsageApiResult(false, string.Empty, ex.Message, DateTimeOffset.UtcNow);
		}
		finally
		{
			KillProcessTree(process);
			process?.Dispose();
		}
	}

	private static async Task<string?> ReadResponseAsync(
		Process process,
		CancellationToken cancellationToken)
	{
		while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)
			is { } line)
		{
			if (IsRateLimitsResponse(line))
			{
				return line;
			}
		}

		return null;
	}

	// Notifications and the initialize answer share the stream, so the usage answer is picked
	// out by its request id rather than by arrival order.
	private static bool IsRateLimitsResponse(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return false;
		}

		try
		{
			using var document = JsonDocument.Parse(line);
			return document.RootElement.ValueKind == JsonValueKind.Object
				&& document.RootElement.TryGetProperty("id", out var id)
				&& id.ValueKind == JsonValueKind.Number
				&& id.TryGetInt32(out var requestId)
				&& requestId == RateLimitsRequestId;
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static string BuildInitializeRequest() => JsonSerializer.Serialize(new
	{
		jsonrpc = "2.0",
		id = InitializeRequestId,
		method = "initialize",
		@params = new
		{
			clientInfo = new
			{
				name = "pact",
				title = (string?)null,
				version = typeof(PowerShellCodexUsageApiClient).Assembly.GetName().Version?.ToString()
					?? "0.0.0"
			},
			capabilities = (object?)null
		}
	});

	private static string BuildRateLimitsRequest() => JsonSerializer.Serialize(new
	{
		jsonrpc = "2.0",
		id = RateLimitsRequestId,
		method = "account/rateLimits/read",
		@params = new { excludeResetCreditDetails = true }
	});

	private static void KillProcessTree(Process? process)
	{
		if (process is null)
		{
			return;
		}

		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (Exception)
		{
		}
	}
}

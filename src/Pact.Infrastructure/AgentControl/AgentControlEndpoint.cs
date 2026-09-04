using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Pact.Infrastructure.AgentControl;

/// <summary>
/// Hosts the MCP endpoint on loopback and routes each authenticated request to its owning session.
/// </summary>
/// <remarks>
/// The listener never binds a routable address. Admission closes before shutdown drains already
/// authenticated handlers, so no new host mutation can begin while application resources stop.
/// </remarks>
public sealed class AgentControlEndpoint : IDisposable
{
	private static readonly TimeSpan DisposeDrainDeadline = TimeSpan.FromSeconds(1);

	private readonly AgentControlTokenRegistry _registry;
	private readonly AgentControlJsonRpc _rpc;
	private readonly HttpListener _listener = new();
	private readonly Lock _sync = new();
	private readonly List<Task> _inFlight = [];
	private readonly List<Task> _notificationStreams = [];
	private readonly CancellationTokenSource _requestCancellation = new();
	private readonly CancellationTokenSource _streamCancellation = new();
	private readonly AgentControlNotificationHub _notificationHub = new();
	private readonly byte[] _sessionKey = RandomNumberGenerator.GetBytes(32);
	private Task? _acceptLoop;
	private Task<AgentControlShutdownResult>? _shutdown;
	private bool _admitting;
	private bool _disposed;

	/// <summary>Creates an endpoint authenticating requests against <paramref name="registry"/>.</summary>
	public AgentControlEndpoint(AgentControlTokenRegistry registry, AgentControlJsonRpc rpc)
	{
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(rpc);
		_registry = registry;
		_rpc = rpc;
	}

	/// <summary>Gets whether the loopback listener is currently accepting connections.</summary>
	public bool IsListening => _listener.IsListening;

	/// <summary>Publishes one coalesced tool-list change to ordinary authenticated streams.</summary>
	public void PublishToolsListChanged() => _notificationHub.PublishToolsListChanged();

	/// <summary>Starts listening on the configured loopback port.</summary>
	/// <param name="port">Port from settings.</param>
	/// <returns>The address written into per-session and profile configuration.</returns>
	/// <exception cref="InvalidOperationException">
	/// The endpoint is already started or the port is unavailable. Binding elsewhere is
	/// deliberately not attempted: durable consumers such as Hermes cron jobs and the messaging
	/// gateway read this address from a file written once, so a moved endpoint is worse than a
	/// visible failure.
	/// </exception>
	public Uri Start(int port)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);

		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_acceptLoop is not null)
			{
				throw new InvalidOperationException("The agent control endpoint is already started.");
			}
		}

		var address = new Uri($"http://127.0.0.1:{port}/mcp/");
		_listener.Prefixes.Add(address.AbsoluteUri);
		try
		{
			_listener.Start();
			lock (_sync)
			{
				_admitting = true;
				_acceptLoop = AcceptLoopAsync();
			}

			return address;
		}
		catch (HttpListenerException exception)
		{
			_listener.Close();
			throw new InvalidOperationException(
				$"Could not bind the agent control endpoint to configured port {port}.",
				exception);
		}
	}

	/// <summary>Closes admission and drains every handler that was already admitted.</summary>
	/// <param name="drainDeadline">
	/// Time allowed for a clean drain before outstanding handlers are asked to cancel.
	/// </param>
	/// <returns>
	/// A shared result describing whether cancellation was unnecessary. The method never returns
	/// while an admitted handler can still mutate application state.
	/// </returns>
	public Task<AgentControlShutdownResult> ShutdownAsync(TimeSpan drainDeadline)
	{
		if (drainDeadline < TimeSpan.Zero && drainDeadline != Timeout.InfiniteTimeSpan)
		{
			throw new ArgumentOutOfRangeException(nameof(drainDeadline));
		}

		lock (_sync)
		{
			if (_shutdown is not null)
			{
				return _shutdown;
			}

			_admitting = false;
			_shutdown = DrainAsync(drainDeadline);
			return _shutdown;
		}
	}

	/// <summary>Stops listening and waits for the shared drain operation; safe to call repeatedly.</summary>
	public void Dispose()
	{
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}
		}

		ShutdownAsync(DisposeDrainDeadline).GetAwaiter().GetResult();

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_listener.Close();
			_requestCancellation.Dispose();
			_streamCancellation.Dispose();
		}
	}

	private async Task AcceptLoopAsync()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync().ConfigureAwait(false);
			}
			catch (HttpListenerException) when (IsAdmissionClosed())
			{
				return;
			}
			catch (ObjectDisposedException) when (IsAdmissionClosed())
			{
				return;
			}
			catch (InvalidOperationException) when (IsAdmissionClosed())
			{
				return;
			}

			TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
			Task? tracked = null;
			var isNotificationStream =
				string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase);
			lock (_sync)
			{
				if (_admitting)
				{
					tracked = HandleTrackedAsync(
						context,
						start.Task,
						isNotificationStream
							? _streamCancellation.Token
							: _requestCancellation.Token);
					(isNotificationStream ? _notificationStreams : _inFlight).Add(tracked);
				}
			}

			if (tracked is null)
			{
				await WriteStatusAsync(context.Response, HttpStatusCode.ServiceUnavailable)
					.ConfigureAwait(false);
				continue;
			}

			_ = tracked.ContinueWith(
				completed =>
				{
					lock (_sync)
					{
						_inFlight.Remove(completed);
						_notificationStreams.Remove(completed);
					}
				},
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
			start.SetResult();
		}
	}

	private async Task HandleTrackedAsync(
		HttpListenerContext context,
		Task start,
		CancellationToken cancellationToken)
	{
		await start.ConfigureAwait(false);
		try
		{
			await HandleAsync(context, cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			await TryWriteStatusAsync(context.Response, 499).ConfigureAwait(false);
		}
		catch (Exception)
		{
			await TryWriteStatusAsync(context.Response, HttpStatusCode.InternalServerError)
				.ConfigureAwait(false);
		}
	}

	private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
	{
		if (!TryResolveCaller(context.Request, out var caller, out var bearerToken))
		{
			await WriteStatusAsync(context.Response, HttpStatusCode.Unauthorized).ConfigureAwait(false);
			return;
		}
		if (!IsAllowedOrigin(context.Request))
		{
			await WriteStatusAsync(context.Response, HttpStatusCode.Forbidden).ConfigureAwait(false);
			return;
		}
		if (!IsValidSessionHeader(context.Request, bearerToken))
		{
			await WriteStatusAsync(context.Response, HttpStatusCode.NotFound).ConfigureAwait(false);
			return;
		}
		if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
		{
			if (!AcceptsEventStream(context.Request))
			{
				await WriteStatusAsync(context.Response, HttpStatusCode.NotAcceptable)
					.ConfigureAwait(false);
				return;
			}

			await HandleNotificationStreamAsync(
				context.Response,
				caller,
				cancellationToken).ConfigureAwait(false);
			return;
		}
		if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
		{
			await WriteStatusAsync(context.Response, HttpStatusCode.MethodNotAllowed)
				.ConfigureAwait(false);
			return;
		}

		JsonNode? request;
		try
		{
			request = await JsonNode.ParseAsync(
				context.Request.InputStream,
				cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		catch (System.Text.Json.JsonException)
		{
			await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest).ConfigureAwait(false);
			return;
		}

		if (request is null)
		{
			await WriteStatusAsync(context.Response, HttpStatusCode.BadRequest).ConfigureAwait(false);
			return;
		}

		var response = await _rpc.HandleAsync(request, caller, cancellationToken)
			.ConfigureAwait(false);
		if (response is null)
		{
			await WriteStatusAsync(context.Response, HttpStatusCode.Accepted).ConfigureAwait(false);
			return;
		}

		var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
		if (string.Equals((string?)request["method"], "initialize", StringComparison.Ordinal))
		{
			context.Response.Headers["Mcp-Session-Id"] = CreateSessionId(bearerToken);
		}

		context.Response.StatusCode = (int)HttpStatusCode.OK;
		context.Response.ContentType = "application/json";
		context.Response.ContentEncoding = Encoding.UTF8;
		context.Response.ContentLength64 = bytes.Length;
		await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
		context.Response.Close();
	}

	private async Task<AgentControlShutdownResult> DrainAsync(TimeSpan drainDeadline)
	{
		Task streams;
		Task handlers;
		lock (_sync)
		{
			streams = Task.WhenAll([.. _notificationStreams]);
			handlers = Task.WhenAll([.. _inFlight]);
		}

		var drainedCleanly = true;
		try
		{
			_notificationHub.Complete();
			_streamCancellation.Cancel();
			await streams.ConfigureAwait(false);
			try
			{
				await handlers.WaitAsync(drainDeadline).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				drainedCleanly = false;
				_requestCancellation.Cancel();
				await handlers.ConfigureAwait(false);
			}

			return new AgentControlShutdownResult(drainedCleanly);
		}
		finally
		{
			if (_listener.IsListening)
			{
				_listener.Stop();
			}

			if (_acceptLoop is not null)
			{
				await _acceptLoop.ConfigureAwait(false);
			}
		}
	}

	private bool TryResolveCaller(
		HttpListenerRequest request,
		out AgentControlCaller caller,
		out string bearerToken)
	{
		caller = null!;
		bearerToken = string.Empty;
		var authorization = request.Headers["Authorization"];
		const string prefix = "Bearer ";
		if (authorization?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true)
		{
			return false;
		}

		bearerToken = authorization[prefix.Length..].Trim();
		return _registry.TryResolveCaller(bearerToken, out caller);
	}

	private async Task HandleNotificationStreamAsync(
		HttpListenerResponse response,
		AgentControlCaller caller,
		CancellationToken cancellationToken)
	{
		await using AgentControlNotificationSubscription subscription =
			_notificationHub.Subscribe(caller);
		response.StatusCode = (int)HttpStatusCode.OK;
		response.ContentType = "text/event-stream";
		response.Headers["Cache-Control"] = "no-cache";
		response.Headers["X-Accel-Buffering"] = "no";
		response.SendChunked = true;
		await response.OutputStream.WriteAsync(
			Encoding.UTF8.GetBytes(": connected\n\n"),
			cancellationToken).ConfigureAwait(false);
		await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await foreach (string payload in subscription.Reader.ReadAllAsync(cancellationToken)
				.ConfigureAwait(false))
			{
				byte[] frame = Encoding.UTF8.GetBytes($"data: {payload}\n\n");
				await response.OutputStream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
				await response.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception exception) when (
			exception is OperationCanceledException
				or HttpListenerException
				or ObjectDisposedException
				or IOException)
		{
		}
		finally
		{
			try
			{
				response.Close();
			}
			catch (ObjectDisposedException)
			{
			}
		}
	}

	private string CreateSessionId(string bearerToken)
	{
		byte[] digest = HMACSHA256.HashData(_sessionKey, Encoding.UTF8.GetBytes(bearerToken));
		return Convert.ToBase64String(digest)
			.TrimEnd('=')
			.Replace('+', '-')
			.Replace('/', '_');
	}

	private bool IsValidSessionHeader(HttpListenerRequest request, string bearerToken)
	{
		var supplied = request.Headers["Mcp-Session-Id"];
		if (string.IsNullOrEmpty(supplied))
		{
			return true;
		}

		try
		{
			string base64 = supplied.Replace('-', '+').Replace('_', '/');
			base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
			byte[] suppliedDigest = Convert.FromBase64String(base64);
			byte[] expectedDigest =
				HMACSHA256.HashData(_sessionKey, Encoding.UTF8.GetBytes(bearerToken));
			return suppliedDigest.Length == expectedDigest.Length
				&& CryptographicOperations.FixedTimeEquals(suppliedDigest, expectedDigest);
		}
		catch (FormatException)
		{
			return false;
		}
	}

	private static bool IsAllowedOrigin(HttpListenerRequest request)
	{
		var origin = request.Headers["Origin"];
		return string.IsNullOrWhiteSpace(origin)
			|| (Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.IsLoopback);
	}

	private static bool AcceptsEventStream(HttpListenerRequest request) =>
		request.AcceptTypes?.Any(value =>
			value.Split(';', 2)[0].Trim().Equals(
				"text/event-stream",
				StringComparison.OrdinalIgnoreCase)) == true;

	private bool IsAdmissionClosed()
	{
		lock (_sync)
		{
			return !_admitting;
		}
	}

	private static async Task WriteStatusAsync(
		HttpListenerResponse response,
		HttpStatusCode statusCode)
	{
		response.StatusCode = (int)statusCode;
		response.ContentLength64 = 0;
		await response.OutputStream.FlushAsync().ConfigureAwait(false);
		response.Close();
	}

	private static async Task TryWriteStatusAsync(
		HttpListenerResponse response,
		HttpStatusCode statusCode) =>
		await TryWriteStatusAsync(response, (int)statusCode).ConfigureAwait(false);

	private static async Task TryWriteStatusAsync(HttpListenerResponse response, int statusCode)
	{
		try
		{
			response.StatusCode = statusCode;
			response.ContentLength64 = 0;
			await response.OutputStream.FlushAsync().ConfigureAwait(false);
			response.Close();
		}
		catch (Exception exception) when (
			exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
		{
			// The client or listener has already gone away; the tracked task still completes normally.
		}
	}
}

/// <summary>Outcome of closing the agent control endpoint.</summary>
/// <param name="DrainedCleanly">
/// Whether every admitted handler completed before the deadline without cancellation.
/// </param>
public sealed record AgentControlShutdownResult(bool DrainedCleanly);

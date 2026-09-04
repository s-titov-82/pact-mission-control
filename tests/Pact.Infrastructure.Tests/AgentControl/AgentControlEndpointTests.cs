using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class AgentControlEndpointTests : IDisposable
{
	private AgentControlTokenRegistry _registry = null!;
	private AgentControlEndpoint _endpoint = null!;
	private Uri _address = null!;
	private HttpClient _client = null!;
	private TaskCompletionSource _handlerEntered = null!;
	private TaskCompletionSource _handlerMayFinish = null!;
	private CancellationToken _handlerCancellation;
	private int _lastFreePort;

	[SetUp]
	public void SetUp()
	{
		_registry = new AgentControlTokenRegistry();
		_handlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_handlerMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		AgentControlJsonRpc rpc = new(
			_ => new JsonObject { ["tools"] = new JsonArray() },
			async (call, cancellationToken) =>
			{
				_handlerCancellation = cancellationToken;
				switch (call.ToolName)
				{
					case "slow":
						_handlerEntered.TrySetResult();
						await _handlerMayFinish.Task;
						break;

					case "obedient":
						_handlerEntered.TrySetResult();
						await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
						break;
				}

				return new AgentControlResultData("ok", IsError: false);
			});
		_endpoint = new AgentControlEndpoint(_registry, rpc);
		_address = _endpoint.Start(FreePort());
		_client = new HttpClient();
	}

	[TearDown]
	public void TearDown()
	{
		_handlerMayFinish.TrySetResult();
		Dispose();
	}

	public void Dispose()
	{
		_endpoint?.Dispose();
		_client?.Dispose();
	}

	[Test]
	public void Start_BindsTheConfiguredPort()
	{
		_address.Port.ShouldBe(_lastFreePort);
		_address.Host.ShouldBe("127.0.0.1");
		IPAddress.IsLoopback(IPAddress.Parse(_address.Host)).ShouldBeTrue();
	}

	[Test]
	public void Start_ThrowsWhenTheConfiguredPortIsTaken()
	{
		using AgentControlEndpoint second = new(
			new AgentControlTokenRegistry(),
			new AgentControlJsonRpc(
				_ => new JsonObject { ["tools"] = new JsonArray() },
				(_, _) => Task.FromResult(new AgentControlResultData("ok", IsError: false))));

		Uri act()
		{
			return second.Start(_address.Port);
		}

		Should.Throw<InvalidOperationException>((Func<Uri>)act).Message
			.ShouldContain(_address.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
	}

	[Test]
	public async Task Post_RejectsRequestWithoutToken()
	{
		(await PostStatusAsync(_client, token: null)).ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task Post_RejectsUnknownToken()
	{
		(await PostStatusAsync(_client, "not-a-token")).ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task Post_AnswersInitializeForIssuedToken()
	{
		var token = _registry.Issue("session-1");
		using var response = await PostAsync(_client, token);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!["serverInfo"]!["name"]!
			.GetValue<string>().ShouldBe("pact");
	}

	[Test]
	public async Task Initialize_returns_a_bearer_bound_mcp_session_id()
	{
		var token = _registry.Issue("session-1");
		using var initialize = await PostAsync(_client, token);
		string sessionId = initialize.Headers.GetValues("Mcp-Session-Id").Single();
		sessionId.Length.ShouldBe(43);
		sessionId.All(character => character is >= '!' and <= '~').ShouldBeTrue();

		using var accepted = await PostAsync(_client, token, sessionId: sessionId);
		accepted.StatusCode.ShouldBe(HttpStatusCode.OK);
		using var rejected = await PostAsync(_client, token, sessionId: new string('x', 43));
		rejected.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Test]
	public async Task Get_stream_receives_tools_list_changed_notification()
	{
		var token = _registry.Issue("session-1");
		using var request = CreateGet(token);
		using HttpResponseMessage response = await _client.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead);
		response.StatusCode.ShouldBe(HttpStatusCode.OK);
		using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
		(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(": connected");
		(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeEmpty();

		_endpoint.PublishToolsListChanged();
		(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(
			$"data: {AgentControlNotificationHub.ToolsListChangedJson}");
		(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeEmpty();
	}

	[Test]
	public async Task Get_accepts_an_authenticated_request_without_a_session_header()
	{
		var token = _registry.Issue("session-1");
		using var request = CreateGet(token);
		using HttpResponseMessage response = await _client.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead);

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Test]
	public async Task Get_validates_a_supplied_session_id_and_allows_loopback_origin()
	{
		var token = _registry.Issue("session-1");
		using var initialize = await PostAsync(_client, token);
		string sessionId = initialize.Headers.GetValues("Mcp-Session-Id").Single();

		using var accepted = CreateGet(token, sessionId);
		accepted.Headers.Add("Origin", "http://localhost");
		using HttpResponseMessage acceptedResponse = await _client.SendAsync(
			accepted,
			HttpCompletionOption.ResponseHeadersRead);
		acceptedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

		using var rejected = CreateGet(token, new string('x', 43));
		using var rejectedResponse = await _client.SendAsync(rejected);
		rejectedResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Test]
	public async Task Get_rejects_missing_token_non_sse_accept_and_non_loopback_origin()
	{
		using var missingToken = CreateGet(token: null);
		using var missingTokenResponse = await _client.SendAsync(missingToken);
		missingTokenResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		var token = _registry.Issue("session-1");
		using var wrongAccept = new HttpRequestMessage(HttpMethod.Get, _address);
		wrongAccept.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		using var wrongAcceptResponse = await _client.SendAsync(wrongAccept);
		wrongAcceptResponse.StatusCode.ShouldBe(HttpStatusCode.NotAcceptable);

		using var foreignOrigin = CreateGet(token);
		foreignOrigin.Headers.Add("Origin", "https://example.test");
		using var foreignOriginResponse = await _client.SendAsync(foreignOrigin);
		foreignOriginResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
	}

	[Test]
	public async Task ShutdownAsync_closes_notification_stream_without_waiting_for_post_deadline()
	{
		var token = _registry.Issue("session-1");
		using var request = CreateGet(token);
		using HttpResponseMessage response = await _client.SendAsync(
			request,
			HttpCompletionOption.ResponseHeadersRead);
		using Stream stream = await response.Content.ReadAsStreamAsync();
		using StreamReader reader = new(stream, leaveOpen: true);
		(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBe(": connected");
		(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeEmpty();

		var result = await _endpoint.ShutdownAsync(TimeSpan.FromSeconds(5));

		result.DrainedCleanly.ShouldBeTrue();
		try
		{
			(await stream.ReadAsync(new byte[1])).ShouldBe(0);
		}
		catch (IOException)
		{
			// HttpListener may surface its immediate server-side close as a TCP reset.
		}
	}

	[Test]
	public async Task Post_answers_initialize_for_the_orchestrator_credential()
	{
		_registry.SetOrchestratorCredential("slot-token");

		using var response = await PostAsync(_client, "slot-token");

		response.StatusCode.ShouldBe(HttpStatusCode.OK);
	}

	[Test]
	public async Task Post_RejectsTokenAfterRevoke()
	{
		var token = _registry.Issue("session-1");
		_registry.Revoke("session-1");
		(await PostStatusAsync(_client, token)).ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task ShutdownAsync_RefusesARequestArrivingDuringAnActiveDrain()
	{
		var token = _registry.Issue("session-1");
		var pending = PostStatusAsync(
			_client,
			token,
			"""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"slow","arguments":{}}}""");
		await _handlerEntered.Task;
		var shutdown = _endpoint.ShutdownAsync(TimeSpan.FromSeconds(10));

		var duringDrain = await PostStatusAsync(_client, token);

		duringDrain.ShouldBe(HttpStatusCode.ServiceUnavailable);
		_handlerMayFinish.SetResult();
		await shutdown;
		(await pending).ShouldBe(HttpStatusCode.OK);
	}

	[Test]
	public async Task ShutdownAsync_WaitsForAnAlreadyAdmittedHandler()
	{
		var token = _registry.Issue("session-1");
		var pending = PostStatusAsync(
			_client,
			token,
			"""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"slow","arguments":{}}}""");

		await _handlerEntered.Task;
		var shutdown = _endpoint.ShutdownAsync(TimeSpan.FromSeconds(10));

		shutdown.IsCompleted.ShouldBeFalse();
		_handlerMayFinish.SetResult();
		await shutdown;
		(await pending).ShouldBe(HttpStatusCode.OK);
	}

	[Test]
	public async Task ShutdownAsync_CancelsButKeepsWaitingWhenAHandlerOutlivesTheDeadline()
	{
		var token = _registry.Issue("session-1");
		var pending = PostStatusAsync(
			_client,
			token,
			"""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"slow","arguments":{}}}""");
		await _handlerEntered.Task;

		var shutdown = _endpoint.ShutdownAsync(TimeSpan.FromMilliseconds(50));

		await WaitUntilAsync(() => _handlerCancellation.IsCancellationRequested);
		shutdown.IsCompleted.ShouldBeFalse();

		_handlerMayFinish.SetResult();
		var expired = await shutdown;

		expired.DrainedCleanly.ShouldBeFalse();
		(await pending).ShouldBe((HttpStatusCode)499);
	}

	[Test]
	public async Task ShutdownAsync_EndsCleanlyWhenAHandlerHonoursItsCancellation()
	{
		var token = _registry.Issue("session-1");
		var pending = PostStatusAsync(
			_client,
			token,
			"""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"obedient","arguments":{}}}""");
		await _handlerEntered.Task;

		var expired = await _endpoint.ShutdownAsync(TimeSpan.FromMilliseconds(50));

		expired.DrainedCleanly.ShouldBeFalse();
		_endpoint.IsListening.ShouldBeFalse();
		(await pending).ShouldBe((HttpStatusCode)499);
	}

	[Test]
	public async Task ShutdownAsync_JoinsOneSharedDrainForEveryCaller()
	{
		var token = _registry.Issue("session-1");
		var pending = PostStatusAsync(
			_client,
			token,
			"""{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"slow","arguments":{}}}""");
		await _handlerEntered.Task;

		var first = _endpoint.ShutdownAsync(TimeSpan.FromMilliseconds(50));
		var second = _endpoint.ShutdownAsync(TimeSpan.FromMinutes(1));

		await WaitUntilAsync(() => _handlerCancellation.IsCancellationRequested);
		first.IsCompleted.ShouldBeFalse();
		second.IsCompleted.ShouldBeFalse();

		_handlerMayFinish.SetResult();
		var results = await Task.WhenAll(first, second);

		results[0].DrainedCleanly.ShouldBeFalse();
		results[1].DrainedCleanly.ShouldBeFalse();
		await pending;
	}

	private async Task<HttpResponseMessage> PostAsync(
		HttpClient client,
		string? token,
		string body = """{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
		string? sessionId = null)
	{
		using HttpRequestMessage request = new(HttpMethod.Post, _address)
		{
			Content = new StringContent(body, Encoding.UTF8, "application/json")
		};

		if (token is not null)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}
		if (sessionId is not null)
		{
			request.Headers.Add("Mcp-Session-Id", sessionId);
		}

		return await client.SendAsync(request);
	}

	private HttpRequestMessage CreateGet(string? token, string? sessionId = null)
	{
		HttpRequestMessage request = new(HttpMethod.Get, _address);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		if (token is not null)
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}
		if (sessionId is not null)
		{
			request.Headers.Add("Mcp-Session-Id", sessionId);
		}

		return request;
	}

	private async Task<HttpStatusCode> PostStatusAsync(
		HttpClient client,
		string? token,
		string body = """{"jsonrpc":"2.0","id":1,"method":"initialize"}""")
	{
		using var response = await PostAsync(client, token, body);
		return response.StatusCode;
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var stopwatch = Stopwatch.StartNew();
		while (!condition())
		{
			if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
			{
				throw new TimeoutException("Condition was not reached.");
			}

			await Task.Yield();
		}
	}

	private int FreePort()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			_lastFreePort = ((IPEndPoint)listener.LocalEndpoint).Port;
			return _lastFreePort;
		}
		finally
		{
			listener.Stop();
		}
	}
}

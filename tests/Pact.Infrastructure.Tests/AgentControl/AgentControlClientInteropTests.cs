using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class AgentControlClientInteropTests
{
	private const string Token = "interop-token";

	[Test]
	[Explicit("Requires the installed Codex CLI and a loopback HTTP listener.")]
	public async Task Installed_codex_opens_authenticated_sse_with_returned_session_id()
	{
		await using ProbeServer probe = new();
		using Process process = Start(
			"codex",
			[
				"debug",
				"prompt-input",
				"-c",
				$"mcp_servers.pact_probe.url={probe.Address}",
				"-c",
				"mcp_servers.pact_probe.bearer_token_env_var=PACT_AGENT_CONTROL_TOKEN"
			],
			throughPowerShellProfile: true);

		try
		{
			RecordedGet request =
				await probe.GetOpened.Task.WaitAsync(TimeSpan.FromSeconds(15));
			AssertSseRequest(request, requireSessionId: true);
		}
		catch (TimeoutException)
		{
			Stop(process);
			string error = await process.StandardError.ReadToEndAsync();
			throw new AssertionException(
				$"Codex opened no SSE stream. Requests: {string.Join(", ", probe.SeenMethods)}."
				+ $" Stderr: {error}");
		}
		finally
		{
			Stop(process);
		}
	}

	[Test]
	[Explicit("Requires the installed Claude CLI and a loopback HTTP listener.")]
	public async Task Installed_claude_opens_authenticated_sse_with_compatible_session_header()
	{
		await using ProbeServer probe = new();
		using var directory = TemporaryDirectory.Create();
		var configPath = Path.Combine(directory.Path, "mcp.json");
		await File.WriteAllTextAsync(
			configPath,
			new JsonObject
			{
				["mcpServers"] = new JsonObject
				{
					["pact_probe"] = new JsonObject
					{
						["type"] = "http",
						["url"] = probe.Address.AbsoluteUri,
						["headers"] = new JsonObject
						{
							["Authorization"] = "Bearer ${PACT_AGENT_CONTROL_TOKEN}"
						}
					}
				}
			}.ToJsonString());
		using Process process = Start(
			"claude",
			["--mcp-config", configPath, "-p", "Reply only OK"]);

		RecordedGet request =
			await probe.GetOpened.Task.WaitAsync(TimeSpan.FromSeconds(15));

		AssertSseRequest(request, requireSessionId: false);
		Stop(process);
	}

	[Test]
	[Explicit("Requires the installed Codex CLI and a loopback HTTP listener.")]
	public async Task Installed_codex_opens_sse_through_the_real_endpoint()
	{
		AgentControlTokenRegistry registry = new();
		var token = registry.Issue("session-1");
		using AgentControlEndpoint endpoint = CreateEndpoint(registry);
		Uri endpointAddress = endpoint.Start(FreePort());
		await using TransparentProxy proxy = new(endpointAddress);
		using Process process = Start(
			"codex",
			[
				"debug",
				"prompt-input",
				"-c",
				$"mcp_servers.pact_probe.url={proxy.Address}",
				"-c",
				"mcp_servers.pact_probe.bearer_token_env_var=PACT_AGENT_CONTROL_TOKEN"
			],
			throughPowerShellProfile: true,
			bearerToken: token);

		try
		{
			RecordedGet request =
				await proxy.GetOpened.Task.WaitAsync(TimeSpan.FromSeconds(15));
			request.Authorization.ShouldBe($"Bearer {token}");
			request.SessionId.ShouldNotBeNullOrWhiteSpace();
		}
		finally
		{
			Stop(process);
		}
	}

	[Test]
	[Explicit("Requires the installed Claude CLI and a loopback HTTP listener.")]
	public async Task Installed_claude_opens_sse_through_the_real_endpoint()
	{
		AgentControlTokenRegistry registry = new();
		var token = registry.Issue("session-1");
		using AgentControlEndpoint endpoint = CreateEndpoint(registry);
		Uri endpointAddress = endpoint.Start(FreePort());
		await using TransparentProxy proxy = new(endpointAddress);
		using var directory = TemporaryDirectory.Create();
		var configPath = Path.Combine(directory.Path, "mcp.json");
		await WriteClaudeConfigAsync(configPath, proxy.Address);
		using Process process = Start(
			"claude",
			["--mcp-config", configPath, "-p", "Reply only OK"],
			bearerToken: token);

		try
		{
			RecordedGet request =
				await proxy.GetOpened.Task.WaitAsync(TimeSpan.FromSeconds(15));
			request.Authorization.ShouldBe($"Bearer {token}");
			(request.SessionId is null || request.SessionId.Length == 43).ShouldBeTrue();
		}
		finally
		{
			Stop(process);
		}
	}

	private static AgentControlEndpoint CreateEndpoint(AgentControlTokenRegistry registry) =>
		new(
			registry,
			new AgentControlJsonRpc(
				_ => new JsonObject { ["tools"] = new JsonArray() },
				(_, _) => Task.FromResult(
					new AgentControlResultData("ok", IsError: false))));

	private static Task WriteClaudeConfigAsync(string path, Uri address) =>
		File.WriteAllTextAsync(
			path,
			new JsonObject
			{
				["mcpServers"] = new JsonObject
				{
					["pact_probe"] = new JsonObject
					{
						["type"] = "http",
						["url"] = address.AbsoluteUri,
						["headers"] = new JsonObject
						{
							["Authorization"] = "Bearer ${PACT_AGENT_CONTROL_TOKEN}"
						}
					}
				}
			}.ToJsonString());

	private static void AssertSseRequest(RecordedGet request, bool requireSessionId)
	{
		request.Authorization.ShouldBe($"Bearer {Token}");
		request.Accept.ShouldContain("text/event-stream");
		if (requireSessionId)
		{
			request.SessionId.ShouldBe(ProbeServer.SessionId);
		}
		else
		{
			(request.SessionId is null || request.SessionId == ProbeServer.SessionId).ShouldBeTrue();
		}
	}

	private static Process Start(
		string fileName,
		IReadOnlyList<string> arguments,
		bool throughPowerShellProfile = false,
		string bearerToken = Token)
	{
		if (throughPowerShellProfile)
		{
			var script = new StringBuilder("$arguments = @(");
			var first = true;
			foreach (string argument in arguments)
			{
				if (!first)
				{
					script.Append(',');
				}

				script
					.Append('\'')
					.Append(argument.Replace("'", "''", StringComparison.Ordinal))
					.Append('\'');
				first = false;
			}

			script.Append("); ").Append(fileName).Append(" @arguments");
			arguments =
			[
				"-NoLogo",
				"-EncodedCommand",
				Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()))
			];
			fileName = "pwsh";
		}

		ProcessStartInfo startInfo = new()
		{
			FileName = fileName,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (string argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		startInfo.Environment["PACT_AGENT_CONTROL_TOKEN"] = bearerToken;
		return Process.Start(startInfo)
			?? throw new InvalidOperationException($"Could not start installed CLI '{fileName}'.");
	}

	private static void Stop(Process process)
	{
		if (!process.HasExited)
		{
			process.Kill(entireProcessTree: true);
			process.WaitForExit();
		}
	}

	private sealed record RecordedGet(
		string? Authorization,
		string? SessionId,
		IReadOnlyList<string> Accept);

	private static int FreePort()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}

	private sealed class ProbeServer : IAsyncDisposable
	{
		public const string SessionId = "pact-probe-session";
		private readonly HttpListener _listener = new();
		private readonly CancellationTokenSource _stopping = new();
		private readonly Task _acceptLoop;

		public ProbeServer()
		{
			Address = new Uri($"http://127.0.0.1:{AgentControlClientInteropTests.FreePort()}/mcp/");
			_listener.Prefixes.Add(Address.AbsoluteUri);
			_listener.Start();
			_acceptLoop = AcceptLoopAsync();
		}

		public Uri Address { get; }
		public TaskCompletionSource<RecordedGet> GetOpened { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public ConcurrentQueue<string> SeenMethods { get; } = new();

		public async ValueTask DisposeAsync()
		{
			_stopping.Cancel();
			_listener.Close();
			try
			{
				await _acceptLoop;
			}
			catch (Exception exception) when (
				exception is HttpListenerException or ObjectDisposedException)
			{
			}

			_stopping.Dispose();
		}

		private async Task AcceptLoopAsync()
		{
			while (_listener.IsListening)
			{
				HttpListenerContext context = await _listener.GetContextAsync();
				_ = HandleAsync(context);
			}
		}

		private async Task HandleAsync(HttpListenerContext context)
		{
			if (context.Request.HttpMethod == "GET")
			{
				GetOpened.TrySetResult(new RecordedGet(
					context.Request.Headers["Authorization"],
					context.Request.Headers["Mcp-Session-Id"],
					context.Request.AcceptTypes ?? []));
				context.Response.StatusCode = (int)HttpStatusCode.OK;
				context.Response.ContentType = "text/event-stream";
				context.Response.SendChunked = true;
				await context.Response.OutputStream.FlushAsync();
				try
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, _stopping.Token);
				}
				catch (OperationCanceledException)
				{
				}

				context.Response.Close();
				return;
			}

			JsonNode? request = await JsonNode.ParseAsync(context.Request.InputStream);
			string method = (string?)request?["method"] ?? string.Empty;
			SeenMethods.Enqueue(method);
			JsonNode? id = request?["id"]?.DeepClone();
			if (id is null)
			{
				context.Response.StatusCode = (int)HttpStatusCode.Accepted;
				context.Response.Close();
				return;
			}

			JsonNode result = method switch
			{
				"initialize" => new JsonObject
				{
					["protocolVersion"] = "2025-06-18",
					["capabilities"] = new JsonObject
					{
						["tools"] = new JsonObject { ["listChanged"] = true }
					},
					["serverInfo"] = new JsonObject
					{
						["name"] = "pact-probe",
						["version"] = "1"
					}
				},
				"tools/list" => new JsonObject { ["tools"] = new JsonArray() },
				_ => new JsonObject()
			};
			JsonObject response = new()
			{
				["jsonrpc"] = "2.0",
				["id"] = id,
				["result"] = result
			};
			if (method == "initialize")
			{
				context.Response.Headers["Mcp-Session-Id"] = SessionId;
			}

			byte[] bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
			context.Response.StatusCode = (int)HttpStatusCode.OK;
			context.Response.ContentType = "application/json";
			context.Response.ContentLength64 = bytes.Length;
			await context.Response.OutputStream.WriteAsync(bytes);
			context.Response.Close();
		}

	}

	private sealed class TransparentProxy : IAsyncDisposable
	{
		private readonly Uri _target;
		private readonly HttpListener _listener = new();
		private readonly HttpClient _client = new();
		private readonly Task _acceptLoop;

		public TransparentProxy(Uri target)
		{
			_target = target;
			Address = new Uri(
				$"http://127.0.0.1:{AgentControlClientInteropTests.FreePort()}/mcp/");
			_listener.Prefixes.Add(Address.AbsoluteUri);
			_listener.Start();
			_acceptLoop = AcceptLoopAsync();
		}

		public Uri Address { get; }
		public TaskCompletionSource<RecordedGet> GetOpened { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask DisposeAsync()
		{
			_listener.Close();
			_client.Dispose();
			try
			{
				await _acceptLoop;
			}
			catch (Exception exception) when (
				exception is HttpListenerException or ObjectDisposedException)
			{
			}
		}

		private async Task AcceptLoopAsync()
		{
			while (_listener.IsListening)
			{
				HttpListenerContext context = await _listener.GetContextAsync();
				_ = ForwardAsync(context);
			}
		}

		private async Task ForwardAsync(HttpListenerContext context)
		{
			using HttpRequestMessage request =
				new(new HttpMethod(context.Request.HttpMethod), _target);
			foreach (string? name in context.Request.Headers.AllKeys)
			{
				if (name is not null && !name.Equals("Host", StringComparison.OrdinalIgnoreCase))
				{
					request.Headers.TryAddWithoutValidation(
						name,
						context.Request.Headers[name]);
				}
			}

			if (context.Request.HasEntityBody)
			{
				using MemoryStream body = new();
				await context.Request.InputStream.CopyToAsync(body);
				request.Content = new ByteArrayContent(body.ToArray());
				if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
				{
					request.Content.Headers.TryAddWithoutValidation(
						"Content-Type",
						context.Request.ContentType);
				}
			}

			if (context.Request.HttpMethod == "GET")
			{
				GetOpened.TrySetResult(new RecordedGet(
					context.Request.Headers["Authorization"],
					context.Request.Headers["Mcp-Session-Id"],
					context.Request.AcceptTypes ?? []));
			}

			try
			{
				using HttpResponseMessage upstream = await _client.SendAsync(
					request,
					HttpCompletionOption.ResponseHeadersRead);
				context.Response.StatusCode = (int)upstream.StatusCode;
				foreach (var header in upstream.Headers.Concat(upstream.Content.Headers))
				{
					if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
						&& !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
					{
						context.Response.Headers[header.Key] = string.Join(",", header.Value);
					}
				}

				if (context.Request.HttpMethod == "GET")
				{
					context.Response.SendChunked = true;
					await using Stream source = await upstream.Content.ReadAsStreamAsync();
					await source.CopyToAsync(context.Response.OutputStream);
				}
				else
				{
					byte[] bytes = await upstream.Content.ReadAsByteArrayAsync();
					context.Response.ContentLength64 = bytes.Length;
					await context.Response.OutputStream.WriteAsync(bytes);
				}
			}
			catch (Exception exception) when (
				exception is HttpRequestException
					or IOException
					or ObjectDisposedException
					or HttpListenerException)
			{
			}
			finally
			{
				try
				{
					context.Response.Close();
				}
				catch (ObjectDisposedException)
				{
				}
			}
		}
	}
}

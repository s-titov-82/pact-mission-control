using System.Security.Cryptography;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Identifies the principal that presented a validated agent-control token.</summary>
/// <param name="SessionId">
/// The calling session, or <see langword="null"/> for the orchestrator.
/// </param>
/// <param name="IsOrchestrator">
/// Whether the caller holds the slot credential and may use cross-session tools.
/// </param>
public sealed record AgentControlCaller(string? SessionId, bool IsOrchestrator);

/// <summary>
/// Maps bearer tokens to their callers. Session tokens live only in memory for the lifetime of
/// their session; the orchestrator credential is durable configuration loaded into the registry.
/// </summary>
/// <remarks>
/// This routes requests; it does not isolate same-user sessions from each other. A token proves
/// only that the caller was started by this Pact instance.
/// </remarks>
public sealed class AgentControlTokenRegistry
{
	private readonly Lock _sync = new();
	private readonly Dictionary<string, string> _sessionByToken = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _tokenBySession = new(StringComparer.Ordinal);
	private string? _orchestratorCredential;

	/// <summary>Mints a token, replacing any token already issued for the session.</summary>
	public string Issue(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

		lock (_sync)
		{
			if (_tokenBySession.TryGetValue(sessionId, out var previous))
			{
				_sessionByToken.Remove(previous);
			}

			_tokenBySession[sessionId] = token;
			_sessionByToken[token] = sessionId;
		}

		return token;
	}

	/// <summary>Resolves an issued token without revealing whether any other session exists.</summary>
	public bool TryResolve(string token, out string sessionId)
	{
		if (TryResolveCaller(token, out var caller) && caller.SessionId is { } resolvedSessionId)
		{
			sessionId = resolvedSessionId;
			return true;
		}

		sessionId = string.Empty;
		return false;
	}

	/// <summary>
	/// Sets or clears the durable orchestrator credential. Replacing it immediately revokes the
	/// previous value.
	/// </summary>
	public void SetOrchestratorCredential(string? credential)
	{
		lock (_sync)
		{
			_orchestratorCredential = string.IsNullOrWhiteSpace(credential)
				? null
				: credential;
		}
	}

	/// <summary>Resolves a token to its session or orchestrator principal.</summary>
	public bool TryResolveCaller(string token, out AgentControlCaller caller)
	{
		caller = null!;
		if (string.IsNullOrWhiteSpace(token))
		{
			return false;
		}

		lock (_sync)
		{
			if (string.Equals(_orchestratorCredential, token, StringComparison.Ordinal))
			{
				caller = new AgentControlCaller(SessionId: null, IsOrchestrator: true);
				return true;
			}

			if (_sessionByToken.TryGetValue(token, out var sessionId))
			{
				caller = new AgentControlCaller(sessionId, IsOrchestrator: false);
				return true;
			}
		}

		return false;
	}

	/// <summary>Revokes the token for a session; safe when no token was issued.</summary>
	public void Revoke(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

		lock (_sync)
		{
			if (_tokenBySession.Remove(sessionId, out var token))
			{
				_sessionByToken.Remove(token);
			}
		}
	}
}

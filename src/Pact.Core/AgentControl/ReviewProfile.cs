using Pact.Core.Agents;

namespace Pact.Core.AgentControl;

/// <summary>
/// A launch template used only to create reviewer sessions. It is separate from shell profiles
/// so reviewer-only model and effort flags never appear in the normal project launch menu.
/// </summary>
/// <param name="Id">Stable key named by a review request; it must survive edits.</param>
/// <param name="DisplayName">Label shown in settings and request metadata.</param>
/// <param name="Kind">Agent kind that selects terminal compatibility behavior.</param>
/// <param name="CommandTemplate">Full reviewer command line, including model and effort flags.</param>
public sealed record ReviewProfile(
	string Id,
	string DisplayName,
	AgentKind Kind,
	string CommandTemplate);

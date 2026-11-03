namespace Pact.Core.Updates;

/// <summary>Identifies the project or ROOT item selected immediately before a soft restart.</summary>
/// <param name="ProjectId">The owning project id, or <see langword="null"/> for ROOT.</param>
/// <param name="ItemId">The selected terminal, browser, or project-surface id.</param>
/// <param name="ProjectScreenTab">Optional project tab name; absent in legacy checkpoints.</param>
/// <param name="MarkdownRelativePath">Selected project Markdown path for the Docs tab.</param>
public sealed record SoftRestartSelection(
	string? ProjectId,
	string? ItemId,
	string? ProjectScreenTab = null,
	string? MarkdownRelativePath = null);

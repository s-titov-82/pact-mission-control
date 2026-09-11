namespace Pact.Core.Updates;

/// <summary>Identifies the project or ROOT item selected immediately before a soft restart.</summary>
/// <param name="ProjectId">The owning project id, or <see langword="null"/> for ROOT.</param>
/// <param name="ItemId">The selected terminal, browser, or project-surface id.</param>
public sealed record SoftRestartSelection(string? ProjectId, string? ItemId);

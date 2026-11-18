namespace Pact.Core.Boards;

/// <summary>A durable explicit comment, not a captured terminal message.</summary>
public sealed record BoardComment(string Id, long Number, string? AuthorSessionId, string AuthorName, DateTimeOffset CreatedAt, string Text);

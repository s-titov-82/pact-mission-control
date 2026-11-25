namespace Pact.Infrastructure.AgentControl;

/// <summary>Changes an agent-owned MCP declaration or its native availability setting.</summary>
public enum McpNativeEditAction
{
	/// <summary>Enable the declaration in its native scope.</summary>
	Enable,
	/// <summary>Disable the declaration while retaining its settings and credentials.</summary>
	Disable,
	/// <summary>Remove the selected declaration from its native configuration file.</summary>
	Remove
}

/// <summary>A revision-bound edit whose public summary contains file paths, never configuration values.</summary>
public sealed class McpNativeEditPreview
{
	internal McpNativeEditPreview(string sourcePath, string sourceContent, McpNativeFileEdit[] edits)
	{
		SourcePath = sourcePath;
		SourceContent = sourceContent;
		Edits = edits;
		Files = Array.AsReadOnly(edits.Select(edit => edit.Path).ToArray());
	}

	/// <summary>The native files that will be written if the user applies the preview.</summary>
	public IReadOnlyList<string> Files { get; }
	internal string SourcePath { get; }
	internal string SourceContent { get; }
	internal McpNativeFileEdit[] Edits { get; }
}

internal sealed record McpNativeFileEdit(string Path, string? Expected, string Updated);

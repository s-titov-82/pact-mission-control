using Pact.Core.Agents;

namespace Pact.Infrastructure.AgentControl;

/// <summary>An installed definition opened for editing against its original file revision.</summary>
public sealed class McpNativeDefinitionDraft
{
	internal McpNativeDefinitionDraft(AgentKind kind, NativeMcpServer source, string original, string path, string content)
	{
		Kind = kind;
		Source = source;
		Original = original;
		Path = path;
		Content = content;
	}

	/// <summary>The selected native server and the scope that declares it.</summary>
	public NativeMcpServer Source { get; }
	/// <summary>The native declaration, which may include credentials.</summary>
	public string Content { get; }
	internal AgentKind Kind { get; }
	internal string Original { get; }
	internal string Path { get; }
}

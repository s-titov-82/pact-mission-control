namespace Pact.Infrastructure.Scenarios;

/// <summary>
/// The <c>.pact-reviews</c> directory older Pact versions exchanged review files in. Reviews now
/// pass through MCP; the directory is only removed when a project opens.
/// </summary>
public static class ReviewExchangeDirectory
{
	/// <summary>Directory name older Pact versions reserved for review exchange files.</summary>
	public const string RootName = ".pact-reviews";

	/// <summary>Removes the exchange directory an older Pact left behind.</summary>
	public static void CleanupAbandoned(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var root = Path.Combine(Path.GetFullPath(projectRoot), RootName);
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}
}

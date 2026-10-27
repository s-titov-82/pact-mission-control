using System.Text;

namespace Pact.Infrastructure.Storage;

/// <summary>The <c>.pact</c> folder Pact keeps inside a project for its own files.</summary>
public static class PactFolder
{
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	/// <summary>Creates <c>.pact</c> with a <c>.gitignore</c> of <c>*</c> when it is missing, so the folder stays out of Git.</summary>
	public static void EnsureGitIgnored(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var pactDirectory = Path.Combine(projectRoot, ".pact");
		Directory.CreateDirectory(pactDirectory);
		var gitignore = Path.Combine(pactDirectory, ".gitignore");
		if (!File.Exists(gitignore))
		{
			File.WriteAllText(gitignore, "*\n", Utf8);
		}
	}
}

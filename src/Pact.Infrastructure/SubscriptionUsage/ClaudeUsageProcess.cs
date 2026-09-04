using System.Diagnostics;

namespace Pact.Infrastructure.SubscriptionUsage;

internal sealed class ClaudeUsageProcess : IClaudeUsageProcess
{
	private readonly Process _process;

	public ClaudeUsageProcess(Process process)
	{
		_process = process;
	}

	public TextReader StandardOutput => _process.StandardOutput;
	public TextReader StandardError => _process.StandardError;
	public int ExitCode => _process.ExitCode;
	public bool HasExited => _process.HasExited;

	public Task WaitForExitAsync(CancellationToken cancellationToken) =>
		_process.WaitForExitAsync(cancellationToken);

	public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

	public void Dispose() => _process.Dispose();
}

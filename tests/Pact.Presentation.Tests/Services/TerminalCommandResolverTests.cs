using System.Runtime.InteropServices;
using System.Diagnostics;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed partial class TerminalCommandResolverTests
{
	[Test]
	public async Task ResolveCommandLineAsync_resolves_shell_to_quoted_full_path()
	{
		TerminalCommandResolver resolver = new(new FakeExecutableLocator(
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["pwsh"] = @"C:\Program Files\PowerShell\7\pwsh.exe"
			}));

		var commandLine = await resolver.ResolveCommandLineAsync("pwsh");

		commandLine.ShouldBe(@"""C:\Program Files\PowerShell\7\pwsh.exe""");
	}

	[Test]
	public async Task ResolveCommandLineAsync_preserves_shell_arguments()
	{
		TerminalCommandResolver resolver = new(new FakeExecutableLocator(
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["pwsh"] = @"C:\Program Files\PowerShell\7\pwsh.exe"
			}));

		var commandLine = await resolver.ResolveCommandLineAsync("pwsh -NoProfile");

		commandLine.ShouldBe(@"""C:\Program Files\PowerShell\7\pwsh.exe"" -NoProfile");
	}

	[Test]
	public async Task ResolveCommandLineAsync_wraps_non_shell_path_command_in_profile_shell()
	{
		TerminalCommandResolver resolver = new(new FakeExecutableLocator(
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

		var commandLine = await resolver.ResolveCommandLineAsync("codex --model gpt-5");

		commandLine.ShouldBe(@"pwsh -NoLogo -Command ""codex --model gpt-5""");
	}

	[Test]
	public async Task Appended_arguments_reach_a_bare_powershell_command_literally()
	{
		const string expected = """C:\Pact Root\$cash`tick's\PactMcpSkill.md\""";
		TerminalCommandResolver resolver = new(new FakeExecutableLocator(
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

		string? commandLine = await resolver.ResolveCommandLineAsync(
			"Write-Output",
			[expected]);

		string result = await RunAndCaptureStandardOutputAsync(commandLine!);

		result.TrimEnd().ShouldBe(expected);
	}

	[Test]
	public async Task Appended_arguments_reach_a_path_qualified_command_as_win32_argv()
	{
		const string expected = """developer_instructions=C:\Pact Root\$cash`tick's.md""";
		TerminalCommandResolver resolver = new(new FakeExecutableLocator(
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

		string? commandLine = await resolver.ResolveCommandLineAsync(
			@"""C:\Program Files\Codex\codex.exe"" --model gpt-5",
			["-c", expected]);

		string[] argv = SplitCommandLine(commandLine!);
		argv[^2].ShouldBe("-c");
		argv[^1].ShouldBe(expected);
	}

	[Test]
	[TestCase(@"C:\Profiles\developer\AppData\Roaming\npm\codex.ps1", @"pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""C:\Profiles\developer\AppData\Roaming\npm\codex.ps1""")]
	[TestCase(@"C:\Profiles\developer\AppData\Roaming\npm\codex.cmd", @"cmd.exe /d /s /c """"C:\Profiles\developer\AppData\Roaming\npm\codex.cmd""""")]
	[TestCase(@"C:\Profiles\developer\.local\bin\claude.exe", @"""C:\Profiles\developer\.local\bin\claude.exe""")]
	public void BuildLaunchCommand_wraps_windows_script_shims(string resolvedPath, string expectedCommandLine) => TerminalCommandResolver.BuildLaunchCommand(resolvedPath).ShouldBe(expectedCommandLine);

	[Test]
	[TestCase(@"C:\Profiles\developer\AppData\Roaming\npm\codex.cmd", "--model gpt-5", @"cmd.exe /d /s /c """"C:\Profiles\developer\AppData\Roaming\npm\codex.cmd"" --model gpt-5""")]
	[TestCase(@"C:\Profiles\developer\AppData\Roaming\npm\codex.ps1", "--model gpt-5", @"pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""C:\Profiles\developer\AppData\Roaming\npm\codex.ps1"" --model gpt-5")]
	[TestCase(@"C:\Profiles\developer\.local\bin\claude.exe", "--resume abc", @"""C:\Profiles\developer\.local\bin\claude.exe"" --resume abc")]
	public void BuildLaunchCommand_preserves_arguments(string resolvedPath, string arguments, string expectedCommandLine) => TerminalCommandResolver.BuildLaunchCommand(resolvedPath, arguments).ShouldBe(expectedCommandLine);

	[Test]
	public void BuildPowerShellProfileCommand_loads_profile_for_shell_commands()
	{
		var commandLine = TerminalCommandResolver.BuildPowerShellProfileCommand("claude --resume");

		commandLine.ShouldBe(@"pwsh -NoLogo -Command ""claude --resume""");
	}

	[Test]
	public void BuildPowerShellProfileCommand_keeps_quoted_arguments_intact_for_the_agent()
	{
		var commandLine = TerminalCommandResolver.BuildPowerShellProfileCommand(
			@"claude --mcp-config ""C:\Data\Pact\pact-mcp.json""");

		commandLine.ShouldBe(
			@"pwsh -NoLogo -Command ""claude --mcp-config \""C:\Data\Pact\pact-mcp.json\""""");
		SplitCommandLine(commandLine)[^1]
			.ShouldBe(@"claude --mcp-config ""C:\Data\Pact\pact-mcp.json""");
	}

	[Test]
	public void BuildPowerShellProfileCommand_keeps_a_trailing_directory_separator_literal()
	{
		var commandLine = TerminalCommandResolver.BuildPowerShellProfileCommand(
			@"claude --add-dir ""C:\Data\Pact\""");

		SplitCommandLine(commandLine)[^1]
			.ShouldBe(@"claude --add-dir ""C:\Data\Pact\""");
	}

	/// <summary>Splits a command line the way the operating system splits it for a new process.</summary>
	private static string[] SplitCommandLine(string commandLine)
	{
		var argv = CommandLineToArgvW(commandLine, out var count);
		if (argv == IntPtr.Zero)
		{
			throw new InvalidOperationException("The command line could not be split.");
		}

		try
		{
			var arguments = new string[count];
			for (var index = 0; index < count; index++)
			{
				var pointer = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
				arguments[index] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
			}

			return arguments;
		}
		finally
		{
			LocalFree(argv);
		}
	}

	private static async Task<string> RunAndCaptureStandardOutputAsync(string commandLine)
	{
		string[] argv = SplitCommandLine(commandLine);
		ProcessStartInfo startInfo = new()
		{
			FileName = argv[0],
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (string argument in argv[1..])
		{
			startInfo.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException("PowerShell did not start.");
		string output = await process.StandardOutput.ReadToEndAsync();
		string error = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
		process.ExitCode.ShouldBe(0, error);
		return output;
	}

	[LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16)]
	private static partial IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

	[LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
	private static partial IntPtr LocalFree(IntPtr memory);

	private sealed class FakeExecutableLocator(IReadOnlyDictionary<string, string> paths)
		: Core.Platform.IExecutableLocator
	{
		public string? FindOnPath(string executableName) => paths.GetValueOrDefault(executableName);
	}
}

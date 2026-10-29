using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class AgentFileLocationTests
{
	private const string Temp = @"C:\Users\u\AppData\Local\Temp\";

	[TestCase(@"C:\Users\u\AppData\Local\Temp\claude\p\s1\scratchpad\run.ps1", @"D:\proj", "scratchpad")]
	[TestCase(@"D:\proj\docs\plan.md", @"D:\proj", "project")]
	[TestCase(@"D:\project2\a.md", @"D:\proj", "project2")]
	[TestCase(@"C:\Users\u\AppData\Local\Temp\out.txt", @"D:\proj", "Temp")]
	[TestCase(@"C:\tools\bin\x.cs", null, "bin")]
	[TestCase(@"C:\x.txt", null, @"C:\")]
	public void Describe_names_where_the_file_lies(string path, string? projectRoot, string expected) =>
		AgentFileLocation.Describe(path, projectRoot, [Temp]).ShouldBe(expected);
}

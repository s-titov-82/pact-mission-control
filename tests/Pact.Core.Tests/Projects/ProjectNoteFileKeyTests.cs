using Pact.Core.Projects;

namespace Pact.Core.Tests.Projects;

public sealed class ProjectNoteFileKeyTests
{
	[Test]
	public void FromRootPath_IsStableAcrossCaseAndTrailingSeparator()
	{
		var a = ProjectNoteFileKey.FromRootPath(@"D:\Personal\Pact");
		var b = ProjectNoteFileKey.FromRootPath(@"d:\personal\pact\");
		b.ShouldBe(a);
	}

	[Test]
	public void FromRootPath_DiffersForDifferentPaths()
	{
		var a = ProjectNoteFileKey.FromRootPath(@"D:\Personal\Pact");
		var b = ProjectNoteFileKey.FromRootPath(@"D:\Personal\OtherProject");
		b.ShouldNotBe(a);
	}

	[Test]
	public void FromRootPath_EndsWithSanitizedReadableLeafName()
	{
		var key = ProjectNoteFileKey.FromRootPath(@"D:\Personal\Agent Terminal!");
		key.EndsWith("-agent-terminal", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public void FromRootPath_ContainsOnlyFileNameSafeCharacters()
	{
		var key = ProjectNoteFileKey.FromRootPath(@"C:\проекты\мой проект");
		key.ShouldAllBe(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-',
			$"unexpected character in '{key}'");
		string.IsNullOrWhiteSpace(key).ShouldBeFalse();
	}

	[Test]
	public void FromRootPath_Throws_OnNullOrWhitespace() => Should.Throw<ArgumentException>(() => ProjectNoteFileKey.FromRootPath(" "));
}
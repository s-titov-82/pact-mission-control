using Pact.Core.Remarks;
using Pact.Infrastructure.Remarks;
using Pact.Presentation.Services.Remarks;

namespace Pact.Presentation.Tests.Services.Remarks;

public sealed class RemarkPlacerTests : IDisposable
{
	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();

	public void Dispose() => _root.Dispose();

	[Test]
	public async Task A_new_side_remark_follows_its_line_in_the_current_file()
	{
		using var book = Book();
		var thread = await AddNewAsync(book);
		await WriteFileAsync("x\na\nb\nc\n");

		(await RemarkPlacer.PlaceOnDiskAsync(book, thread, "abc", CancellationToken.None))
			.ShouldBe(new RemarkPlacement(3, IsOutdated: false));
	}

	[Test]
	public async Task A_new_side_remark_follows_its_line_in_given_text()
	{
		using var book = Book();
		var thread = await AddNewAsync(book);

		(await RemarkPlacer.PlaceInTextAsync(book, thread, ["x", "y", "a", "b", "c"], "abc", CancellationToken.None))
			.ShouldBe(new RemarkPlacement(4, IsOutdated: false));
		(await RemarkPlacer.PlaceInTextAsync(book, thread, null, "abc", CancellationToken.None))
			.ShouldBe(new RemarkPlacement(null, IsOutdated: true));
	}

	[Test]
	public async Task A_deleted_file_outdates_a_new_side_remark()
	{
		using var book = Book();
		var thread = await AddNewAsync(book);
		File.Delete(Path.Combine(_root.Path, "src", "X.cs"));

		(await RemarkPlacer.PlaceOnDiskAsync(book, thread, "abc", CancellationToken.None))
			.ShouldBe(new RemarkPlacement(null, IsOutdated: true));
	}

	[Test]
	public async Task An_old_side_remark_outdates_when_head_moves()
	{
		using var book = Book();
		var thread = await book.AddAsync(
			new RemarkAnchor("src/X.cs", RemarkSide.Old, 7, "gone();", [], [], "abc", null),
			null,
			"keep it",
			CancellationToken.None);

		(await RemarkPlacer.PlaceOnDiskAsync(book, thread, "abc", CancellationToken.None))
			.ShouldBe(new RemarkPlacement(7, IsOutdated: false));
		(await RemarkPlacer.PlaceOnDiskAsync(book, thread, "def", CancellationToken.None))
			.ShouldBe(new RemarkPlacement(null, IsOutdated: true));
	}

	private RemarkBook Book() => new(_root.Path, new RemarkFileStore(_root.Path), TimeProvider.System);

	private async Task<RemarkThread> AddNewAsync(RemarkBook book)
	{
		await WriteFileAsync("a\nb\nc\n");
		return await book.AddAsync(
			new RemarkAnchor("src/X.cs", RemarkSide.New, 2, "b", ["a"], ["c"], "abc", null),
			"a\nb\nc\n",
			"why?",
			CancellationToken.None);
	}

	private async Task WriteFileAsync(string text)
	{
		Directory.CreateDirectory(Path.Combine(_root.Path, "src"));
		await File.WriteAllTextAsync(Path.Combine(_root.Path, "src", "X.cs"), text);
	}
}

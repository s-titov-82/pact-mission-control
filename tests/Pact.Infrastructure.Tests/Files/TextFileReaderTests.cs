using System.Text;
using Pact.Infrastructure.Files;

namespace Pact.Infrastructure.Tests.Files;

public sealed class TextFileReaderTests
{
	[Test]
	public async Task Reads_text_and_drops_the_bom()
	{
		using var folder = TemporaryDirectory.Create();
		var path = Path.Combine(folder.Path, "a.txt");
		await File.WriteAllTextAsync(path, "привет\nworld", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

		var content = await TextFileReader.ReadAsync(path, CancellationToken.None);

		content.ShouldBe(new TextFileContent(TextFileState.Text, "привет\nworld"));
	}

	[Test]
	public async Task A_missing_file_or_a_folder_is_missing()
	{
		using var folder = TemporaryDirectory.Create();

		(await TextFileReader.ReadAsync(Path.Combine(folder.Path, "none.txt"), CancellationToken.None))
			.ShouldBe(new TextFileContent(TextFileState.Missing, null));
		(await TextFileReader.ReadAsync(folder.Path, CancellationToken.None))
			.ShouldBe(new TextFileContent(TextFileState.Missing, null));
	}

	[Test]
	public async Task A_file_with_a_zero_byte_is_binary()
	{
		using var folder = TemporaryDirectory.Create();
		var path = Path.Combine(folder.Path, "a.bin");
		await File.WriteAllBytesAsync(path, [0x41, 0x00, 0x42]);

		(await TextFileReader.ReadAsync(path, CancellationToken.None))
			.ShouldBe(new TextFileContent(TextFileState.Binary, null));
	}

	[Test]
	public async Task A_file_over_two_megabytes_is_too_large()
	{
		using var folder = TemporaryDirectory.Create();
		var path = Path.Combine(folder.Path, "big.log");
		await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)'a', (int)TextFileReader.MaxBytes + 1).ToArray());

		(await TextFileReader.ReadAsync(path, CancellationToken.None))
			.ShouldBe(new TextFileContent(TextFileState.TooLarge, null));
	}
}

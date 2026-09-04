using System.Text.Json;
using Pact.App.Avalonia.Diagnostics;

namespace Pact.App.Avalonia.Tests.Lifecycle;

public sealed class PreviewFailureIsolationTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;

	[Test]
	public async Task CleanupContinuesAfterFailedFlushAndReturnsAggregate()
	{
		List<string> events = [];

		var error = await Should.ThrowAsync<AggregateException>(() =>
			AppShutdownSequence.RunAsync(
				() => { events.Add("flush"); throw new IOException("flush failed"); },
				() => { events.Add("stop"); return Task.CompletedTask; },
				() => { events.Add("dispose"); return Task.CompletedTask; }));

		events.ShouldBe(["flush", "stop", "dispose"]);
		error.InnerExceptions.ShouldContain(exception => exception is IOException);
	}

	[Test]
	public async Task AppLogWritesOneJsonLineAndAllowsConcurrentReaders()
	{
		await AppLog.AppendAsync(_root, "startup", new InvalidOperationException("broken"));
		var path = Directory.GetFiles(Path.Combine(_root, "Logs"), "pact-*.log").ShouldHaveSingleItem();
		await using FileStream reader = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using StreamReader textReader = new(reader);
		var line = (await textReader.ReadLineAsync(TestContext.CurrentContext.CancellationToken))!;
		using var document = JsonDocument.Parse(line);

		document.RootElement.GetProperty("phase").GetString().ShouldBe("startup");
		document.RootElement.GetProperty("exceptionType").GetString().ShouldBe(typeof(InvalidOperationException).FullName);
		document.RootElement.GetProperty("message").GetString().ShouldBe("broken");
	}
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}

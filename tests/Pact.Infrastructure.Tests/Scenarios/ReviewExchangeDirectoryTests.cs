namespace Pact.Infrastructure.Tests.Scenarios;

public sealed class ReviewExchangeDirectoryTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _projectRoot => _temporaryDirectory.Path;

	[Test]
	public void CleanupAbandoned_removes_owned_root_but_never_touches_generic_reviews_directory()
	{
		Directory.CreateDirectory(Path.Combine(_projectRoot, ".reviews", "owned-by-someone-else"));
		Directory.CreateDirectory(Path.Combine(_projectRoot, ".pact-reviews", "aaaaaaaa"));

		ReviewExchangeDirectory.CleanupAbandoned(_projectRoot);

		Directory.Exists(Path.Combine(_projectRoot, ".pact-reviews")).ShouldBeFalse();
		Directory.Exists(Path.Combine(_projectRoot, ".reviews")).ShouldBeTrue();
	}

	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}

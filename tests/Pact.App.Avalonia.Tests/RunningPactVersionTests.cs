using System.Reflection;
using System.Reflection.Emit;
using Pact.Core.Updates;

namespace Pact.App.Avalonia.Tests;

public sealed class RunningPactVersionTests
{
	[Test]
	public void Read_uses_only_the_informational_version_and_strips_build_metadata()
	{
		var assembly = CreateAssembly(
			informationalVersion: "1.2.3+sha.abcdef",
			assemblyVersion: new Version(9, 8, 7, 6),
			fileVersion: "8.7.6.5");

		RunningPactVersion.Read(assembly)
			.ShouldBe(new StableReleaseVersion(1, 2, 3));
	}

	[TestCase(null)]
	[TestCase("1.2.3-preview.1")]
	[TestCase("not-a-version")]
	public void Read_rejects_missing_or_nonstable_informational_versions(
		string? informationalVersion)
	{
		var assembly = CreateAssembly(
			informationalVersion,
			new Version(9, 8, 7, 6),
			"8.7.6.5");

		Should.Throw<InvalidOperationException>(() => RunningPactVersion.Read(assembly));
	}

	private static AssemblyBuilder CreateAssembly(
		string? informationalVersion,
		Version assemblyVersion,
		string fileVersion)
	{
		AssemblyName name = new($"PactVersionProbe.{Guid.NewGuid():N}")
		{
			Version = assemblyVersion
		};
		var assembly = AssemblyBuilder.DefineDynamicAssembly(
			name,
			AssemblyBuilderAccess.Run);
		if (informationalVersion is not null)
		{
			assembly.SetCustomAttribute(new CustomAttributeBuilder(
				typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
				[informationalVersion]));
		}

		assembly.SetCustomAttribute(new CustomAttributeBuilder(
			typeof(AssemblyFileVersionAttribute).GetConstructor([typeof(string)])!,
			[fileVersion]));
		return assembly;
	}
}

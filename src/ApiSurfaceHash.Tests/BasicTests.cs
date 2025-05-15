using System.Runtime.CompilerServices;
using NUnit.Framework;

//[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Foo")]

namespace ApiSurfaceHash.Tests;

[TestFixture]
public class BasicTests
{
  private readonly RoslynCompiler myCompiler = new();

  [Test]
  public void Test01()
  {
    AssertEqual(
      """
      namespace ApiSurfaceHash.Tests;
      class C;
      """,
      """
      namespace ApiSurfaceHash.Tests;
      class C { void M() { } }
      """);
  }

  private void AssertEqual(params IReadOnlyList<string> sourceCodes)
  {
    if (sourceCodes.Count == 0)
      throw new ArgumentOutOfRangeException(nameof(sourceCodes));

    ulong? expectedHash = null;

    foreach (var sourceCode in sourceCodes)
    {
      var peBytes = myCompiler.Compile(sourceCode);
      var currentHash = ApiSurfaceHasher.Execute(peBytes);

      if (expectedHash is null)
      {
        expectedHash = currentHash;
      }
      else
      {
        Assert.That(expectedHash, Is.EqualTo(currentHash));
      }
    }
  }
}
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
    // name chane
    AssertEqualSurface("class A;", "class A;");
    AssertEqualSurface("public class A;", "public class A;");
    AssertNotEqualSurface("public class A;", "public class B;");
    AssertNotEqualSurface("namespace X { public class A; }", "namespace Y { public class A; }");

    // added member
    AssertEqualSurface(
      "namespace ApiSurfaceHash.Tests { public class C; }",
      """
      namespace ApiSurfaceHash.Tests;
      public class C { void M() { } }
      """);
  }

  private void AssertEqualSurface(params IReadOnlyList<string> sourceCodes)
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

  private void AssertNotEqualSurface(string sourceCode1, string sourceCode2)
  {
    var peBytes1 = myCompiler.Compile(sourceCode1);
    var peBytes2 = myCompiler.Compile(sourceCode2);

    var currentHash1 = ApiSurfaceHasher.Execute(peBytes1);
    var currentHash2 = ApiSurfaceHasher.Execute(peBytes2);

    Assert.That(currentHash1, Is.Not.EqualTo(currentHash2));
  }
}
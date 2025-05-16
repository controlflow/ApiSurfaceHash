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
    // name change
    AssertSurfaceEqual("class A;", "class A;");
    AssertSurfaceEqual("public class A;", "public class A;");
    AssertSurfaceNotEqual("public class A;", "public class B;");
    AssertSurfaceNotEqual("namespace X { public class A; }", "namespace Y { public class A; }");

    // order in metadata
    AssertSurfaceEqual(
      "public class A; public class B;",
      "public class B; public class A;");

    // added member
    AssertSurfaceEqual(
      "namespace ApiSurfaceHash.Tests { public class C; }",
      """
      namespace ApiSurfaceHash.Tests;
      public class C { void M() { } }
      """);
  }

  [Test]
  public void TestMethodSignatures()
  {
    AssertMemberSurfaceEqual("public void M() { _ = 1; }", "public void M() { _ = 2; }");
    AssertMemberSurfaceEqual("public void M() { } public void F() { }", "public void F() { } public void M() { }");

    // types change
    AssertMemberSurfaceNotEqual("public void M() { }", "public void M(int x) { }");
    AssertMemberSurfaceNotEqual("public void M(sbyte x) { }", "public void M(char x) { }");
    AssertMemberSurfaceNotEqual("public void M(byte x) { }", "public void M(bool x) { }");
    AssertMemberSurfaceNotEqual("public void M(ushort x) { }", "public void M(short x) { }");
    AssertMemberSurfaceNotEqual("public void M(uint x) { }", "public void M(int x) { }");
    AssertMemberSurfaceNotEqual("public void M(ulong x) { }", "public void M(long x) { }");
    AssertMemberSurfaceNotEqual("public void M(float x) { }", "public void M(double x) { }");
    AssertMemberSurfaceNotEqual("public void M(nuint x) { }", "public void M(nint x) { }");
    AssertMemberSurfaceNotEqual("public void M(object x) { }", "public void M(string x) { }");
    AssertMemberSurfaceNotEqual("public void M(float x) { }", "public void M(decimal x) { }"); // type ref
    AssertMemberSurfaceNotEqual("public void M(object x) { }", "public void M(C x) { }"); // type def
    AssertMemberSurfaceNotEqual("public void M(int? x) { }", "public void M(bool? x) { }"); // type spec
    AssertMemberSurfaceEqual("private void M(int x) { }", "private void M(bool x) { }");

    // unmanaged types
    AssertMemberSurfaceNotEqual("public unsafe void M(int x) { }", "public unsafe void M(int* x) { }");
    AssertMemberSurfaceNotEqual("public unsafe void M(int* x) { }", "public unsafe void M(int** x) { }");
    AssertMemberSurfaceNotEqual("public unsafe void M(int* x) { }", "public unsafe void M(ref int x) { }");
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(delegate*<int> ptr) { }",
      "public unsafe void M(delegate*<uint> ptr) { }");
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(delegate*<bool,int> ptr) { }",
      "public unsafe void M(delegate*<uint,int> ptr) { }");
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(delegate*<uint,int> ptr) { }",
      "public unsafe void M(delegate*<uint,uint,int> ptr) { }");
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(delegate*<int> ptr) { }",
      "public unsafe void M(delegate* unmanaged<int> ptr) { }");
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(delegate* unmanaged<int> ptr) { }",
      "public unsafe void M(delegate* unmanaged[Cdecl]<int> ptr) { }");
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(delegate* managed<int> ptr) { }",
      "public unsafe void M(delegate* unmanaged<int> ptr) { }");

    // parameter changes
    AssertMemberSurfaceNotEqual("public void M(int x) { }", "public void M(int y) { }"); // name change
    AssertMemberSurfaceNotEqual("public void M(int x) { }", "public void M(ref int x) { }");
    AssertMemberSurfaceNotEqual("public void M(ref int x) { }", "public void M(out int x) { x = 0; }"); // ref-out
    AssertMemberSurfaceNotEqual("public void M(ref int x) { }", "public void M(in int x) { }"); // ref-in
    AssertMemberSurfaceNotEqual("public void M(ref int x) { }", "public void M(ref readonly int x) { }"); // ref-ref readonly
    AssertMemberSurfaceNotEqual("public void M(in int x) { }", "public void M(ref readonly int x) { }"); // in-ref readonly (attr change)
    AssertMemberSurfaceNotEqual("public void M(int x) { }", "public void M(int x = 0) { }"); // optional or not
    AssertMemberSurfaceEqual("public void M(int x = 0) { }", "public void M(int x = 1 - 1) { }");
    AssertMemberSurfaceNotEqual("public void M(int x = 0) { }", "public void M(int x = 1) { }"); // optional value change
    AssertMemberSurfaceNotEqual("public void M(string s = null) { }", "public void M(string s = \"\") { }"); // optional value change
    AssertMemberSurfaceNotEqual( // pseudo attr
      "public void M(string s) { }",
      "public void M([System.Runtime.InteropServices.Optional] string s) { }");
    AssertMemberSurfaceNotEqual( // presence vs. absence of the default value
      "public void M(string s = null) { }",
      "public void M([System.Runtime.InteropServices.Optional] string s) { }");
    AssertMemberSurfaceNotEqual("public void M(int? x = null) { }", "public void M(int? x = new int()) { }");
    AssertMemberSurfaceEqual(
      "public void M(S x = default) { } public struct S;",
      "public void M(S x = new S()) { } public struct S;");
    AssertSurfaceNotEqual( // custom attrs mismatch
      "public static class E { public static void Extension(string s) { } }",
      "public static class E { public static void Extension(this string s) { } }");

    // return type change
    AssertMemberSurfaceNotEqual("public void M() { }", "public int M() { return 1; }");
    AssertMemberSurfaceNotEqual("public object M() => null;", "public object[] M() => null;");
    AssertMemberSurfaceNotEqual("public object[] M() => null;", "public object[,] M() => null;");
    AssertMemberSurfaceNotEqual("public object[,] M() => null;", "public object[,,] M() => null;");
    AssertMemberSurfaceNotEqual(
      "public int M(int x) => throw null!;",
      "public ref int M(ref int x) => throw null!;");
    AssertMemberSurfaceNotEqual(
      "public ref int M(int x) => throw null!;",
      "public ref readonly int M(ref int x) => throw null!;");
  }

  [Test]
  public void TestGenericTypes()
  {
    AssertMemberSurfaceEqual("public void M<T>(T x) { }", "public void M<T>(T x) { _ = 1; }");
    AssertMemberSurfaceNotEqual("public void M<T>(T x) { }", "public void M<T>(int x) { }");
    AssertSurfaceEqual(
      "public class C<T> { public void M(T x) { } }",
      "public class C<T> { public void M(T x) { _ = 1; } }");
    AssertSurfaceNotEqual(
      "public class C<T> { public void M(T x) { } }",
      "public class C { public void M<T>(T x) { } }");
    AssertSurfaceNotEqual(
      "public class C<T> { public class N<U> { public void M(T x) { } } }",
      "public class C<T> { public class N<U> { public void M(U x) { } } }");
    AssertMemberSurfaceNotEqual(
      "public void M<T>(T x) { }",
      "public void M<T>(T x) where T : class { }");

    AssertSurfaceEqual("public class C<T>;", "public class C<U>;"); // positional
    AssertMemberSurfaceEqual("public void M<T>(T x) { }", "public void M<U>(U x) { }");
    AssertSurfaceNotEqual(
      "public class A<T> : System.Attribute;",
      "public class A<[A<int>] T> : System.Attribute;");
    AssertMemberSurfaceNotEqual(
      "public void M<T>() { } public class A : System.Attribute;",
      "public void M<[A] T>() { } public class A : System.Attribute;");
  }

  private void AssertSurfaceEqual(params IReadOnlyList<string> sourceCodes)
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

  private void AssertSurfaceNotEqual(string sourceCode1, string sourceCode2)
  {
    var peBytes1 = myCompiler.Compile(sourceCode1);
    var peBytes2 = myCompiler.Compile(sourceCode2);

    var currentHash1 = ApiSurfaceHasher.Execute(peBytes1);
    var currentHash2 = ApiSurfaceHasher.Execute(peBytes2);

    Assert.That(currentHash1, Is.Not.EqualTo(currentHash2));
  }

  private void AssertMemberSurfaceEqual(string member1, string member2)
  {
    Assert.That(CheckMemberSurfaceEqual(member1, member2), Is.True);
  }

  private void AssertMemberSurfaceNotEqual(string member1, string member2)
  {
    Assert.That(CheckMemberSurfaceEqual(member1, member2), Is.False);
  }

  private bool CheckMemberSurfaceEqual(string member1, string member2)
  {
    const string template =
      """
      using System;
      public class C {{
        {0}
      }}
      """;

    var peBytes1 = myCompiler.Compile(string.Format(template, member1));
    var peBytes2 = myCompiler.Compile(string.Format(template, member2));

    var currentHash1 = ApiSurfaceHasher.Execute(peBytes1);
    var currentHash2 = ApiSurfaceHasher.Execute(peBytes2);
    return currentHash1 == currentHash2;
  }
}
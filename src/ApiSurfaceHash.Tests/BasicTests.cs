using System.Diagnostics.Contracts;
using NUnit.Framework;

//[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Foo")]

namespace ApiSurfaceHash.Tests;

[TestFixture]
public class BasicTests
{
  private readonly RoslynCompiler myCompiler = new();

  [Test]
  public void TestBasics()
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
      "namespace ApiSurfaceHash.Tests; public class C { void M() { } }");
  }

  [Test]
  public void TestTypeKinds()
  {
    AssertSurfaceNotEqual( // top-level types
      "public class C;",
      "public abstract class C;",
      "public sealed class C;",
      "public static class C;",
      "public struct C;",
      "public ref struct C;",
      "public readonly struct C;",
      "public readonly ref struct C;",
      "public record C;",
      "public record struct C;",
      "public interface C;");
    AssertMemberSurfaceNotEqual( // nested types
      "public class N;",
      "public abstract class N;",
      "public sealed class N;",
      "public static class N;",
      "public struct N;",
      "public ref struct N;",
      "public readonly struct N;",
      "public readonly ref struct N;",
      "public record N;",
      "public record struct N;",
      "public interface N;");
  }

  [Test]
  public void TestInternalVisibleTo()
  {
    // included in surface
    AssertSurfaceNotEqual(
      "public class C { public void Method() { } }",
      "public class C { public void MethodChanged() { } }");
    AssertSurfaceNotEqual(
      "public class C { public class N { public void Method() { } } }",
      "public class C { public class N { public void MethodChanged() { } } }",
      "public class C { protected class N { public void Method() { } } }",
      "public class C { protected class N { public void MethodChanged() { } } }",
      "public class C { protected internal class N { public void Method() { } } }",
      "public class C { protected internal class N { public void MethodChanged() { } } }");

    // internal is excluded by default
    AssertSurfaceEqual(
      "internal class C { public void Method() { } }",
      "internal class C { public void MethodChanged() { } }");
    AssertSurfaceEqual(
      "public class C { internal class N { public void Method() { } } }",
      "public class C { internal class N { public void MethodChanged() { } } }",
      "public class C { private protected class N { public void Method() { } } }",
      "public class C { private protected class N { public void MethodChanged() { } } }");
    AssertSurfaceEqual( // file-local types are internal
      "file class C { public void Method() { } }",
      "file class C { public void MethodChanged() { } }");

    // internals exposed
    AssertSurfaceNotEqual(
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      internal class C { public void Method() { } }
      """,
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      internal class C { public void MethodChanged() { } }
      """);
    AssertSurfaceNotEqual(
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      public class C { internal class N { public void Method() { } } }
      """,
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      public class C { internal class N { public void MethodChanged() { } } }
      """);
    AssertSurfaceNotEqual(
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      public class C { private protected class N { public void Method() { } } }
      """,
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      public class C { private protected class N { public void MethodChanged() { } } }
      """);

    // exceptions
    AssertSurfaceEqual( // <PrivateImplDetails> is not part of the surface
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      public class C {
        public int[] Method() => null;
      }
      """,
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      public class C {
        public int[] Method() => new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }; // stored in private impl details
      }
      """);

    AssertSurfaceEqual( // file-local types are not part of the surface
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      file class C { public void Method() { } }
      """,
      """
      [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]
      file class C { public void MethodChanged() { } }
      """);
  }

  [Test]
  public void TestMethodSignatures()
  {
    AssertMemberSurfaceEqual( // impl details
      "public void M() { _ = 1; }",
      "public void M() { _ = 2; }");
    AssertMemberSurfaceEqual( // member order
      "public void M() { } public void F() { }",
      "public void F() { } public void M() { }");

    // types change
    AssertMemberSurfaceNotEqual(
      "public void M() { }",
      "public void M1() { }",
      "public void M(int x) { }",
      "public void M(char x) { }",
      "public void M(sbyte x) { }",
      "public void M(byte x) { }",
      "public void M(bool x) { }",
      "public void M(ushort x) { }",
      "public void M(short x) { }",
      "public void M(uint x) { }",
      "public void M(ulong x) { }",
      "public void M(long x) { }",
      "public void M(float x) { }",
      "public void M(double x) { }",
      "public void M(nuint x) { }",
      "public void M(nint x) { }",
      "public void M(object x) { }",
      "public void M(string x) { }",
      "public void M(decimal x) { }", // type ref
      "public void M(C x) { }", // type def
      "public void M(int? x) { }", // type spec
      "public void M(bool? x) { }");
    AssertMemberSurfaceEqual(
      "private void M(int x) { }",
      "private void M(bool x) { }");

    // visibility
    AssertMemberSurfaceNotEqual(
      "public void M() { }",
      "protected internal void M() { }",
      "internal void M() { }",
      "protected void M() { }");
    AssertMemberSurfaceEqual(
      "internal void M() { }",
      "private protected void M() { }",
      "private void M() { }");

    // modifiers
    AssertSurfaceEqual(
      "public abstract class C { public void M() { } }",
      "public abstract class C { public abstract void M(); }",
      "public abstract class C { public virtual void M() { } }");

    // unmanaged types
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(int x) { }",
      "public unsafe void M(int* x) { }",
      "public unsafe void M(int** x) { }",
      "public unsafe void M(ref int x) { }");
    AssertMemberSurfaceNotEqual(
      "public unsafe void M(delegate*<int> ptr) { }",
      "public unsafe void M(delegate*<uint> ptr) { }",
      "public unsafe void M(delegate*<bool,int> ptr) { }",
      "public unsafe void M(delegate*<uint,int> ptr) { }",
      "public unsafe void M(delegate*<uint,uint,int> ptr) { }",
      "public unsafe void M(delegate* unmanaged<bool> ptr) { }",
      "public unsafe void M(delegate* unmanaged[Cdecl]<bool> ptr) { }",
      "public unsafe void M(delegate* managed<bool> ptr) { }");
    AssertMemberSurfaceEqual(
      "public unsafe void M(delegate*<int> ptr) { }",
      "public unsafe void M(delegate* managed<int> ptr) { }");

    // parameter changes
    AssertMemberSurfaceNotEqual(
      "public void M(int x) { }",
      "public void M(int y) { }", // name change
      "public void M(in int x) { }",
      "public void M(ref readonly int x) { }",
      "public void M(ref int x) { }",
      "public void M(out int x) { x = 0; }",
      "public void M(int x = 0) { }",
      "public void M(int x = 1) { }",
      "public void M(string s) { }",
      "public void M(string s = null) { }",
      "public void M(string s = \"\") { }",
      "public void M([System.Runtime.InteropServices.Optional] string s) { }", // pseudo attr
      "public void M(int? x = null) { }",
      "public void M(int? x = new int()) { }");
    AssertMemberSurfaceEqual(
      "public void M(int x = 0) { }",
      "public void M(int x = 1 - 1) { }");
    AssertMemberSurfaceEqual(
      "public void M(S x = default) { } public struct S;",
      "public void M(S x = new S()) { } public struct S;");
    AssertSurfaceNotEqual( // custom attrs mismatch
      "public static class E { public static void Extension(string s) { } }",
      "public static class E { public static void Extension(this string s) { } }");

    // return type change
    AssertMemberSurfaceNotEqual(
      "public void M() { }",
      "public int M() => 1;",
      "public object M() => null;",
      "public object[] M() => null;",
      "public object[,] M() => null;",
      "public object[,,] M() => null;");
    AssertMemberSurfaceNotEqual(
      "public int M(int x) => throw null!;",
      "public int M(ref int x) => throw null!;",
      "public ref int M(int x) => throw null!;",
      "public ref int M(ref int x) => throw null!;",
      "public ref readonly int M(ref int x) => throw null!;");
  }

  [Test]
  public void TestGenericTypes()
  {
    AssertMemberSurfaceEqual(
      "public void M<T>(T x) { }",
      "public void M<T>(T x) { _ = 1; }");
    AssertMemberSurfaceNotEqual(
      "public void M<T>(T x) { }",
      "public void M<T>(int x) { }");
    AssertSurfaceEqual(
      "public class C<T> { public void M(T x) { } }",
      "public class C<T> { public void M(T x) { _ = 1; } }");
    AssertSurfaceNotEqual(
      "public class C<T> { public void M(T x) { } }",
      "public class C { public void M<T>(T x) { } }");
    AssertSurfaceNotEqual(
      "public class C<T> { public class N<U> { public void M(T x) { } } }",
      "public class C<T> { public class N<U> { public void M(U x) { } } }");
    AssertSurfaceNotEqual(
      "public class C<T> { }",
      "public class C<T> where T : class { }",
      "public class C<T> where T : struct { }",
      "public class C<T> where T : unmanaged { }",
      "public class C<T> where T : new() { }",
      "public class C<T> where T : notnull { }",
      "public class C<T> where T : System.Enum { }",
      "public class C<T> where T : System.Delegate { }",
      "public interface C;",
      "public interface C<T>;",
      "public interface C<in T>;",
      "public interface C<out T>;");

    AssertSurfaceNotEqual(
      "public class C;",
      "public class C<T>;",
      "public class C<T1, T2>;",
      "public class C<T1, T2, T3>;",
      "public interface I;",
      "public interface I<T>;",
      "public interface I<T1, T2>;",
      "public interface I<T1, T2, T3>;");
    AssertSurfaceEqual("public class C<T>;", "public class C<U>;"); // positional
    AssertSurfaceEqual("public class C<T, U>;", "public class C<U, T>;");
    AssertMemberSurfaceEqual("public void M<T>(T x) { }", "public void M<U>(U x) { }");
    AssertSurfaceNotEqual( // attrs on type parameters
      "public class A<T> : System.Attribute;",
      "public class A<[A<int>] T> : System.Attribute;");
    AssertMemberSurfaceNotEqual(
      "public void M<T>() { } public class A : System.Attribute;",
      "public void M<[A] T>() { } public class A : System.Attribute;");

    AssertMemberSurfaceNotEqual(
      "public void M<T>() { }",
      "public void M<T>() where T : class { }",
      "public void M<T>() where T : struct { }",
      "public void M<T>() where T : unmanaged { }",
      "public void M<T>() where T : new() { }",
      "public void M<T>() where T : notnull { }",
      "public void M<T>() where T : System.Enum { }",
      "public void M<T>() where T : System.Delegate { }");
    AssertMemberSurfaceNotEqual(
      """
      #nullable disable
      public void M<T>() { }
      """,
      """
      #nullable disable
      public void M<T>() where T : notnull { }
      """);

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

  private void AssertSurfaceNotEqual(params IReadOnlyList<string> sourceCodes)
  {
    var hashes = new Dictionary<ulong, string>();

    foreach (var sourceCode in sourceCodes)
    {
      var peBytes = myCompiler.Compile(sourceCode);
      var surfaceHash = ApiSurfaceHasher.Execute(peBytes);

      if (hashes.TryGetValue(surfaceHash, out var previousCode))
      {
        Assert.Fail(
          $"""
           Hashes are not supposed to be equal:

             {previousCode}

           and

             {sourceCode}

           {(sourceCode == previousCode ? "Text is equal!" : "")}
           """);
      }
      else
      {
        hashes[surfaceHash] = sourceCode;
      }
    }
  }

  private void AssertMemberSurfaceEqual(params IReadOnlyList<string> memberSources)
  {
    if (memberSources.Count == 0)
      throw new ArgumentOutOfRangeException(nameof(memberSources));

    ulong? expectedHash = null;

    foreach (var sourceCode in memberSources)
    {
      var currentHash = GetMemberSurfaceHash(sourceCode);

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

  private void AssertMemberSurfaceNotEqual(params IEnumerable<string> members)
  {
    var hashes = new Dictionary<ulong, string>();

    foreach (var member in members)
    {
      var memberSurfaceHash = GetMemberSurfaceHash(member);

      if (hashes.TryGetValue(memberSurfaceHash, out var previousMember))
      {
        Assert.Fail(
          $"""
          Hashes are not supposed to be equal:

            {previousMember}

          and

            {member}
          
          {(member == previousMember ? "Text is equal!" : "")}
          """);
      }
      else
      {
        hashes[memberSurfaceHash] = member;
      }
    }
  }

  [Pure]
  private ulong GetMemberSurfaceHash(string member)
  {
    const string template =
      """
      using System;
      public class C {{
        {0}
      }}
      """;

    var peBytes = myCompiler.Compile(string.Format(template, member));
    return ApiSurfaceHasher.Execute(peBytes);
  }

  private bool CheckMemberSurfaceEqual(string member1, string member2)
  {
    var currentHash1 = GetMemberSurfaceHash(member1);
    var currentHash2 = GetMemberSurfaceHash(member2);
    return currentHash1 == currentHash2;
  }
}
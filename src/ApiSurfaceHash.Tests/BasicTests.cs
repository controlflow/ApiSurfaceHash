using System.Diagnostics.Contracts;
using NUnit.Framework;

// todo: generic attributes
// todo: generic type usages

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
      "public interface C;",
      "public enum C;",
      "public delegate void C();");
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
      "public interface N;",
      "public enum N;",
      "public delegate void N();");
  }

  [Test]
  public void TestInternalVisibleTo()
  {
    // included in surface
    AssertSurfaceNotEqual(
      "public class C { public int Field; }",
      "public class C { public int FieldChanged; }",
      "public class C { public void Method() { } }",
      "public class C { public void MethodChanged() { } }",
      "public class C { protected void Method() { } }",
      "public class C { protected void MethodChanged() { } }",
      "public class C { protected internal void Method() { } }",
      "public class C { protected internal void MethodChanged() { } }");
    AssertMemberSurfaceNotEqual(
      "public class N { public int Field; }",
      "public class N { public int FieldChanged; }",
      "public class N { public void Method() { } }",
      "public class N { public void MethodChanged() { } }",
      "protected class N { public int Field; }",
      "protected class N { public int FieldChanged; }",
      "protected class N { public void Method() { } }",
      "protected class N { public void MethodChanged() { } }",
      "protected internal class N { public int Field; }",
      "protected internal class N { public int FieldChanged; }",
      "protected internal class N { public void Method() { } }",
      "protected internal class N { public void MethodChanged() { } }");

    // internal is excluded by default
    AssertSurfaceEqual(
      "internal class C { public int Field; }",
      "internal class C { public int FieldChanged; }");
    AssertSurfaceEqual(
      "internal class C { public void Method() { } }",
      "internal class C { public void MethodChanged() { } }");
    AssertMemberSurfaceEqual(
      "internal int Field;",
      "internal int FieldChanged;",
      "private protected int Field;",
      "private protected int FieldChanged;",
      "internal void Method() { }",
      "internal void MethodChanged() { }",
      "private protected void Method() { }",
      "private protected void MethodChanged() { }");
    AssertMemberSurfaceEqual(
      "internal class N { public int Field; }",
      "internal class N { public int FieldChanged; }",
      "internal class N { public void Method() { } }",
      "internal class N { public void MethodChanged() { } }",
      "private protected class N { public int Field; }",
      "private protected class N { public int FieldChanged; }",
      "private protected class N { public void Method() { } }",
      "private protected class N { public void MethodChanged() { } }");
    AssertSurfaceEqual( // file-local types are internal
      "file class C { public void Method() { } public int Field; }",
      "file class C { public void MethodChanged() { } public int FieldChanged; }");

    // internals exposed
    const string ivt = """[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("a")]""";
    AssertSurfaceNotEqual(
      ivt + "internal class C { public int Field; }",
      ivt + "internal class C { public int FieldChanged; }",
      ivt + "internal class C { public void Method() { } }",
      ivt + "internal class C { public void MethodChanged() { } }");
    AssertSurfaceNotEqual(
      ivt + "public class C { internal class N { internal int Field; } }",
      ivt + "public class C { internal class N { internal int FieldChanged; } }",
      ivt + "public class C { internal class N { public void Method() { } } }",
      ivt + "public class C { internal class N { public void MethodChanged() { } } }",
      ivt + "public class C { internal int Field; }",
      ivt + "public class C { internal int FieldChanged; }",
      ivt + "public class C { internal void Method() { } }",
      ivt + "public class C { internal void MethodChanged() { } }");
    AssertSurfaceNotEqual(
      ivt + "public class C { private protected class N { public int Field; } }",
      ivt + "public class C { private protected class N { public int FieldChanged; } }",
      ivt + "public class C { private protected class N { public void Method() { } } }",
      ivt + "public class C { private protected class N { public void MethodChanged() { } } }",
      ivt + "public class C { private protected int Field; }",
      ivt + "public class C { private protected int FieldChanged; }",
      ivt + "public class C { private protected void Method() { } }",
      ivt + "public class C { private protected void MethodChanged() { } }");

    // exceptions
    AssertSurfaceEqual( // <PrivateImplDetails> is not part of the surface
      ivt + "public class C { public int[] Method() => null; }",
      ivt + "public class C { public int[] Method() => new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }; /* stored in private impl details */ }");
    AssertSurfaceEqual( // file-local types are not part of the surface
      ivt + "file class C { public void Method() { } }",
      ivt + "file class C { public void MethodChanged() { } }");

    // private is private
    AssertSurfaceEqual(
      ivt + "internal class C { private void Method() { } }",
      ivt + "internal class C { private void MethodChanged() { } }",
      ivt + "internal class C { private int Field; }",
      ivt + "internal class C { private int FieldChanged; }");
    AssertSurfaceEqual(
      ivt + "public class C { private class N { public void Method() { } public int Field; } }",
      ivt + "public class C { private class N { public void MethodChanged() { } public int FieldChanged; } }");
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
    AssertSurfaceNotEqual(
      "public abstract class C { public void M() { } }",
      "public abstract class C { public static void M() { } }",
      "public abstract class C { public abstract void M(); }",
      "public abstract class C { public virtual void M() { } }",
      "public class C { public string ToString() => \"a\"; }",
      "public class C { public override string ToString() => \"a\"; }",
      "public class C { public sealed override string ToString() => \"a\"; }");
    AssertMemberSurfaceNotEqual( // C# has warnings when 'await' is absent
      "public System.Threading.Tasks.Task M() => null;",
      "public async System.Threading.Tasks.Task M() { }");
    AssertMemberSurfaceEqual( // 'unsafe' is a C# thing
      "public void M() { }",
      "public unsafe void M() { int* ptr; }");
    AssertMemberSurfaceEqual( // 'extern' is a impl detail
      "public static void M() { }",
      """
      [System.Runtime.InteropServices.DllImport("aa")]
      public static extern void M();
      """);
    AssertSurfaceNotEqual( // 'readonly' modifier for struct members
      "public struct S { public void M() { } }",
      "public struct S { public readonly void M() { } }",
      "public readonly struct S { public void M() { } }");
    AssertMemberSurfaceEqual( // impl details
      "public void M() { }",
      """
      [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
      public void M() { }
      """);

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
    AssertMemberSurfaceNotEqual( // params
      "public void M(int[] xs) { }",
      "public void M(params int[] xs) { }",
      "public void M(System.Collections.Generic.IEnumerable<string> xs) { }",
      "public void M(params System.Collections.Generic.IEnumerable<string> xs) { }");

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
  public void TestFieldSignatures()
  {
    AssertMemberSurfaceEqual(
      "public int Field, A;",
      "public int A, Field;",
      "public int Field; public int A;",
      "public int A; public int Field = 1;");
    AssertMemberSurfaceNotEqual(
      "public int Field;",
      "public string Field;",
      "public object Field;",
      "public dynamic Field;",
      "public const int Field = 0;",
      "public const int Field = 1;",
      "public const string Field = null;",
      "public const string Field = \"\";",
      "public const string Field = \"abc\";",
      "public const object? Field = null;",
      "public required int Field;",
      "public readonly int Field;",
      "public volatile int Field;", // modreq
      "public static int Field;",
      "public static readonly int Field;",
      "[System.ThreadStatic] public static int Field;");
    AssertSurfaceNotEqual(
      "public ref struct S { public int RefField; }",
      "public ref struct S { public ref int RefField; }",
      "public ref struct S { public ref readonly int RefField; }",
      "public ref struct S { public readonly int RefField; }",
      "public ref struct S { public readonly ref int RefField; }",
      "public ref struct S { public readonly ref readonly int RefField; }",
      "public unsafe struct S { public fixed int Buf[1]; }",
      "public unsafe struct S { public fixed int Buf[14]; }");
    AssertSurfaceEqual(
      "public enum E { A, B, C }",
      "public enum E : int { A = 0, B = 1, C = 2 }");
    AssertSurfaceNotEqual(
      "public enum E { A, B, C }",
      "public enum E { D, E, F }",
      "public enum E : byte { A, B, C }",
      "public enum E { A = 0, B = 1, C = 3 }");
  }

  [Test]
  public void TestDelegates()
  {
    AssertSurfaceNotEqual(
      "public delegate void Action();",
      "public delegate void Action(int x);",
      "public delegate void Action<T>(T x);",
      "public delegate T Action<T>();");
  }

  [Test]
  public void TestCustomAttributes()
  {
    AssertSurfaceNotEqual(
      """
      public class A(int x) : System.Attribute;
      [A(1)] public class C;
      """,
      """
      public class A(int x) : System.Attribute;
      [A(2)] public class C;
      """);
    AssertSurfaceEqual(
      """
      internal class A(int x) : System.Attribute;
      [A(1)] public class C;
      """,
      """
      internal class A(int x) : System.Attribute;
      [A(2)] public class C;
      """);
  }

  [Test]
  public void TestBaseTypes()
  {
    AssertSurfaceNotEqual(
      "public enum S;",
      "public struct S;",
      "public class B; public class C;",
      "public class B; public class C : B;",
      "public interface I; public class C;",
      "public interface I; public class C : I;",
      "public interface I<T>; public class C : I<int>;",
      "public interface I<T>; public class C : I<string>;",
      "public interface I<T>; public class C : I<int>, I<string>;");

    AssertSurfaceEqual(
      "internal interface I; public class C;",
      "internal interface I; public class C : I;");
    AssertSurfaceEqual(
      "internal interface I<T>; public class C : I<int>;",
      "internal interface I<T>; public class C : I<bool>;",
      "internal interface I<T>; public class C : I<int>, I<string>;");
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
      "public class C;",
      "public class C<T>;",
      "public class C<T1, T2>;",
      "public class C<T> where T : class;",
      "public class C<T> where T : struct;",
      "public class C<T> where T : unmanaged;",
      "public class C<T> where T : new();",
      "public class C<T> where T : notnull;",
      "public class C<T> where T : System.Enum;",
      "public class C<T> where T : System.Delegate;",
      "public interface C;",
      "public interface C<T>;",
      "public interface C<in T>;",
      "public interface C<out T>;");
    AssertSurfaceEqual(
      """
      public class B;
      public interface I1;
      public interface I2;
      public class C<T> where T : B, I1, I2, new();
      public class L<T> : System.Collections.Generic.List<T[]>;
      """,
      """
      public interface I2;
      public interface I1;
      public class B;
      public class C<T> where T : B, I2, I1, new();
      public class L<T> : System.Collections.Generic.List<T[]>;
      """);
    AssertSurfaceNotEqual(
      """
      public class A : System.Attribute;
      public class C<T>;
      """,
      """
      public class A : System.Attribute;
      public class C<[A] T>;
      """);
    AssertSurfaceNotEqual(
      "public class C<T> where T : System.Collections.Generic.List<int>;",
      "public class C<T> where T : System.Collections.Generic.List<uint>;",
      "public class C<T> where T : System.Collections.Generic.List<T>;",
      "public class C<T> where T : System.Collections.Generic.List<string>;",
      "public class C<T> where T : System.Collections.Generic.List<object[]>;",
      "public unsafe class C<T> where T : System.Collections.Generic.List<int*[]>;",
      "public class C<T> where T : System.Collections.Generic.List<object>;",
      "public interface C<T> where T : System.Collections.Generic.IList<int>;",
      "public interface C<T> where T : System.Collections.Generic.IList<uint>;",
      "public interface C<T> where T : System.Collections.Generic.IList<T>;");

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
    AssertMemberSurfaceEqual(
      """
      public class B;
      public interface I1;
      public interface I2;
      public void M<T>() where T : B, I1, I2, new() { }
      """,
      """
      public interface I2;
      public interface I1;
      public class B;
      public void M<T>() where T : B, I2, I1, new() { }
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
}
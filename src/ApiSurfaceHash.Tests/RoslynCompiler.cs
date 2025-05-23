using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ApiSurfaceHash.Tests;

public sealed class RoslynCompiler(string name)
{
  public bool EnableOptimizations { get; init; } = true;
  public string AssemblyName { get; init; } = "Assembly42";
  public bool Deterministic { get; init; } = true;
  public bool UseNetFramework35Target { get; init; }

  public override string ToString() => name;

  public Span<byte> Compile(string sourceCode)
  {
    var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

    var compilationOptions = new CSharpCompilationOptions(
      outputKind: OutputKind.DynamicallyLinkedLibrary,
      optimizationLevel: EnableOptimizations ? OptimizationLevel.Release : OptimizationLevel.Debug,
      deterministic: Deterministic,
      allowUnsafe: true,
      nullableContextOptions: NullableContextOptions.Enable);

    List<MetadataReference> metadataReferences;
    if (UseNetFramework35Target)
    {
      var root = TestHelpers.GetTestPath("libs_net35");
      metadataReferences = [
        MetadataReference.CreateFromFile(Path.Combine(root, "mscorlib.dll")),
        MetadataReference.CreateFromFile(Path.Combine(root, "System.dll")),
        MetadataReference.CreateFromFile(Path.Combine(root, "System.Core.dll"))
      ];
    }
    else
    {
      metadataReferences = GetAssembliesWithTypes(
        typeof(object), typeof(DynamicAttribute), typeof(Task), typeof(IEnumerable<>), typeof(Console));
    }

    var compilation = CSharpCompilation.Create(
      AssemblyName, [syntaxTree], metadataReferences, compilationOptions);

    using var memoryStream = new MemoryStream();
    var emitResult = compilation.Emit(memoryStream);
    if (!emitResult.Success)
    {
      // Handle compilation errors
      var errors = emitResult.Diagnostics
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .Select(d => $"{d.Id}: {d.GetMessage()}");

      throw new InvalidOperationException($"Compilation failed:\n{string.Join("\n", errors)}");
    }

    return memoryStream.ToArray();

    List<MetadataReference> GetAssembliesWithTypes(params IEnumerable<Type> types)
    {
      return types.Select(x => x.Assembly).Distinct()
        .Select(MetadataReference (x) => MetadataReference.CreateFromFile(x.Location))
        .ToList();
    }
  }
}
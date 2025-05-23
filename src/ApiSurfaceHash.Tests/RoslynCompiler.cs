using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ApiSurfaceHash.Tests;

public sealed class RoslynCompiler(string configName)
{
  public string ConfigName { get; } = configName;

  public bool EnableOptimizations { get; init; } = true;
  public string AssemblyName { get; init; } = "Assembly42";
  public bool Deterministic { get; init; } = true;
  public bool UseNetFramework35Target { get; init; }
  public bool EmitReferenceAssembly { get; init; }

  public override string ToString() => ConfigName;

  public Span<byte> Compile(string sourceCode)
  {
    var sourceFiles = new List<string> { sourceCode };

    var compilationOptions = new CSharpCompilationOptions(
      outputKind: OutputKind.DynamicallyLinkedLibrary,
      optimizationLevel: EnableOptimizations ? OptimizationLevel.Release : OptimizationLevel.Debug,
      deterministic: Deterministic,
      allowUnsafe: true,
      nullableContextOptions: NullableContextOptions.Enable);

    var metadataReferences = new List<MetadataReference>();
    if (UseNetFramework35Target)
    {
      var root = TestHelpers.GetTestPath("libs_net35");
      metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(root, "mscorlib.dll")));
      metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(root, "System.dll")));
      metadataReferences.Add(MetadataReference.CreateFromFile(Path.Combine(root, "System.Core.dll")));

      // add shims
      sourceFiles.Add(
        """
        namespace System.Runtime.CompilerServices;

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Parameter | AttributeTargets.ReturnValue | AttributeTargets.GenericParameter, Inherited = false)]
        internal sealed class NullableAttribute : Attribute
        {
          public NullableAttribute(byte value) { }
          public NullableAttribute(byte[]? value) { }
        }

        [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
        internal sealed class RequiredMemberAttribute : Attribute;

        [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
        internal sealed class CompilerFeatureRequiredAttribute : Attribute
        {
          public CompilerFeatureRequiredAttribute(string featureName) { }
        }

        internal static class IsExternalInit;
        """);
    }
    else
    {
      metadataReferences.AddRange(GetAssembliesWithTypes(
        typeof(object), typeof(DynamicAttribute), typeof(Task), typeof(IEnumerable<>), typeof(Console)));
    }

    var compilation = CSharpCompilation.Create(
      AssemblyName,
      sourceFiles.Select((x, index) => CSharpSyntaxTree.ParseText(x, path: $"file{index}.cs")),
      metadataReferences,
      compilationOptions);

    using var memoryStream = new MemoryStream();
    var emitResult = compilation.Emit(memoryStream, options: new EmitOptions(metadataOnly: EmitReferenceAssembly));
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
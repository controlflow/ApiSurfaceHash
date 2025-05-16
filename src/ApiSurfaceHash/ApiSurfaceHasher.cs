using System.Diagnostics.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// todo: internalsvisibleto
//[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Foo")]
// todo: sort hashes, not types/members
// todo: internal class '<PrivateImplementationDetails>' handling
// todo: detect popular FQNs with hash
// todo: DebuggableAttribute nested type with "" name - enum erased?
// todo: C# file-local types + internalsvisibleto = <Program>F9627F0A54B81FFFF0798796D9DC073EE4A8DE73FBB12F9E5435789023F83CA51__A is not referencable
// todo: top-level code <Main>$ - ignore types/members starting from '<'

public class ApiSurfaceHasher
{
  private readonly MetadataReader myMetadataReader;
  private readonly Dictionary<StringHandle, ulong> myStringHashes = new();
  // todo: split to increase locality?
  private readonly Dictionary<EntityHandle, ulong> myHashes = new();

  private static readonly ulong ourCompilerServicesNamespaceHash
    = LongHashCode.FromUtf8String("System.Runtime.CompilerServices"u8);
  private static readonly ulong ourInternalsVisibleNameHash
    = LongHashCode.FromUtf8String("InternalsVisibleTo"u8);

  private ApiSurfaceHasher(MetadataReader metadataReader)
  {
    myMetadataReader = metadataReader;
  }

  [Pure]
  private ulong GetOrComputeStringHash(StringHandle handle)
  {
    if (handle.IsNil)
      return LongHashCode.FnvOffset;

    if (!myStringHashes.TryGetValue(handle, out var hash))
    {
      // todo: count usages string hashes

      var stringReader = myMetadataReader.GetBlobReader(handle);
      myStringHashes[handle] = hash = LongHashCode.FromBlob(stringReader);
    }

    return hash;
  }

  [Pure]
  private ulong GetOrComputeAssemblyReferenceHash(AssemblyReferenceHandle handle)
  {
    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var assemblyReference = myMetadataReader.GetAssemblyReference(handle);

    // System.Private.CoreLib, Version=9.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e
    var nameHash = GetOrComputeStringHash(assemblyReference.Name);
    var version = assemblyReference.Version;
    var versionHash = LongHashCode.Combine(
      (ulong)version.Major, (ulong)version.Minor, (ulong)version.Revision, (ulong)version.Build);
    var cultureHash = GetOrComputeStringHash(assemblyReference.Culture);
    var publicKeyHash = LongHashCode.FromBlob(
      myMetadataReader.GetBlobReader(assemblyReference.PublicKeyOrToken));

    myHashes[handle] = hash = LongHashCode.Combine(nameHash, versionHash, cultureHash, publicKeyHash);

    return hash;
  }

  [Pure]
  private ulong GetOrComputeTypeReferenceHash(TypeReferenceHandle handle)
  {
    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var typeReference = myMetadataReader.GetTypeReference(handle);

    //var ns = myMetadataReader.GetString(typeReference.Namespace);
    //var fqn = (ns.Length == 0 ? "" : ns + ".") + myMetadataReader.GetString(typeReference.Name);

    var namespaceHash = GetOrComputeStringHash(typeReference.Namespace);
    var nameHash = GetOrComputeStringHash(typeReference.Name);

    var resolutionScope = typeReference.ResolutionScope;
    switch (resolutionScope.Kind)
    {
      case HandleKind.AssemblyReference: // type from a referenced assembly
      {
        var assemblyReferenceHash = GetOrComputeAssemblyReferenceHash((AssemblyReferenceHandle)resolutionScope);
        hash = LongHashCode.Combine(assemblyReferenceHash, namespaceHash, nameHash);
        break;
      }

      case HandleKind.TypeReference: // nested type
      {
        var containingTypeReferenceHash = GetOrComputeTypeReferenceHash((TypeReferenceHandle)resolutionScope);
        return LongHashCode.Combine(containingTypeReferenceHash, namespaceHash, nameHash);
      }

      case HandleKind.ModuleDefinition: // reference to current module, rare
      case HandleKind.ModuleReference: // type from another assembly module file
      default: // should not happen
      {
        hash = LongHashCode.Combine(namespaceHash, nameHash);
        break;
      }
    }

    return myHashes[handle] = hash;
  }

  // todo: nested types
  // todo: generic type parameters
  [Pure]
  private ulong GetTypeDefinitionHash(TypeDefinition typeDefinition)
  {
    var namespaceHash = GetOrComputeStringHash(typeDefinition.Namespace);
    var nameHash = GetOrComputeStringHash(typeDefinition.Name);

    // todo: visibility
    const TypeAttributes apiSurfaceAttributes =
      TypeAttributes.Abstract
      | TypeAttributes.Sealed
      | TypeAttributes.SpecialName
      | TypeAttributes.RTSpecialName;

    var attributesHash = (ulong)(typeDefinition.Attributes & apiSurfaceAttributes);

    return LongHashCode.Combine(namespaceHash, nameHash, attributesHash);
  }

  public static unsafe ulong Execute(byte* imagePtr, int imageLength)
  {
    using var peReader = new PEReader(imagePtr, imageLength);

    var metadataReader = peReader.GetMetadataReader(MetadataReaderOptions.Default);

    var surfaceHasher = new ApiSurfaceHasher(metadataReader);

    // 1. assembly attributes
    // 2. module attributes

    // foreach (var metadataReaderAssemblyReference in metadataReader.AssemblyReferences)
    // {
    //   var hash = surfaceHasher.GetAssemblyReferenceHash(metadataReaderAssemblyReference);
    // }

    var typeHashes = new List<ulong>(); // todo: sort hashes

    foreach (var typeDefinitionHandle in metadataReader.TypeDefinitions)
    {
      var typeDefinition = metadataReader.GetTypeDefinition(typeDefinitionHandle);

      var ns = metadataReader.GetString(typeDefinition.Namespace);
      var fqn = (ns.Length == 0 ? "" : ns + ".") + metadataReader.GetString(typeDefinition.Name);

      if ((typeDefinition.Attributes & TypeAttributes.Public) != 0)
      {
        typeHashes.Add(surfaceHasher.GetTypeDefinitionHash(typeDefinition));
      }
      else
      {
        //typeDefinition.Attributes.
      }
    }

    foreach (var customAttributeHandle in metadataReader.CustomAttributes)
    {
      var customAttribute = metadataReader.GetCustomAttribute(customAttributeHandle);
      if (customAttribute.Constructor.Kind == HandleKind.MemberReference)
      {
        //
      }
      else if (customAttribute.Constructor.Kind == HandleKind.MethodDefinition)
      {
        //
      }
    }

    return LongHashCode.Combine(typeHashes);
  }

  public static unsafe ulong Execute(Span<byte> assemblyBytes)
  {
    fixed (byte* ptr = &assemblyBytes[0])
    {
      return Execute(ptr, assemblyBytes.Length);
    }
  }
}

file static class LongHashCode
{
  public const ulong FnvOffset = 14695981039346656037UL;
  public const ulong FnvPrime = 1099511628211UL;

  public static unsafe ulong FromUtf8String(ReadOnlySpan<byte> utf8Bytes)
  {
    if (utf8Bytes.Length == 0) return FnvOffset;

    fixed (byte* ptr = &utf8Bytes[0])
    {
      return FromBlob(ptr, utf8Bytes.Length);
    }
  }

  [Pure]
  public static unsafe ulong FromBlob(BlobReader blobReader)
  {
    return FromBlob(blobReader.CurrentPointer, blobReader.Length);
  }

  [Pure]
  public static unsafe ulong FromBlob(byte* bytePtr, int length)
  {
    unchecked
    {
      var hash = FnvOffset;

      for (var index = 0; index < length; index++)
      {
        hash = hash * FnvPrime + bytePtr[index];
      }

      return hash;
    }
  }

  [Pure]
  public static ulong Combine(List<ulong> hashes)
  {
    unchecked
    {
      var hash = FnvOffset;

      foreach (var itemHash in hashes)
      {
        hash = hash * FnvPrime + itemHash;
      }

      return hash;
    }
  }

  [Pure]
  public static ulong Combine(ulong a, ulong b)
  {
    return unchecked(a * FnvPrime + b);
  }

  [Pure]
  public static ulong Combine(ulong a, ulong b, ulong c)
  {
    return unchecked((a * FnvPrime + b) * FnvPrime + c);
  }

  [Pure]
  public static ulong Combine(ulong a, ulong b, ulong c, ulong d)
  {
    return unchecked(((a * FnvPrime + b) * FnvPrime + c) * FnvPrime + d);
  }
}
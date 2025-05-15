using System.Diagnostics.Contracts;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

// todo: internalsvisibleto
//[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Foo")]
// todo: sort hashes, not types/members
// todo: internal class '<PrivateImplementationDetails>' handling
// todo: detect popular FQNs with hash

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

  private ulong GetStringHash(StringHandle handle)
  {
    if (!myStringHashes.TryGetValue(handle, out var hash))
    {
      var stringReader = myMetadataReader.GetBlobReader(handle);
      myStringHashes[handle] = hash = LongHashCode.FromBlob(stringReader);
    }

    return hash;
  }

  private ulong GetAssemblyReferenceHash(AssemblyReferenceHandle handle)
  {
    if (!myHashes.TryGetValue(handle, out var hash))
    {
      var assemblyReference = myMetadataReader.GetAssemblyReference(handle);
      var assemblyName = assemblyReference.GetAssemblyName();
      
      Get

      //myHashes[handle] = hash = ;
    }

    return hash;
  }

  private ulong GetTypeReferenceHash(TypeReferenceHandle handle)
  {
    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var typeReference = myMetadataReader.GetTypeReference(handle);

    var namespaceHash = GetStringHash(typeReference.Namespace);
    var nameHash = GetStringHash(typeReference.Name);

    var resolutionScope = typeReference.ResolutionScope;
    switch (resolutionScope.Kind)
    {
      case HandleKind.AssemblyReference:
      {
        var assemblyReferenceHash = GetAssemblyReferenceHash((AssemblyReferenceHandle)resolutionScope);
        hash = LongHashCode.Combine(assemblyReferenceHash, namespaceHash, nameHash);
        break;
      }

      case HandleKind.TypeReference: // nested type
      {
        var containingTypeReferenceHash = GetTypeReferenceHash((TypeReferenceHandle)resolutionScope);
        return LongHashCode.Combine(containingTypeReferenceHash, namespaceHash, nameHash);
      }

      case HandleKind.ModuleDefinition: // reference to current module, rare
      case HandleKind.ModuleReference: // type from another .netmodule
      default: // should not happen
      {
        hash = LongHashCode.Combine(namespaceHash, nameHash);
        break;
      }
    }

    return myHashes[handle] = hash;
  }

  public static unsafe ulong Execute(byte* imagePtr, int imageLength)
  {
    using var peReader = new PEReader(imagePtr, imageLength);

    var metadataReader = peReader.GetMetadataReader(MetadataReaderOptions.Default);

    // 1. assembly attributes
    // 2. 

    foreach (var metadataReaderAssemblyReference in metadataReader.AssemblyReferences)
    {
      
    }

    foreach (var typeDefinitionHandle in metadataReader.TypeDefinitions)
    {
      var typeDefinition = metadataReader.GetTypeDefinition(typeDefinitionHandle);

      var ns = metadataReader.GetString(typeDefinition.Namespace);
      var name = metadataReader.GetString(typeDefinition.Name);

      if (typeDefinition.Namespace.IsNil)
        continue;

      var stringBlob = metadataReader.GetBlobReader(typeDefinition.Namespace);


      var bytesLength = stringBlob.ReadCompressedInteger();

      var readBytes = stringBlob.ReadBytes(bytesLength);
      var sss = Encoding.UTF8.GetString(readBytes);

      ;
    }
    
    

    var attributeHashes = new List<long>();

    

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

    //metadataReader.StringComparer.Equals()

    //var stringHeap = metadataReader.GetBlobReader();

    return 0;
  }

  public static unsafe ulong Execute(Span<byte> assemblyBytes)
  {
    fixed (byte* ptr = &assemblyBytes[0])
    {
      return Execute(ptr, assemblyBytes.Length);
    }
  }
}

public static class LongHashCode
{
  private const ulong FnvOffset = 14695981039346656037UL;
  private const ulong FnvPrime = 1099511628211UL;

  public static unsafe ulong FromUtf8String(ReadOnlySpan<byte> utf8Bytes)
  {
    if (utf8Bytes.Length == 0) return FnvOffset;
    
    fixed (byte* ptr = &utf8Bytes[0])
    {
      
    }
  }

  [Pure]
  public static unsafe ulong FromBlob(BlobReader blobReader)
  {
    unchecked
    {
      var hash = FnvOffset;
      var ptr = blobReader.CurrentPointer;
      var length = blobReader.Length;

      for (var index = 0; index < length; index++)
      {
        hash = hash * FnvPrime + ptr[index];
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
}
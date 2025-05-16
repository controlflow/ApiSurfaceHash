using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ApiSurfaceHash;

// todo: internalsvisibleto
//[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Foo")]
// todo: sort hashes, not types/members
// todo: internal class '<PrivateImplementationDetails>' handling
// todo: detect popular FQNs with hash
// todo: DebuggableAttribute nested type with "" name - enum erased?
// todo: C# file-local types + internalsvisibleto = <Program>F9627F0A54B81FFFF0798796D9DC073EE4A8DE73FBB12F9E5435789023F83CA51__A is not referencable
// todo: top-level code <Main>$ - ignore types/members starting from '<'
// todo: init-only accessors breaking change
// todo: required members breaking change (count of members changed)
// todo: constant value change can affect compilation

public class ApiSurfaceHasher
{
  private readonly MetadataReader myMetadataReader;
  private readonly SignatureHasher mySignatureHasher;
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
    mySignatureHasher = new SignatureHasher(this);
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

  [Pure] // note: only includes FQN of the type definition
  private ulong GetOrComputeTypeDefinitionUsageHash(TypeDefinitionHandle handle)
  {
    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var typeDefinition = myMetadataReader.GetTypeDefinition(handle);

    var namespaceHash = GetOrComputeStringHash(typeDefinition.Namespace);
    var nameHash = GetOrComputeStringHash(typeDefinition.Name);

    hash = LongHashCode.Combine(namespaceHash, nameHash);

    return myHashes[handle] = hash;
  }

  // todo: similar method for type def "references" in attributes
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

  [Pure]
  private ulong GetOrComputeMemberReferenceHash(MemberReferenceHandle handle)
  {
    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var memberReference = myMetadataReader.GetMemberReference(handle);

    var nameHash = GetOrComputeStringHash(memberReference.Name);

    switch (memberReference.GetKind())
    {
      case MemberReferenceKind.Method:
      {
        var methodSignature = memberReference.DecodeMethodSignature(mySignatureHasher, genericContext: null);

        var signatureHeader = methodSignature.Header;
        

        break;
      }

      case MemberReferenceKind.Field:
        break;

      default:
        throw new BadImageFormatException();
    }

    

    return myHashes[handle] = hash;
  }

  // todo: replace with non-allocating
  private sealed class SignatureHasher : ISignatureTypeProvider<ulong, object?>
  {
    private readonly ApiSurfaceHasher mySurfaceHasher;

    public SignatureHasher(ApiSurfaceHasher surfaceHasher)
    {
      mySurfaceHasher = surfaceHasher;
    }

    ulong ISimpleTypeProvider<ulong>.GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
      return (ulong)typeCode; // use code itself as a hash
    }

    public ulong GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
      return mySurfaceHasher.GetOrComputeTypeDefinitionUsageHash(handle);
    }

    public ulong GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
      return mySurfaceHasher.GetOrComputeTypeReferenceHash(handle);
    }

    public ulong GetGenericInstantiation(ulong genericTypeHash, ImmutableArray<ulong> typeArgumentsHashes)
    {
      return LongHashCode.Combine(genericTypeHash, LongHashCode.Combine(typeArgumentsHashes));
    }

    public ulong GetSZArrayType(ulong elementTypeHash) => LongHashCode.Combine(elementTypeHash, 1);
    public ulong GetByReferenceType(ulong elementTypeHash) => LongHashCode.Combine(elementTypeHash, 2);
    public ulong GetPointerType(ulong elementTypeHash) => LongHashCode.Combine(elementTypeHash, 3);
    public ulong GetPinnedType(ulong elementTypeHash) => LongHashCode.Combine(elementTypeHash, 4);

    public ulong GetArrayType(ulong elementTypeHash, ArrayShape shape)
    {
      return LongHashCode.Combine(
        elementTypeHash,
        (ulong)shape.Rank,
        HashCombine(shape.LowerBounds),
        HashCombine(shape.Sizes));

      ulong HashCombine(ImmutableArray<int> values)
      {
        var hash = LongHashCode.FnvOffset;

        foreach (var value in values)
        {
          hash = LongHashCode.Combine(hash, (ulong)value);
        }

        return hash;
      }
    }

    public ulong GetFunctionPointerType(MethodSignature<ulong> signature)
    {
      var returnTypeHash = signature.ReturnType;
      var parameterTypesHash = LongHashCode.Combine(signature.ParameterTypes);
      var genericParametersCountHash = (ulong)signature.GenericParameterCount;
      var callingConventionHash = (ulong)signature.Header.CallingConvention;

      return LongHashCode.Combine(
        returnTypeHash, parameterTypesHash, genericParametersCountHash, callingConventionHash);
    }

    public ulong GetGenericMethodParameter(object? genericContext, int index)
    {
      throw new NotImplementedException();
    }

    public ulong GetGenericTypeParameter(object? genericContext, int index)
    {
      throw new NotImplementedException();
    }

    public ulong GetModifiedType(ulong modifier, ulong unmodifiedType, bool isRequired)
    {
      throw new NotImplementedException();
    }

    public ulong GetTypeFromSpecification(
      MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
      throw new NotImplementedException();
    }
  }

  // todo: nested types
  // todo: generic type parameters
  [Pure]
  private ulong GetTypeDefinitionSurfaceHash(TypeDefinition typeDefinition)
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

    var methodHashes = new List<ulong>();

    foreach (var methodDefinitionHandle in typeDefinition.GetMethods())
    {
      var methodDefinition = myMetadataReader.GetMethodDefinition(methodDefinitionHandle);

      if ((methodDefinition.Attributes & MethodAttributes.Public) == MethodAttributes.Public)
      {
        var methodDefinitionHash = GetMethodDefinitionSurfaceHash(methodDefinition);
        methodHashes.Add(methodDefinitionHash);
      }
    }

    methodHashes.Sort();

    var fqnHash = LongHashCode.Combine(namespaceHash, nameHash, attributesHash);
    var methodsHash = LongHashCode.Combine(methodHashes);

    return LongHashCode.Combine(fqnHash, methodsHash);
  }

  private ulong GetOrComputeConstantValueHash(ConstantHandle handle)
  {
    if (handle.IsNil)
      return LongHashCode.FnvOffset;

    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var constant = myMetadataReader.GetConstant(handle);

    var constantTypeHash = (ulong)constant.TypeCode;
    var constantValueBlobHash = LongHashCode.FromBlob(myMetadataReader.GetBlobReader(constant.Value));

    hash = LongHashCode.Combine(constantTypeHash, constantValueBlobHash);

    return myHashes[handle] = hash;
  }

  [Pure]
  private ulong GetMethodDefinitionSurfaceHash(MethodDefinition methodDefinition)
  {
    // todo: attrs, header
    var attributes = methodDefinition.Attributes;

    var nameHash = GetOrComputeStringHash(methodDefinition.Name);

    // todo: avoid materialization in the future
    var decodedSignature = methodDefinition.DecodeSignature(mySignatureHasher, genericContext: null);

    var parametersHash = LongHashCode.FnvOffset;
    foreach (var parameterHandle in methodDefinition.GetParameters())
    {
      var parameter = myMetadataReader.GetParameter(parameterHandle);

      const ParameterAttributes parameterApiSurfaceAttributesMask
        = ParameterAttributes.In
          | ParameterAttributes.Out
          | ParameterAttributes.Optional
          | ParameterAttributes.HasDefault
          | ParameterAttributes.Retval;

      var parameterAttributesHash = (ulong)(parameter.Attributes & parameterApiSurfaceAttributesMask);

      var parameterNameHash = GetOrComputeStringHash(parameter.Name);
      var parameterCustomAttributesHash = GetCustomAttributesHash(parameter.GetCustomAttributes());
      var optionalParameterValueHash = GetOrComputeConstantValueHash(parameter.GetDefaultValue());

      parametersHash = LongHashCode.Combine(
        parametersHash, LongHashCode.Combine(
          parameterNameHash, parameterAttributesHash, parameterCustomAttributesHash, optionalParameterValueHash));
    }

    var parameterTypesHash = LongHashCode.Combine(decodedSignature.ParameterTypes);
    var combinedSignatureHash = LongHashCode.Combine(nameHash, parameterTypesHash, decodedSignature.ReturnType);

    var customAttributesHash = GetCustomAttributesHash(methodDefinition.GetCustomAttributes());

    return LongHashCode.Combine(combinedSignatureHash, parametersHash, customAttributesHash);
  }

  [Pure] // todo: incomplete
  private ulong GetCustomAttributesHash(CustomAttributeHandleCollection customAttributes)
  {
    var hash = LongHashCode.FnvOffset;

    foreach (var customAttributeHandle in customAttributes)
    {
      var customAttribute = myMetadataReader.GetCustomAttribute(customAttributeHandle);

      // todo: include ctor signature

      switch (customAttribute.Constructor.Kind)
      {
        case HandleKind.MemberReference:
        {
          var memberReference = myMetadataReader.GetMemberReference((MemberReferenceHandle)customAttribute.Constructor);

          if (memberReference.Parent.Kind == HandleKind.TypeReference)
          {
            var typeReferenceHandle = (TypeReferenceHandle)memberReference.Parent;
            var typeReferenceHash = GetOrComputeTypeReferenceHash(typeReferenceHandle);

            hash = LongHashCode.Combine(hash, typeReferenceHash);
          }
          else
          {
            // ?
          }

          break;
        }

        case HandleKind.MethodDefinition:
        {
          var methodDefinition = myMetadataReader.GetMethodDefinition((MethodDefinitionHandle)customAttribute.Constructor);

          var typeDefinitionHandle = methodDefinition.GetDeclaringType();
          var typeDefinitionHash = GetOrComputeTypeDefinitionUsageHash(typeDefinitionHandle);

          hash = LongHashCode.Combine(hash, typeDefinitionHash);
          break;
        }
      }
    }

    return hash;
  }

  public static unsafe ulong Execute(byte* imagePtr, int imageLength)
  {
    using var peReader = new PEReader(imagePtr, imageLength);

    var metadataReader = peReader.GetMetadataReader(MetadataReaderOptions.Default);

    var surfaceHasher = new ApiSurfaceHasher(metadataReader);

    // 1. hash assembly attributes and check [assembly: InternalsVisibleTo] presence
    // 2. hash module attributes
    // 3. hash types
    // 4. hash embedded resources


    var typeHashes = new List<ulong>(); // todo: sort hashes

    foreach (var typeDefinitionHandle in metadataReader.TypeDefinitions)
    {
      var typeDefinition = metadataReader.GetTypeDefinition(typeDefinitionHandle);

      //var ns = metadataReader.GetString(typeDefinition.Namespace);
      //var fqn = (ns.Length == 0 ? "" : ns + ".") + metadataReader.GetString(typeDefinition.Name);

      if ((typeDefinition.Attributes & TypeAttributes.Public) != 0)
      {
        typeHashes.Add(surfaceHasher.GetTypeDefinitionSurfaceHash(typeDefinition));
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
    
    foreach (var manifestResourceHandle in metadataReader.ManifestResources)
    {
      var manifestResource = metadataReader.GetManifestResource(manifestResourceHandle);
      if ((manifestResource.Attributes & ManifestResourceAttributes.Public) != 0)
      {
        // manifestResource.Name; - match
      }
    }

    typeHashes.Sort();

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
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using ApiSurfaceHash;

// todo: class vs struct?
// todo: sort hashes, not types/members
// todo: detect popular FQNs with hash
// todo: top-level code <Main>$ - ignore types/members starting from '<'
// todo: init-only accessors breaking change
// todo: required members breaking change (count of members changed)
// todo: constant value change can affect compilation
// todo: exported types
// todo: readonly struct members
// todo: scoped modifier
// todo: delegate types
// todo: record clone method
// todo: type layout affects compilation?
// todo: getstring - remove all

public class ApiSurfaceHasher
{
  private readonly MetadataReader myMetadataReader;
  private readonly SignatureHasher mySignatureHasher;
  private readonly Dictionary<StringHandle, ulong> myStringHashes = new();
  // todo: split to increase locality?
  private readonly Dictionary<EntityHandle, ulong> myHashes = new();

  private const string InternalsVisibleToAttributeName = nameof(InternalsVisibleToAttribute);
  private const string SystemRuntimeCompilerServicesNamespace = "System.Runtime.CompilerServices";
  private static readonly ulong CompilerServicesNamespaceHash
    = LongHashCode.FromUtf8String("System.Runtime.CompilerServices"u8);
  private static readonly ulong InternalsVisibleNameHash
    = LongHashCode.FromUtf8String("InternalsVisibleToAttribute"u8);

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

  [Pure]
  private ulong GetOrComputeTypeUsageHash(EntityHandle handle)
  {
    switch (handle.Kind)
    {
      case HandleKind.TypeReference:
      {
        return GetOrComputeTypeReferenceHash((TypeReferenceHandle)handle);
      }

      // note: only includes FQN of the type definition
      case HandleKind.TypeDefinition:
      {
        if (myHashes.TryGetValue(handle, out var hash)) return hash;

        var typeDefinition = myMetadataReader.GetTypeDefinition((TypeDefinitionHandle)handle);

        var namespaceHash = GetOrComputeStringHash(typeDefinition.Namespace);
        var nameHash = GetOrComputeStringHash(typeDefinition.Name);

        hash = LongHashCode.Combine(namespaceHash, nameHash);

        return myHashes[handle] = hash;
      }

      case HandleKind.TypeSpecification:
      {
        if (myHashes.TryGetValue(handle, out var hash)) return hash;

        var typeSpecification = myMetadataReader.GetTypeSpecification((TypeSpecificationHandle)handle);

        var typeSpecificationHash = typeSpecification.DecodeSignature(mySignatureHasher, genericContext: null);
        var typeSpecificationCustomAttributesHash = GetCustomAttributesSurfaceHash(typeSpecification.GetCustomAttributes());

        hash = LongHashCode.Combine(typeSpecificationHash, typeSpecificationCustomAttributesHash);

        return myHashes[handle] = hash;
      }

      default:
        throw new BadImageFormatException();
    }
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

  // todo: replace with non-allocating direct decoder
  private sealed class SignatureHasher(ApiSurfaceHasher surfaceHasher)
    : ISignatureTypeProvider<ulong, object?>
  {
    ulong ISimpleTypeProvider<ulong>.GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
      return (ulong)typeCode; // use code itself as a hash
    }

    public ulong GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
      return surfaceHasher.GetOrComputeTypeUsageHash(handle);
    }

    public ulong GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
      return surfaceHasher.GetOrComputeTypeReferenceHash(handle);
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

    public ulong GetGenericMethodParameter(object? _, int index) => LongHashCode.Combine((ulong)index, 1000);
    public ulong GetGenericTypeParameter(object? _, int index) => LongHashCode.Combine((ulong)index, 1000_000);

    public ulong GetModifiedType(ulong modifierHash, ulong unmodifiedTypeHash, bool isRequired)
    {
      // `ref readonly` returns are encoded via T& modreq([InAttribute])
      return LongHashCode.Combine(unmodifiedTypeHash, modifierHash, isRequired ? 42UL : 0UL);
    }

    public ulong GetTypeFromSpecification(
      MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
    {
      throw new InvalidOperationException();
    }
  }

  // todo: nested types
  // todo: generic type parameters
  [Pure]
  private ulong GetTypeDefinitionSurfaceHash(TypeDefinition typeDefinition, bool includeInternals)
  {
    var namespaceHash = GetOrComputeStringHash(typeDefinition.Namespace);
    var nameHash = GetOrComputeStringHash(typeDefinition.Name);

    const TypeAttributes apiSurfaceAttributes =
      TypeAttributes.Abstract
      | TypeAttributes.Sealed
      | TypeAttributes.SpecialName
      | TypeAttributes.RTSpecialName
      | TypeAttributes.ClassSemanticsMask // interface or not
      | TypeAttributes.VisibilityMask;

    var typeSurfaceAttributes = typeDefinition.Attributes & apiSurfaceAttributes;
    var typeSurfaceAttributesHash = (ulong)typeSurfaceAttributes;

    var typeTypeParametersHash = GetTypeParametersSurfaceHash(typeDefinition.GetGenericParameters());

    // todo:
    typeDefinition.GetProperties();
    typeDefinition.GetEvents();

    var typeMethodHashes = new List<ulong>();

    foreach (var methodDefinitionHandle in typeDefinition.GetMethods())
    {
      var methodDefinition = myMetadataReader.GetMethodDefinition(methodDefinitionHandle);

      if (IsPartOfTheApiSurface(methodDefinition.Attributes, includeInternals))
      {
        var methodDefinitionHash = GetMethodDefinitionSurfaceHash(methodDefinition);
        typeMethodHashes.Add(methodDefinitionHash);
      }
    }

    typeMethodHashes.Sort();

    var typeCustomAttributesHash = GetCustomAttributesSurfaceHash(typeDefinition.GetCustomAttributes());

    var fqnHash = LongHashCode.Combine(namespaceHash, nameHash, typeSurfaceAttributesHash);
    var methodsHash = LongHashCode.Combine(typeMethodHashes);

    return LongHashCode.Combine(fqnHash, typeTypeParametersHash, typeCustomAttributesHash, methodsHash);
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
  private ulong GetTypeParametersSurfaceHash(GenericParameterHandleCollection typeParameters)
  {
    var typeParameterHashes = new List<ulong>();

    foreach (var typeParameterHandle in typeParameters)
    {
      var typeParameter = myMetadataReader.GetGenericParameter(typeParameterHandle);

      // note: typeParameter.Name - is not a part of signature, type parameters are positional
      var typeParameterIndexHash = (ulong)typeParameter.Index;
      var typeParameterConstraintsHash = (ulong)typeParameter.Attributes;

      foreach (var typeParameterConstraintHandle in typeParameter.GetConstraints())
      {
        var constraint = myMetadataReader.GetGenericParameterConstraint(typeParameterConstraintHandle);

        var constraintTypeUsageHash = GetOrComputeTypeUsageHash(constraint.Type);
        var constraintCustomAttributesHash = GetCustomAttributesSurfaceHash(constraint.GetCustomAttributes());

        typeParameterConstraintsHash = LongHashCode.Combine(typeParameterConstraintsHash,
          constraintTypeUsageHash, constraintCustomAttributesHash);
      }

      var typeParameterCustomAttributesHash = GetCustomAttributesSurfaceHash(typeParameter.GetCustomAttributes());

      typeParameterHashes.Add(
        LongHashCode.Combine(typeParameterIndexHash, typeParameterConstraintsHash, typeParameterCustomAttributesHash));
    }

    typeParameterHashes.Sort();

    return LongHashCode.Combine(typeParameterHashes);
  }

  [Pure]
  private ulong GetMethodDefinitionSurfaceHash(MethodDefinition methodDefinition)
  {
    var methodName = myMetadataReader.GetString(methodDefinition.Name);

    const MethodAttributes apiSurfaceAttributes =
      MethodAttributes.MemberAccessMask
      | MethodAttributes.Static;

    // todo: attrs, header

    var methodAttributes = methodDefinition.Attributes & apiSurfaceAttributes;
    var methodAttributesHash = (ulong)methodAttributes;

    var methodNameHash = GetOrComputeStringHash(methodDefinition.Name);

    // type parameters
    var methodTypeParametersHash = GetTypeParametersSurfaceHash(methodDefinition.GetGenericParameters());

    // formal parameters
    var decodedSignature = methodDefinition.DecodeSignature(mySignatureHasher, genericContext: null);

    var methodParametersHash = LongHashCode.FnvOffset;
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
      var parameterCustomAttributesHash = GetCustomAttributesSurfaceHash(parameter.GetCustomAttributes());
      var parameterOptionalValueHash = GetOrComputeConstantValueHash(parameter.GetDefaultValue());

      methodParametersHash = LongHashCode.Combine(
        methodParametersHash, LongHashCode.Combine(
          parameterNameHash, parameterAttributesHash, parameterCustomAttributesHash, parameterOptionalValueHash));
    }

    var methodReturnTypeHash = decodedSignature.ReturnType;
    var methodParameterTypesHash = LongHashCode.Combine(decodedSignature.ParameterTypes);
    var methodCombinedSignatureHash = LongHashCode.Combine(
      methodNameHash, methodParameterTypesHash, methodTypeParametersHash, methodReturnTypeHash);

    var methodCustomAttributesHash = GetCustomAttributesSurfaceHash(methodDefinition.GetCustomAttributes());

    return LongHashCode.Combine(
      methodAttributesHash, methodCombinedSignatureHash,
      methodParametersHash, methodCustomAttributesHash);
  }

  private bool CheckHasInternalsVisibleTo(AssemblyDefinition assemblyDefinition)
  {
    foreach (var customAttributeHandle in assemblyDefinition.GetCustomAttributes())
    {
      var customAttribute = myMetadataReader.GetCustomAttribute(customAttributeHandle);

      switch (customAttribute.Constructor.Kind)
      {
        case HandleKind.MemberReference:
        {
          var memberReference = myMetadataReader.GetMemberReference((MemberReferenceHandle)customAttribute.Constructor);

          if (memberReference.Parent.Kind == HandleKind.TypeReference)
          {
            var typeReference = myMetadataReader.GetTypeReference((TypeReferenceHandle)memberReference.Parent);

            var s = myMetadataReader.GetString(typeReference.Name);

            if (GetOrComputeStringHash(typeReference.Name) == InternalsVisibleNameHash
                && GetOrComputeStringHash(typeReference.Namespace) == CompilerServicesNamespaceHash
                && myMetadataReader.StringComparer.Equals(typeReference.Name, InternalsVisibleToAttributeName)
                && myMetadataReader.StringComparer.Equals(typeReference.Namespace, SystemRuntimeCompilerServicesNamespace))
            {
              return true;
            }
          }
          else
          {
            // todo: ?
          }

          break;
        }

        case HandleKind.MethodDefinition:
        {
          var methodDefinition = myMetadataReader.GetMethodDefinition((MethodDefinitionHandle)customAttribute.Constructor);
          var typeDefinition = myMetadataReader.GetTypeDefinition(methodDefinition.GetDeclaringType());

          if (GetOrComputeStringHash(typeDefinition.Name) == InternalsVisibleNameHash
              && GetOrComputeStringHash(typeDefinition.Namespace) == CompilerServicesNamespaceHash
              && myMetadataReader.StringComparer.Equals(typeDefinition.Name, InternalsVisibleToAttributeName)
              && myMetadataReader.StringComparer.Equals(typeDefinition.Namespace, SystemRuntimeCompilerServicesNamespace))
          {
            return true;
          }

          break;
        }
      }
    }

    return false;
  }

  // todo: not all attrs are part of the API "surface"
  [Pure] // todo: incomplete
  private ulong GetCustomAttributesSurfaceHash(CustomAttributeHandleCollection customAttributes)
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
          var typeDefinitionHash = GetOrComputeTypeUsageHash(typeDefinitionHandle);

          hash = LongHashCode.Combine(hash, typeDefinitionHash);
          break;
        }
      }

      // todo: this is not correct, will decode it some day
      var attributeBlobReader = myMetadataReader.GetBlobReader(customAttribute.Value);
      var attributeBlobHash = LongHashCode.FromBlob(attributeBlobReader);

      hash = LongHashCode.Combine(hash, attributeBlobHash);
    }

    return hash;
  }

  public static unsafe ulong Execute(byte* imagePtr, int imageLength)
  {
    using var peReader = new PEReader(imagePtr, imageLength);

    var metadataReader = peReader.GetMetadataReader(MetadataReaderOptions.Default);

    var surfaceHasher = new ApiSurfaceHasher(metadataReader);

    var assemblyDefinition = metadataReader.GetAssemblyDefinition();
    var internalsAreVisible = surfaceHasher.CheckHasInternalsVisibleTo(assemblyDefinition);
    var assemblyCustomAttributesSurfaceHash = surfaceHasher.GetCustomAttributesSurfaceHash(assemblyDefinition.GetCustomAttributes());

    // 1. hash assembly attributes and check [assembly: InternalsVisibleTo] presence
    // 2. hash module attributes
    // 3. hash types
    // 4. hash embedded resources

    var typeHashes = new List<ulong>();

    foreach (var typeDefinitionHandle in metadataReader.TypeDefinitions)
    {
      var typeDefinition = metadataReader.GetTypeDefinition(typeDefinitionHandle);

      var ns = metadataReader.GetString(typeDefinition.Namespace);
      var fqn = (ns.Length == 0 ? "" : ns + ".") + metadataReader.GetString(typeDefinition.Name);

      if (surfaceHasher.IsPartOfTheApiSurface(typeDefinition, internalsAreVisible))
      {
        typeHashes.Add(
          surfaceHasher.GetTypeDefinitionSurfaceHash(typeDefinition, internalsAreVisible));
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

  [DebuggerStepThrough]
  public static unsafe ulong Execute(Span<byte> assemblyBytes)
  {
    fixed (byte* ptr = &assemblyBytes[0])
    {
      return Execute(ptr, assemblyBytes.Length);
    }
  }

  [Pure]
  private bool IsPartOfTheApiSurface(TypeDefinition typeDefinition, bool includeInternals)
  {
    var accessRights = typeDefinition.Attributes & TypeAttributes.VisibilityMask;

    var declaringTypeHandle = typeDefinition.GetDeclaringType();
    if (!declaringTypeHandle.IsNil) // nested type
    {
      var containingTypeDefinition = myMetadataReader.GetTypeDefinition(declaringTypeHandle);

      if (!IsPartOfTheApiSurface(containingTypeDefinition, includeInternals))
        return false;
    }

    switch (accessRights)
    {
      case TypeAttributes.Public:
      case TypeAttributes.NestedPublic:
      case TypeAttributes.NestedFamily: // protected
      case TypeAttributes.NestedFamORAssem: // protected internal
      {
        return true;
      }

      case 0 when includeInternals: // internal
      case TypeAttributes.NestedAssembly when includeInternals: // internal
      case TypeAttributes.NestedFamANDAssem when includeInternals: // private protected
      {
        // compiler-generated types:
        //   - `<Module>`
        //   - `<PrivateImplementationDetails>`
        //   - file-local C# types
        var typeNameBlobReader = myMetadataReader.GetBlobReader(typeDefinition.Name);
        if (typeNameBlobReader.Length > 0)
        {
          // check the first character to be ASCII '<'
          var firstChar = (char)typeNameBlobReader.ReadByte();
          if (firstChar == '<')
          {
            return false;
          }
        }

        return true;
      }
    }

    return false;
  }

  [Pure]
  private bool IsPartOfTheApiSurface(MethodAttributes methodAttributes, bool includeInternals)
  {
    methodAttributes &= MethodAttributes.MemberAccessMask;

    switch (methodAttributes)
    {
      case MethodAttributes.Public:
      case MethodAttributes.Family:
      case MethodAttributes.FamORAssem:
      case MethodAttributes.Assembly when includeInternals:
      case MethodAttributes.FamANDAssem when includeInternals:
        return true;

      default:
        return false;
    }
  }
}
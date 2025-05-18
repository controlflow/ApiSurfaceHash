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
// todo: typeof(T) in attribute can reference internal/private type
// todo: attrs can be internal!

public class ApiSurfaceHasher
{
  private readonly MetadataReader myMetadataReader;
  private readonly SignatureHasher mySignatureHasher;
  private readonly Dictionary<StringHandle, ulong> myStringHashes = new();
  // todo: split to increase locality?
  private readonly Dictionary<EntityHandle, ulong> myHashes = new();
  private bool myInternalsAreVisible;

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
    var assemblyNameHash = GetOrComputeStringHash(assemblyReference.Name);
    var assemblyVersion = assemblyReference.Version;
    var assemblyVersionHash = LongHashCode.Combine(
      (ulong)assemblyVersion.Major, (ulong)assemblyVersion.Minor, (ulong)assemblyVersion.Revision, (ulong)assemblyVersion.Build);
    var assemblyCultureHash = GetOrComputeStringHash(assemblyReference.Culture);
    var assemblyPublicKeyHash = LongHashCode.FromBlob(
      myMetadataReader.GetBlobReader(assemblyReference.PublicKeyOrToken));

    hash = LongHashCode.Combine(assemblyNameHash, assemblyVersionHash, assemblyCultureHash, assemblyPublicKeyHash);

    return myHashes[handle] = hash;
  }

  [Pure]
  private ulong GetOrComputeTypeUsageHash(EntityHandle handle)
  {
    if (handle.IsNil) throw new ArgumentNullException();

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

  [Pure]
  private ulong GetTypeDefinitionSurfaceHash(TypeDefinition typeDefinition)
  {
    var typeNamespaceHash = GetOrComputeStringHash(typeDefinition.Namespace);
    var typeNameHash = GetOrComputeStringHash(typeDefinition.Name);

    const TypeAttributes apiSurfaceAttributes =
      TypeAttributes.Abstract
      | TypeAttributes.Sealed
      | TypeAttributes.SpecialName
      | TypeAttributes.RTSpecialName
      | TypeAttributes.ClassSemanticsMask // interface or not
      | TypeAttributes.VisibilityMask;

    var typeAttributesHash = (ulong)(typeDefinition.Attributes & apiSurfaceAttributes);
    var typeSuperTypesHash = GetSuperTypesHash();
    var typeTypeParametersHash = GetTypeParametersSurfaceHash(typeDefinition.GetGenericParameters());

    var typeMemberHashes = new List<ulong>();

    foreach (var fieldDefinitionHandle in typeDefinition.GetFields())
    {
      var fieldDefinition = myMetadataReader.GetFieldDefinition(fieldDefinitionHandle);

      if (IsPartOfTheApiSurface(fieldDefinition.Attributes))
      {
        typeMemberHashes.Add(GetFieldDefinitionSurfaceHash(fieldDefinition));
      }
    }

    var apiSurfaceMethods = new HashSet<MethodDefinitionHandle>();

    foreach (var methodDefinitionHandle in typeDefinition.GetMethods())
    {
      var methodDefinition = myMetadataReader.GetMethodDefinition(methodDefinitionHandle);

      if (IsPartOfTheApiSurface(methodDefinition.Attributes))
      {
        typeMemberHashes.Add(GetMethodDefinitionSurfaceHash(methodDefinition));
        apiSurfaceMethods.Add(methodDefinitionHandle);
      }
    }

    // todo:
    foreach (var propertyDefinitionHandle in typeDefinition.GetProperties())
    {
      var propertyDefinition = myMetadataReader.GetPropertyDefinition(propertyDefinitionHandle);

      var propertySignature = propertyDefinition.DecodeSignature(mySignatureHasher, genericContext: null);
      // todo: lookup accessor methods
    }

    //typeDefinition.GetEvents();

    typeMemberHashes.Sort();

    var typeCustomAttributesHash = GetCustomAttributesSurfaceHash(typeDefinition.GetCustomAttributes());

    var typeHeaderHash = LongHashCode.Combine(
      typeAttributesHash, typeNamespaceHash, typeNameHash, typeTypeParametersHash, typeSuperTypesHash);
    var typeMembersHash = LongHashCode.Combine(typeMemberHashes);

    return LongHashCode.Combine(typeHeaderHash, typeCustomAttributesHash, typeMembersHash);

    ulong GetSuperTypesHash()
    {
      var typeBaseTypeHash = typeDefinition.BaseType.IsNil ? 0UL : GetOrComputeTypeUsageHash(typeDefinition.BaseType);
      var typeImplementedInterfaceHashes = new List<ulong>();

      foreach (var interfaceImplementationHandle in typeDefinition.GetInterfaceImplementations())
      {
        var interfaceImplementation = myMetadataReader.GetInterfaceImplementation(interfaceImplementationHandle);

        // skip internal interface implementations
        var topLevelTypeDefinitionHandle = TryGetTopLevelTypeDefinition(interfaceImplementation.Interface);
        if (!topLevelTypeDefinitionHandle.IsNil)
        {
          var topLevelTypeDefinition = myMetadataReader.GetTypeDefinition(topLevelTypeDefinitionHandle);
          if (!IsPartOfTheApiSurface(topLevelTypeDefinition))
            continue;
        }

        var interfaceImplementationHash = GetOrComputeTypeUsageHash(interfaceImplementation.Interface);
        var interfaceImplementationCustomAttributesHash = GetCustomAttributesSurfaceHash(interfaceImplementation.GetCustomAttributes());

        typeImplementedInterfaceHashes.Add(LongHashCode.Combine(
          interfaceImplementationHash, interfaceImplementationCustomAttributesHash));
      }

      typeImplementedInterfaceHashes.Sort();

      return LongHashCode.Combine(typeBaseTypeHash, LongHashCode.Combine(typeImplementedInterfaceHashes));
    }

    [Pure]
    TypeDefinitionHandle TryGetTopLevelTypeDefinition(EntityHandle entityHandle)
    {
      switch (entityHandle.Kind)
      {
        case HandleKind.TypeDefinition: // : I
        {
          return (TypeDefinitionHandle)entityHandle;
        }

        case HandleKind.TypeSpecification: // : I<X>
        {
          var interfaceTypeSpecification = myMetadataReader.GetTypeSpecification((TypeSpecificationHandle)entityHandle);
          var signatureBlobReader = myMetadataReader.GetBlobReader(interfaceTypeSpecification.Signature);

          var typeCode = (SignatureTypeCode)signatureBlobReader.ReadCompressedInteger();
          if (typeCode != SignatureTypeCode.GenericTypeInstance) break;

          var genericTypeCode = (SignatureTypeKind)signatureBlobReader.ReadCompressedInteger();
          if (genericTypeCode != SignatureTypeKind.Class) break;

          var typeHandle = signatureBlobReader.ReadTypeHandle();
          if (typeHandle is { IsNil: false, Kind: HandleKind.TypeDefinition })
          {
            return (TypeDefinitionHandle)typeHandle;
          }

          break;
        }
      }

      return default;
    }
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
    var typeParameterTypeConstraintsHashes = new List<ulong>();

    foreach (var typeParameterHandle in typeParameters)
    {
      var typeParameter = myMetadataReader.GetGenericParameter(typeParameterHandle);

      // note: typeParameter.Name - is not a part of signature, type parameters are positional
      var typeParameterIndexHash = (ulong)typeParameter.Index;
      var typeParameterAttributesHash = (ulong)typeParameter.Attributes;

      typeParameterTypeConstraintsHashes.Clear();

      foreach (var typeParameterConstraintHandle in typeParameter.GetConstraints())
      {
        var typeConstraint = myMetadataReader.GetGenericParameterConstraint(typeParameterConstraintHandle);

        var typeConstraintTypeUsageHash = GetOrComputeTypeUsageHash(typeConstraint.Type);
        var typeConstraintCustomAttributesHash = GetCustomAttributesSurfaceHash(typeConstraint.GetCustomAttributes());

        typeParameterTypeConstraintsHashes.Add(LongHashCode.Combine(
          typeConstraintTypeUsageHash, typeConstraintCustomAttributesHash));
      }

      typeParameterTypeConstraintsHashes.Sort();

      var typeParameterTypeConstraintsHash = LongHashCode.Combine(typeParameterTypeConstraintsHashes);
      var typeParameterCustomAttributesHash = GetCustomAttributesSurfaceHash(typeParameter.GetCustomAttributes());

      typeParameterHashes.Add(LongHashCode.Combine(
        typeParameterIndexHash, typeParameterAttributesHash, typeParameterTypeConstraintsHash, typeParameterCustomAttributesHash));
    }

    typeParameterHashes.Sort();

    return LongHashCode.Combine(typeParameterHashes);
  }

  [Pure]
  private ulong GetFieldDefinitionSurfaceHash(FieldDefinition fieldDefinition)
  {
    //var fieldName = myMetadataReader.GetString(fieldDefinition.Name);

    const FieldAttributes apiSurfaceAttributes =
      FieldAttributes.FieldAccessMask
      | FieldAttributes.Static
      | FieldAttributes.InitOnly
      | FieldAttributes.Literal
      | FieldAttributes.SpecialName;

    var fieldAttributes = fieldDefinition.Attributes & apiSurfaceAttributes;
    var fieldAttributesHash = (ulong)fieldAttributes;

    var fieldNameHash = GetOrComputeStringHash(fieldDefinition.Name);
    var fieldTypeHash = fieldDefinition.DecodeSignature(mySignatureHasher, genericContext: null);
    var fieldConstantValueHash = GetOrComputeConstantValueHash(fieldDefinition.GetDefaultValue());

    var fieldCustomAttributesHash = GetCustomAttributesSurfaceHash(fieldDefinition.GetCustomAttributes());

    var fieldSignatureHash = LongHashCode.Combine(fieldNameHash, fieldTypeHash, fieldConstantValueHash);

    return LongHashCode.Combine(fieldAttributesHash, fieldSignatureHash, fieldCustomAttributesHash);
  }

  [Pure]
  private ulong GetMethodDefinitionSurfaceHash(MethodDefinition methodDefinition)
  {
    //var methodName = myMetadataReader.GetString(methodDefinition.Name);

    const MethodAttributes apiSurfaceAttributes =
      MethodAttributes.MemberAccessMask
      | MethodAttributes.Static
      | MethodAttributes.Abstract
      | MethodAttributes.Virtual
      | MethodAttributes.Final
      | MethodAttributes.SpecialName;

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

            if (GetOrComputeStringHash(typeReference.Name) == InternalsVisibleNameHash
                && GetOrComputeStringHash(typeReference.Namespace) == CompilerServicesNamespaceHash
                && myMetadataReader.StringComparer.Equals(typeReference.Name, InternalsVisibleToAttributeName)
                && myMetadataReader.StringComparer.Equals(typeReference.Namespace, SystemRuntimeCompilerServicesNamespace))
            {
              myInternalsAreVisible = true;
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
            myInternalsAreVisible = true;
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

          // todo: more efficient?
          var typeDefinition = myMetadataReader.GetTypeDefinition(typeDefinitionHandle);
          if (!IsPartOfTheApiSurface(typeDefinition))
            continue;

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

      if (surfaceHasher.IsPartOfTheApiSurface(typeDefinition))
      {
        typeHashes.Add(
          surfaceHasher.GetTypeDefinitionSurfaceHash(typeDefinition));
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
  private bool IsPartOfTheApiSurface(TypeDefinition typeDefinition)
  {
    var accessRights = typeDefinition.Attributes & TypeAttributes.VisibilityMask;

    var declaringTypeHandle = typeDefinition.GetDeclaringType();
    if (!declaringTypeHandle.IsNil) // nested type
    {
      var containingTypeDefinition = myMetadataReader.GetTypeDefinition(declaringTypeHandle);

      if (!IsPartOfTheApiSurface(containingTypeDefinition))
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

      case 0 when myInternalsAreVisible: // internal
      case TypeAttributes.NestedAssembly when myInternalsAreVisible: // internal
      case TypeAttributes.NestedFamANDAssem when myInternalsAreVisible: // private protected
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
  private bool IsPartOfTheApiSurface(MethodAttributes methodAttributes)
  {
    methodAttributes &= MethodAttributes.MemberAccessMask;

    switch (methodAttributes)
    {
      case MethodAttributes.Public:
      case MethodAttributes.Family:
      case MethodAttributes.FamORAssem:
      case MethodAttributes.Assembly when myInternalsAreVisible:
      case MethodAttributes.FamANDAssem when myInternalsAreVisible:
        return true;

      default:
        return false;
    }
  }

  [Pure]
  private bool IsPartOfTheApiSurface(FieldAttributes fieldAttributes)
  {
    fieldAttributes &= FieldAttributes.FieldAccessMask;

    switch (fieldAttributes)
    {
      case FieldAttributes.Public:
      case FieldAttributes.Family:
      case FieldAttributes.FamORAssem:
      case FieldAttributes.Assembly when myInternalsAreVisible:
      case FieldAttributes.FamANDAssem when myInternalsAreVisible:
        return true;

      default:
        return false;
    }
  }
}
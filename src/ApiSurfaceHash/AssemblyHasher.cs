using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

// todo: typeof(T) in attribute can reference internal/private type, via string; how enum values are stored?
// todo: should internal attrs be stripped despite [InternalsVisibleTo]?
// todo: check coverage
// todo: test memory traffic
// todo: do we need parallelism per assembly?

public class AssemblyHasher
{
  private readonly MetadataReader myMetadataReader;
  private readonly AssemblyHasherOptions myHasherOptions;
  private readonly SignatureHasher mySignatureHasher;
  private readonly SignatureHasher2 mySignatureHasher2;
  private readonly StructFieldTypesHasher myStructFieldTypesHasher;
  private readonly StructFieldTypesHasher2 myStructFieldTypesHasher2;
  private readonly Dictionary<StringHandle, ulong> myStringHashes = new();
  // todo: split to increase locality?
  private readonly Dictionary<EntityHandle, ulong> myHashes = new();
  private readonly Dictionary<TypeDefinitionHandle, ulong> myStructFieldTypeHashes = new();
  private readonly HashSet<EntityHandle> myIgnoredAttributeTypes = [];
  private readonly HashSet<EntityHandle> myIncludedAttributeTypes = [];
  private EntityHandle mySystemValueTypeClassHandle;
  private bool myInternalsAreVisible;

  private AssemblyHasher(MetadataReader metadataReader, AssemblyHasherOptions options)
  {
    myMetadataReader = metadataReader;
    mySignatureHasher = new SignatureHasher(this);
    mySignatureHasher2 = new SignatureHasher2(this);
    myStructFieldTypesHasher = new StructFieldTypesHasher(this);
    myStructFieldTypesHasher2 = new StructFieldTypesHasher2(this);
    myHasherOptions = options;
  }

  #region Entry point

  public static ulong Run(PEReader peReader, AssemblyHasherOptions options = AssemblyHasherOptions.None)
  {
    var metadataReader = peReader.GetMetadataReader(MetadataReaderOptions.Default);

    var surfaceHasher = new AssemblyHasher(metadataReader, options);

    var assemblyDefinition = metadataReader.GetAssemblyDefinition();
    var moduleDefinition = metadataReader.GetModuleDefinition();

    // 0. check assembly definition
    var assemblyDefinitionHash = surfaceHasher.ComputeAssemblyDefinitionHash(assemblyDefinition);

    // 1. check [assembly: InternalsVisibleTo] presence
    surfaceHasher.CheckIfInternalsAreExposed(assemblyDefinition);

    // 2. hash assembly and module attributes
    var assemblyCustomAttributesSurfaceHash = surfaceHasher.ComputeCustomAttributesSurfaceHash(assemblyDefinition.GetCustomAttributes());
    var moduleCustomAttributesSurfaceHash = surfaceHasher.ComputeCustomAttributesSurfaceHash(moduleDefinition.GetCustomAttributes());

    // 3. hash surface types
    using var typeHashes = new SortedHashesSet();

    foreach (var typeDefinitionHandle in metadataReader.TypeDefinitions)
    {
      var typeDefinition = metadataReader.GetTypeDefinition(typeDefinitionHandle);

      if (surfaceHasher.IsPartOfTheApiSurface(typeDefinition))
      {
        typeHashes.Add(surfaceHasher.ComputeTypeDefinitionSurfaceHash(typeDefinition));
      }
    }

    // 4. exported types
    foreach (var exportedTypeHandle in metadataReader.ExportedTypes)
    {
      var exportedType = metadataReader.GetExportedType(exportedTypeHandle);

      if (surfaceHasher.IsPartOfTheApiSurface(exportedType))
      {
        typeHashes.Add(surfaceHasher.ComputeExportedTypeDefinitionSurfaceHash(exportedType));
      }
    }

    // 5. hash embedded attributes
    foreach (var manifestResourceHandle in metadataReader.ManifestResources)
    {
      var manifestResource = metadataReader.GetManifestResource(manifestResourceHandle);

      if (surfaceHasher.IsPartOfTheApiSurface(manifestResource, assemblyDefinition))
      {
        typeHashes.Add(ComputeResourceStreamContentsHash(peReader, manifestResource));
      }
    }

    var assemblyHash = LongHashCode.Combine(
      assemblyDefinitionHash,
      assemblyCustomAttributesSurfaceHash,
      moduleCustomAttributesSurfaceHash,
      typeHashes.CombineAll());

    return assemblyHash;
  }

  [DebuggerStepThrough]
  public static unsafe ulong Run(byte* imagePtr, int imageLength, AssemblyHasherOptions options = AssemblyHasherOptions.None)
  {
    using var peReader = new PEReader(imagePtr, imageLength);

    return Run(peReader, options);
  }

  [DebuggerStepThrough]
  public static unsafe ulong Run(Span<byte> assemblyBytes, AssemblyHasherOptions options = AssemblyHasherOptions.None)
  {
    fixed (byte* ptr = &assemblyBytes[0])
    {
      return Run(ptr, assemblyBytes.Length, options);
    }
  }

  [DebuggerStepThrough]
  public static ulong Run(Stream peStream, AssemblyHasherOptions options = AssemblyHasherOptions.None)
  {
    // note: cannot use `PrefetchMetadata` options because of F# resources
    using var peReader = new PEReader(peStream);

    return Run(peReader, options);
  }

  #endregion
  #region Definitions hashing

  [Pure]
  private ulong ComputeAssemblyDefinitionHash(AssemblyDefinition assemblyDefinition)
  {
    var assemblyNameHash = GetOrComputeStringHash(assemblyDefinition.Name);

    // note: do not include assembly version, like old R#'s surface hasher
    // var assemblyVersionHash = LongHashCode.FromVersion(assemblyDefinition.Version);

    var assemblyCultureHash = GetOrComputeStringHash(assemblyDefinition.Culture);
    var assemblyPublicKeyHash = LongHashCode.FromBlob(myMetadataReader.GetBlobReader(assemblyDefinition.PublicKey));

    //todo: assemblyDefinition.HashAlgorithm?

    return LongHashCode.Combine(assemblyNameHash, assemblyCultureHash, assemblyPublicKeyHash);
  }

  private const TypeAttributes TypeApiSurfaceAttributes =
    TypeAttributes.Abstract
    | TypeAttributes.Sealed
    | TypeAttributes.SpecialName
    | TypeAttributes.RTSpecialName
    | TypeAttributes.ClassSemanticsMask // interface or not
    | TypeAttributes.VisibilityMask;

  [Pure]
  private ulong ComputeTypeDefinitionSurfaceHash(TypeDefinition typeDefinition)
  {
    var typeNamespaceHash = GetOrComputeStringHash(typeDefinition.Namespace);
    var typeNameHash = GetOrComputeStringHash(typeDefinition.Name);

    var typeAttributesHash = (ulong)(typeDefinition.Attributes & TypeApiSurfaceAttributes);
    var typeSuperTypesHash = GetSuperTypesHash(out var isValueType);
    var typeTypeParametersHash = GetTypeParametersSurfaceHash(typeDefinition.GetGenericParameters());

    // nested types hash must include a reference to it's containing type
    var typeContainingTypeHandle = typeDefinition.GetDeclaringType();
    var typeContainingTypeUsageHash = !typeContainingTypeHandle.IsNil
      ? GetOrComputeTypeUsageHash(typeContainingTypeHandle) : 0UL;

    using var typeMemberHashes = new SortedHashesSet();
    var apiSurfaceMethods = new HashSet<MethodDefinitionHandle>();

    HashFieldDefinitions();
    HashMethodDefinitions();
    HashPropertyDefinitions();
    HashEventDefinitions();

    var typeCustomAttributesHash = ComputeCustomAttributesSurfaceHash(typeDefinition.GetCustomAttributes());

    var typeHeaderHash = LongHashCode.Combine(
      typeAttributesHash, typeNamespaceHash, typeNameHash, typeTypeParametersHash, typeSuperTypesHash);
    var typeMembersHash = typeMemberHashes.CombineAll();

    return LongHashCode.Combine(typeHeaderHash, typeContainingTypeUsageHash, typeCustomAttributesHash, typeMembersHash);

    ulong GetSuperTypesHash(out bool baseClassIsSystemValueType)
    {
      var typeBaseTypeHash = 0UL;
      using var typeImplementedInterfaceHashes = new SortedHashesSet();
      baseClassIsSystemValueType = false;

      var baseTypeHandle = typeDefinition.BaseType;
      if (!baseTypeHandle.IsNil)
      {
        typeBaseTypeHash = GetOrComputeTypeUsageHash(baseTypeHandle);
        baseClassIsSystemValueType = mySystemValueTypeClassHandle == baseTypeHandle;
      }

      foreach (var interfaceImplementationHandle in typeDefinition.GetInterfaceImplementations())
      {
        var interfaceImplementation = myMetadataReader.GetInterfaceImplementation(interfaceImplementationHandle);

        // skip internal interface implementations
        var topLevelTypeDefinitionHandle = TryGetTopLevelTypeHandleFromPossibleGeneric(interfaceImplementation.Interface);
        if (topLevelTypeDefinitionHandle is { IsNil: false, Kind: HandleKind.TypeDefinition })
        {
          var topLevelTypeDefinition = myMetadataReader.GetTypeDefinition((TypeDefinitionHandle)topLevelTypeDefinitionHandle);
          if (!IsPartOfTheApiSurface(topLevelTypeDefinition))
            continue;
        }

        var interfaceImplementationHash = GetOrComputeTypeUsageHash(interfaceImplementation.Interface);
        var interfaceImplementationCustomAttributesHash = ComputeCustomAttributesSurfaceHash(interfaceImplementation.GetCustomAttributes());

        typeImplementedInterfaceHashes.Add(LongHashCode.Combine(
          interfaceImplementationHash, interfaceImplementationCustomAttributesHash));
      }

      return LongHashCode.Combine(typeBaseTypeHash, typeImplementedInterfaceHashes.CombineAll());
    }

    void HashFieldDefinitions()
    {
      foreach (var fieldDefinitionHandle in typeDefinition.GetFields())
      {
        var fieldDefinition = myMetadataReader.GetFieldDefinition(fieldDefinitionHandle);

        if (IsPartOfTheApiSurface(fieldDefinition.Attributes))
        {
          typeMemberHashes.Add(ComputeFieldDefinitionSurfaceHash(fieldDefinition));
        }

        // for struct declarations we have to include the types of all the instance fields
        // to track breaking changes like definite assignment errors for empty vs. non-empty structs
        // and `unmanaged` generic constraint checking (has managed references or not)
        if (isValueType
            && (fieldDefinition.Attributes & FieldAttributes.Static) == 0)
        {
          typeMemberHashes.Add(GetOrComputeStructFieldTypeHash(fieldDefinition));
        }
      }
    }

    void HashMethodDefinitions()
    {
      foreach (var methodDefinitionHandle in typeDefinition.GetMethods())
      {
        var methodDefinition = myMetadataReader.GetMethodDefinition(methodDefinitionHandle);

        if (IsPartOfTheApiSurface(methodDefinition.Attributes))
        {
          typeMemberHashes.Add(ComputeMethodDefinitionSurfaceHash(methodDefinition));

          if ((methodDefinition.Attributes & MethodAttributes.SpecialName) != 0
              && GetOrComputeStringHash(methodDefinition.Name) != CtorNameHash)
          {
            // property/event accessors
            apiSurfaceMethods.Add(methodDefinitionHandle);
          }
        }
      }
    }

    void HashPropertyDefinitions()
    {
      foreach (var propertyDefinitionHandle in typeDefinition.GetProperties())
      {
        var propertyDefinition = myMetadataReader.GetPropertyDefinition(propertyDefinitionHandle);

        var propertyAccessors = propertyDefinition.GetAccessors();
        if (apiSurfaceMethods.Contains(propertyAccessors.Getter)
            || apiSurfaceMethods.Contains(propertyAccessors.Setter))
        {
          // note: the property type/indexer parameter type is already present in accessor signatures
          var propertyNameHash = GetOrComputeStringHash(propertyDefinition.Name);
          var propertyCustomAttributesHash = ComputeCustomAttributesSurfaceHash(propertyDefinition.GetCustomAttributes());

          typeMemberHashes.Add(LongHashCode.Combine(propertyNameHash, propertyCustomAttributesHash));
        }
      }
    }

    void HashEventDefinitions()
    {
      foreach (var eventDefinitionHandle in typeDefinition.GetEvents())
      {
        var eventDefinition = myMetadataReader.GetEventDefinition(eventDefinitionHandle);

        var eventAccessors = eventDefinition.GetAccessors();
        if (apiSurfaceMethods.Contains(eventAccessors.Adder)
            || apiSurfaceMethods.Contains(eventAccessors.Remover))
        {
          // note: the event type is already present in accessor signatures
          var propertyNameHash = GetOrComputeStringHash(eventDefinition.Name);
          var propertyCustomAttributesHash = ComputeCustomAttributesSurfaceHash(eventDefinition.GetCustomAttributes());

          typeMemberHashes.Add(LongHashCode.Combine(propertyNameHash, propertyCustomAttributesHash));
        }
      }
    }
  }

  [Pure]
  private EntityHandle TryGetTopLevelTypeHandleFromPossibleGeneric(EntityHandle entityHandle)
  {
    switch (entityHandle.Kind)
    {
      case HandleKind.TypeDefinition: // : I
      {
        return (TypeDefinitionHandle)entityHandle;
      }

      case HandleKind.TypeSpecification: // : I<X>
      {
        // light parse of the signature blob
        var interfaceTypeSpecification = myMetadataReader.GetTypeSpecification((TypeSpecificationHandle)entityHandle);
        var signatureBlobReader = myMetadataReader.GetBlobReader(interfaceTypeSpecification.Signature);

        var typeCode = (SignatureTypeCode)signatureBlobReader.ReadCompressedInteger();
        if (typeCode != SignatureTypeCode.GenericTypeInstance) break;

        var genericTypeCode = (SignatureTypeKind)signatureBlobReader.ReadCompressedInteger();
        if (genericTypeCode != SignatureTypeKind.Class) break;

        var typeHandle = signatureBlobReader.ReadTypeHandle();
        if (typeHandle is { IsNil: false, Kind: HandleKind.TypeDefinition or HandleKind.TypeReference })
        {
          return typeHandle;
        }

        break;
      }
    }

    return default;
  }

  [Pure]
  private ulong ComputeFieldDefinitionSurfaceHash(FieldDefinition fieldDefinition)
  {
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

    var signatureHasher = new SignatureHasher<SignatureHasher2>(mySignatureHasher2, myMetadataReader);
    var fieldSignatureBlobReader = myMetadataReader.GetBlobReader(fieldDefinition.Signature);

    var fieldTypeHash2 = signatureHasher.DecodeFieldSignature(ref fieldSignatureBlobReader);
    if (fieldTypeHash2 != fieldTypeHash)
      throw new InvalidOperationException();

    var fieldConstantValueHash = GetOrComputeConstantValueHash(fieldDefinition.GetDefaultValue());

    var fieldCustomAttributesHash = ComputeCustomAttributesSurfaceHash(fieldDefinition.GetCustomAttributes());

    var fieldSignatureHash = LongHashCode.Combine(fieldNameHash, fieldTypeHash, fieldConstantValueHash);

    return LongHashCode.Combine(fieldAttributesHash, fieldSignatureHash, fieldCustomAttributesHash);
  }

  [Pure]
  private ulong ComputeMethodDefinitionSurfaceHash(MethodDefinition methodDefinition)
  {
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
      var parameterCustomAttributesHash = ComputeCustomAttributesSurfaceHash(parameter.GetCustomAttributes());
      var parameterOptionalValueHash = GetOrComputeConstantValueHash(parameter.GetDefaultValue());

      var parameterHash = LongHashCode.Combine(
        parameterNameHash, parameterAttributesHash, parameterCustomAttributesHash, parameterOptionalValueHash);

      methodParametersHash = LongHashCode.Combine(methodParametersHash, parameterHash);
    }

    var methodDecodedSignature = methodDefinition.DecodeSignature(mySignatureHasher, genericContext: null);
    var methodSignatureHash = GetSignatureHash(methodDecodedSignature);

    var methodCombinedSignatureHash = LongHashCode.Combine(
      methodNameHash, methodSignatureHash, methodTypeParametersHash);

    var methodCustomAttributesHash = ComputeCustomAttributesSurfaceHash(methodDefinition.GetCustomAttributes());

    return LongHashCode.Combine(
      methodAttributesHash, methodCombinedSignatureHash, methodParametersHash, methodCustomAttributesHash);
  }

  [Pure]
  private ulong ComputeExportedTypeDefinitionSurfaceHash(ExportedType exportedType)
  {
    var typeAttributesHash = (ulong)(exportedType.Attributes & TypeApiSurfaceAttributes);

    var typeNameHash = GetOrComputeStringHash(exportedType.Name);
    var typeNamespaceHash = GetOrComputeStringHash(exportedType.Namespace);
    var typeCustomAttributesHash = ComputeCustomAttributesSurfaceHash(exportedType.GetCustomAttributes());
    var exportedTypeHash = LongHashCode.Combine(
      typeAttributesHash, typeNameHash, typeNamespaceHash, typeCustomAttributesHash);

    var implementationHandle = exportedType.Implementation;
    switch (implementationHandle.Kind)
    {
      case HandleKind.AssemblyReference:
      {
        var assemblyReferenceHash = GetOrComputeAssemblyReferenceHash((AssemblyReferenceHandle)implementationHandle);

        return LongHashCode.Combine(exportedTypeHash, assemblyReferenceHash);
      }

      case HandleKind.ExportedType:
      {
        var containingExportedType = myMetadataReader.GetExportedType((ExportedTypeHandle)implementationHandle);
        var containingExportedTypeHash = ComputeExportedTypeDefinitionSurfaceHash(containingExportedType);

        return LongHashCode.Combine(exportedTypeHash, containingExportedTypeHash);
      }

      default:
        throw new ArgumentOutOfRangeException();
    }
  }

  #endregion
  #region Type parameters hashing

  [Pure]
  private ulong GetTypeParametersSurfaceHash(GenericParameterHandleCollection typeParameters)
  {
    using var typeParameterHashes = new SortedHashesSet();
    using var typeParameterTypeConstraintsHashes = new SortedHashesSet();

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
        var typeConstraintCustomAttributesHash = ComputeCustomAttributesSurfaceHash(typeConstraint.GetCustomAttributes());

        typeParameterTypeConstraintsHashes.Add(LongHashCode.Combine(
          typeConstraintTypeUsageHash, typeConstraintCustomAttributesHash));
      }

      var typeParameterTypeConstraintsHash = typeParameterTypeConstraintsHashes.CombineAll();
      var typeParameterCustomAttributesHash = ComputeCustomAttributesSurfaceHash(typeParameter.GetCustomAttributes());

      typeParameterHashes.Add(LongHashCode.Combine(
        typeParameterIndexHash, typeParameterAttributesHash, typeParameterTypeConstraintsHash, typeParameterCustomAttributesHash));
    }

    return typeParameterHashes.CombineAll();
  }

  #endregion
  #region Attributes hashing

  private ulong ComputeCustomAttributesSurfaceHash(CustomAttributeHandleCollection customAttributes)
  {
    using var attributeHashes = new SortedHashesSet();

    foreach (var customAttributeHandle in customAttributes)
    {
      var attribute = myMetadataReader.GetCustomAttribute(customAttributeHandle);

      ulong attributeOwnerTypeHash;

      switch (attribute.Constructor.Kind)
      {
        case HandleKind.MemberReference:
        {
          var memberReference = myMetadataReader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);

          var memberParentTypeHandle = memberReference.Parent;
          if (memberParentTypeHandle.Kind == HandleKind.TypeReference)
          {
            attributeOwnerTypeHash = GetOrComputeTypeUsageHash(memberParentTypeHandle);

            if (!IsIncluded(memberParentTypeHandle))
              continue;

            break;
          }

          if (memberParentTypeHandle.Kind == HandleKind.TypeSpecification) // generic attributes
          {
            attributeOwnerTypeHash = GetOrComputeTypeUsageHash(memberParentTypeHandle);

            var topLevelTypeHandle = TryGetTopLevelTypeHandleFromPossibleGeneric(memberParentTypeHandle);
            if (topLevelTypeHandle.IsNil)
              continue;

            if (!IsIncluded(topLevelTypeHandle))
              continue;

            break;
          }

          continue; // something unusual
        }

        case HandleKind.MethodDefinition:
        {
          var methodDefinition = myMetadataReader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
          var typeDefinitionHandle = methodDefinition.GetDeclaringType();

          attributeOwnerTypeHash = GetOrComputeTypeUsageHash(typeDefinitionHandle);

          if (!IsIncluded(typeDefinitionHandle))
            continue;

          break;
        }

        default: throw new BadImageFormatException();
      }

      var attributeConstructorUsageHash = GetOrComputeMemberUsageHash(attribute.Constructor);

      // note: attribute blobs do not have references to metadata tables (type refs are stored as a strings)
      //       so it should be enough to just hash the whole blob
      var attributeBlobReader = myMetadataReader.GetBlobReader(attribute.Value);
      var attributeBlobHash = LongHashCode.FromBlob(attributeBlobReader);

      attributeHashes.Add(LongHashCode.Combine(
        attributeConstructorUsageHash, attributeOwnerTypeHash, attributeBlobHash));
      continue;

      bool IsIncluded(EntityHandle entityHandle)
      {
        if (myIgnoredAttributeTypes.Contains(entityHandle))
          return false; // [CompilerGenerated], etc

        if ((myHasherOptions & AssemblyHasherOptions.IncludeAllAttributes) != 0)
          return true;

        // normally only special attributes are included - those who can affect C# compilation
        // note: we do not check if the typedef represents a public type or not,
        //       since C# emits embedded attributes as internal types
        if (myIncludedAttributeTypes.Contains(entityHandle))
          return true;

        return false;
      }
    }

    return attributeHashes.CombineAll();
  }

  #endregion
  #region Usages hashing

  private ulong GetOrComputeTypeReferenceHash(TypeReferenceHandle handle)
  {
    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var typeReference = myMetadataReader.GetTypeReference(handle);

    var namespaceHash = GetOrComputeStringHash(typeReference.Namespace);
    var nameHash = GetOrComputeStringHash(typeReference.Name);
    CheckAndStoreWellKnownType(namespaceHash, typeReference.Namespace, nameHash, typeReference.Name, handle);

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

  private ulong GetOrComputeMemberUsageHash(EntityHandle handle)
  {
    if (handle.IsNil) throw new ArgumentNullException();

    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    switch (handle.Kind)
    {
      case HandleKind.MemberReference:
      {
        var memberReference = myMetadataReader.GetMemberReference((MemberReferenceHandle)handle);

        var memberNameHash = GetOrComputeStringHash(memberReference.Name);
        var memberReferenceCustomAttributesHash = ComputeCustomAttributesSurfaceHash(memberReference.GetCustomAttributes());

        switch (memberReference.GetKind())
        {
          case MemberReferenceKind.Method:
          {
            var methodSignature = memberReference.DecodeMethodSignature(mySignatureHasher, genericContext: null);
            var methodSignatureHash = GetSignatureHash(methodSignature);
            var methodTypeParametersCountHash = (ulong)methodSignature.GenericParameterCount;

            hash = LongHashCode.Combine(
              memberNameHash, methodSignatureHash, methodTypeParametersCountHash, memberReferenceCustomAttributesHash);
            break;
          }

          case MemberReferenceKind.Field: // note: probably unreachable
          {
            var fieldTypeHash = memberReference.DecodeFieldSignature(mySignatureHasher, genericContext: null);

            hash = LongHashCode.Combine(
              memberNameHash, fieldTypeHash, memberReferenceCustomAttributesHash);
            break;
          }

          default:
            throw new BadImageFormatException();
        }

        break;
      }

      case HandleKind.MethodDefinition:
      {
        var methodDefinition = myMetadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);

        var methodNameHash = GetOrComputeStringHash(methodDefinition.Name);
        var methodSignature = methodDefinition.DecodeSignature(mySignatureHasher, genericContext: null);
        var methodSignatureHash = GetSignatureHash(methodSignature);
        var methodTypeParametersCountHash = (ulong)methodSignature.GenericParameterCount;

        hash = LongHashCode.Combine(
          methodNameHash, methodSignatureHash, methodTypeParametersCountHash);
        break;
      }

      default: throw new ArgumentOutOfRangeException();
    }

    return myHashes[handle] = hash;
  }

  private ulong GetOrComputeTypeUsageHash(EntityHandle handle)
  {
    if (handle.IsNil) throw new ArgumentNullException();

    switch (handle.Kind)
    {
      case HandleKind.TypeReference:
        return GetOrComputeTypeReferenceHash((TypeReferenceHandle)handle);
      case HandleKind.TypeDefinition:
        return GetOrComputeTypeDefinitionUsageHash();
      case HandleKind.TypeSpecification:
        return GetOrComputeTypeSpecificationHash();
      default:
        throw new BadImageFormatException();
    }

    ulong GetOrComputeTypeDefinitionUsageHash()
    {
      if (myHashes.TryGetValue(handle, out var hash)) return hash;

      var typeDefinition = myMetadataReader.GetTypeDefinition((TypeDefinitionHandle)handle);

      // note: only includes FQN of the type definition
      var namespaceHash = GetOrComputeStringHash(typeDefinition.Namespace);
      var nameHash = GetOrComputeStringHash(typeDefinition.Name);
      CheckAndStoreWellKnownType(namespaceHash, typeDefinition.Namespace, nameHash, typeDefinition.Name, handle);

      hash = LongHashCode.Combine(namespaceHash, nameHash);

      return myHashes[handle] = hash;
    }

    ulong GetOrComputeTypeSpecificationHash()
    {
      if (myHashes.TryGetValue(handle, out var hash)) return hash;

      var typeSpecification = myMetadataReader.GetTypeSpecification((TypeSpecificationHandle)handle);

      var typeSpecificationHash = typeSpecification.DecodeSignature(mySignatureHasher, genericContext: null);
      var typeSpecificationCustomAttributesHash = ComputeCustomAttributesSurfaceHash(typeSpecification.GetCustomAttributes());

      hash = LongHashCode.Combine(typeSpecificationHash, typeSpecificationCustomAttributesHash);

      return myHashes[handle] = hash;
    }
  }

  private void CheckAndStoreWellKnownType(
    ulong namespaceHash, StringHandle namespaceHandle, ulong nameHash, StringHandle nameHandle, EntityHandle typeHandle)
  {
    // System.Runtime.CompilerServices
    if (namespaceHash == SystemRuntimeCompilerServicesNamespaceHash
        && myMetadataReader.StringComparer.Equals(namespaceHandle, SystemRuntimeCompilerServicesNamespace))
    {
      // [CompilerGenerated]
      if (nameHash == CompilerGeneratedAttributeNameHash
          && myMetadataReader.StringComparer.Equals(nameHandle, CompilerGeneratedAttributeName))
      {
        // todo: any other excluded?
        myIgnoredAttributeTypes.Add(typeHandle);
      }
      else
      {
        myIncludedAttributeTypes.Add(typeHandle);
      }
    }

    // System
    else if (namespaceHash == SystemNamespaceHash
        && myMetadataReader.StringComparer.Equals(namespaceHandle, SystemNamespace))
    {
      // ValueType
      if (nameHash == ValueTypeClassNameHash
          && myMetadataReader.StringComparer.Equals(nameHandle, ValueTypeClassName))
      {
        if (mySystemValueTypeClassHandle.IsNil)
        {
          mySystemValueTypeClassHandle = typeHandle;
        }
      }

      // System.ParamArrayAttribute
      if (nameHash == ParamArrayAttributeNameHash
          || nameHash == ObsoleteAttributeNameHash
          || nameHash == AttributeUsageAttributeNameHash
          || nameHash == FlagsAttributeNameHash)
      {
        myIncludedAttributeTypes.Add(typeHandle);
      }
    }

    // System.Diagnostics.CodeAnalysis
    else if (namespaceHash == SystemDiagnosticsCodeAnalysisNamespaceHash
             && myMetadataReader.StringComparer.Equals(namespaceHandle, SystemDiagnosticsCodeAnalysisNamespace))
    {
      myIncludedAttributeTypes.Add(typeHandle);
    }
  }

  #endregion
  #region Signature hashing

  private readonly struct SignatureHasher2(AssemblyHasher assemblyHasher) : ISignatureHashProvider
  {
    public ulong HashTypeDefinition(TypeDefinitionHandle handle, byte rawTypeKind)
    {
      return assemblyHasher.GetOrComputeTypeUsageHash(handle);
    }

    public ulong HashTypeReference(TypeReferenceHandle handle, byte rawTypeKind)
    {
      return assemblyHasher.GetOrComputeTypeReferenceHash(handle);
    }
  }

  // todo: replace with non-allocating direct decoder
  private class SignatureHasher(AssemblyHasher surfaceHash)
    : ISignatureTypeProvider<ulong, object?>
  {
    protected readonly AssemblyHasher SurfaceHash = surfaceHash;

    ulong ISimpleTypeProvider<ulong>.GetPrimitiveType(PrimitiveTypeCode typeCode)
    {
      return (ulong)typeCode; // use code itself as a hash
    }

    public virtual ulong GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
      return SurfaceHash.GetOrComputeTypeUsageHash(handle);
    }

    public ulong GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
      return SurfaceHash.GetOrComputeTypeReferenceHash(handle);
    }

    public ulong GetGenericInstantiation(ulong genericTypeHash, ImmutableArray<ulong> typeArgumentsHashes)
    {
      var combine = LongHashCode.Combine(typeArgumentsHashes);
      return LongHashCode.Combine(genericTypeHash, combine);
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

    public ulong GetGenericMethodParameter(object? _, int index) => LongHashCode.Combine((ulong)index, 1000_000);
    public ulong GetGenericTypeParameter(object? _, int index) => LongHashCode.Combine((ulong)index, 1000);

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
  private static ulong GetSignatureHash(MethodSignature<ulong> signature)
  {
    var returnTypeHash = signature.ReturnType;
    var parameterTypesHash = LongHashCode.Combine(signature.ParameterTypes);

    return LongHashCode.Combine(parameterTypesHash, returnTypeHash);
  }

  #endregion
  #region Struct fields hashing

  private ulong GetOrComputeStructFieldTypeHash(FieldDefinition fieldDefinition)
  {
    return fieldDefinition.DecodeSignature(myStructFieldTypesHasher, genericContext: null);
  }

  private ulong GetOrComputeNestedStructFieldTypesHash(TypeDefinitionHandle typeDefinitionHandle)
  {
    if (myStructFieldTypeHashes.TryGetValue(typeDefinitionHandle, out var hash)) return hash;

    var typeDefinition = myMetadataReader.GetTypeDefinition(typeDefinitionHandle);

    var isValueType = false;

    var typeDefinitionBaseType = typeDefinition.BaseType;
    if (!typeDefinitionBaseType.IsNil)
    {
      _ = GetOrComputeTypeUsageHash(typeDefinitionBaseType);
      isValueType = mySystemValueTypeClassHandle == typeDefinitionBaseType;
    }

    if (isValueType) // note: do not include enums
    {
      // in mscorlib `System.Int32` has a field of type `System.Int32`, avoid SO
      myStructFieldTypeHashes[typeDefinitionHandle] = LongHashCode.FnvOffset;

      using var fieldHashes = new SortedHashesSet();

      foreach (var fieldDefinitionHandle in typeDefinition.GetFields())
      {
        // we are only interested in instance fields
        var structFieldDefinition = myMetadataReader.GetFieldDefinition(fieldDefinitionHandle);
        if ((structFieldDefinition.Attributes & FieldAttributes.Static) == 0)
        {
          fieldHashes.Add(GetOrComputeStructFieldTypeHash(structFieldDefinition));
        }
      }

      hash = fieldHashes.CombineAll();
    }
    else
    {
      hash = GetOrComputeTypeUsageHash(typeDefinitionHandle);
    }

    return myStructFieldTypeHashes[typeDefinitionHandle] = hash;
  }

  private readonly struct StructFieldTypesHasher2(AssemblyHasher assemblyHasher) : ISignatureHashProvider
  {
    public ulong HashTypeDefinition(TypeDefinitionHandle handle, byte rawTypeKind)
    {
      return assemblyHasher.GetOrComputeNestedStructFieldTypesHash(handle);
    }

    public ulong HashTypeReference(TypeReferenceHandle handle, byte rawTypeKind)
    {
      return assemblyHasher.GetOrComputeTypeReferenceHash(handle);
    }
  }

  private sealed class StructFieldTypesHasher(AssemblyHasher surfaceHash)
    : SignatureHasher(surfaceHash)
  {
    public override ulong GetTypeFromDefinition(
      MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
      return SurfaceHash.GetOrComputeNestedStructFieldTypesHash(handle);
    }
  }

  #endregion
  #region Resource stream hashing

  [Pure]
  private static unsafe ulong ComputeResourceStreamContentsHash(PEReader peReader, ManifestResource manifestResource)
  {
    var corHeader = peReader.PEHeaders.CorHeader ?? throw new BadImageFormatException();
    var resourcesSection = peReader.GetSectionData(corHeader.ResourcesDirectory.RelativeVirtualAddress);

    var resourcePtr = resourcesSection.Pointer + manifestResource.Offset;
    var resourceLength = *(int*)resourcePtr;
    resourcePtr += sizeof(int);

    // hash entire resource using MD5
    using var md5Hasher = MD5.Create();
    using var resourceStream = new UnmanagedMemoryStream(resourcePtr, resourceLength);

    var hash = md5Hasher.ComputeHash(resourceStream);

    return LongHashCode.FromBlob(hash); // collapse into ulong
  }

  #endregion
  #region Leafs hashing

  private ulong GetOrComputeStringHash(StringHandle handle)
  {
    if (handle.IsNil)
      return LongHashCode.FnvOffset;

    if (!myStringHashes.TryGetValue(handle, out var hash))
    {
      var stringReader = myMetadataReader.GetBlobReader(handle);
      myStringHashes[handle] = hash = LongHashCode.FromBlob(stringReader);
    }

    return hash;
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

  private ulong GetOrComputeAssemblyReferenceHash(AssemblyReferenceHandle handle)
  {
    if (myHashes.TryGetValue(handle, out var hash)) return hash;

    var assemblyReference = myMetadataReader.GetAssemblyReference(handle);

    // System.Private.CoreLib, Version=9.0.0.0, Culture=neutral, PublicKeyToken=7cec85d7bea7798e
    var assemblyNameHash = GetOrComputeStringHash(assemblyReference.Name);
    var assemblyVersionHash = LongHashCode.FromVersion(assemblyReference.Version);
    var assemblyCultureHash = GetOrComputeStringHash(assemblyReference.Culture);
    var assemblyPublicKeyHash = LongHashCode.FromBlob(
      myMetadataReader.GetBlobReader(assemblyReference.PublicKeyOrToken));

    hash = LongHashCode.Combine(assemblyNameHash, assemblyVersionHash, assemblyCultureHash, assemblyPublicKeyHash);

    return myHashes[handle] = hash;
  }

  #endregion
  #region Surface checks

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

  [Pure]
  private bool IsPartOfTheApiSurface(ExportedType exportedType)
  {
    var accessRights = exportedType.Attributes & TypeAttributes.VisibilityMask;

    // nested exported type
    var implementationHandle = exportedType.Implementation;
    if (implementationHandle.Kind == HandleKind.ExportedType)
    {
      var containingExportedType = myMetadataReader.GetExportedType((ExportedTypeHandle)implementationHandle);

      if (!IsPartOfTheApiSurface(containingExportedType))
        return false;
    }

    switch (accessRights)
    {
      case TypeAttributes.Public:
      case TypeAttributes.NestedPublic:
      case TypeAttributes.NestedFamily: // protected
      case TypeAttributes.NestedFamORAssem: // protected internal
      case 0 when myInternalsAreVisible: // internal
      case TypeAttributes.NestedAssembly when myInternalsAreVisible: // internal
      case TypeAttributes.NestedFamANDAssem when myInternalsAreVisible: // private protected
      {
        return true;
      }
    }

    return false;
  }

  [Pure]
  private bool IsPartOfTheApiSurface(ManifestResource manifestResource, AssemblyDefinition assemblyDefinition)
  {
    if ((manifestResource.Attributes & ManifestResourceAttributes.Public) == 0)
      return false;

    var stringComparer = myMetadataReader.StringComparer;
    if (stringComparer.StartsWith(manifestResource.Name, "FSharpSignatureInfo.")
        || stringComparer.StartsWith(manifestResource.Name, "FSharpSignatureData.")
        || stringComparer.StartsWith(manifestResource.Name, "FSharpSignatureCompressedData."))
    {
      // not necessary, but why not
      var resourceName = myMetadataReader.GetString(manifestResource.Name);
      var assemblyName = myMetadataReader.GetString(assemblyDefinition.Name);

      if (resourceName.EndsWith(assemblyName, StringComparison.Ordinal))
        return true;
    }

    return false;
  }

  private void CheckIfInternalsAreExposed(AssemblyDefinition assemblyDefinition)
  {
    foreach (var customAttributeHandle in assemblyDefinition.GetCustomAttributes())
    {
      var customAttribute = myMetadataReader.GetCustomAttribute(customAttributeHandle);
      if (customAttribute.Constructor.Kind != HandleKind.MemberReference) continue;

      var memberReference = myMetadataReader.GetMemberReference((MemberReferenceHandle)customAttribute.Constructor);
      if (memberReference.Parent.Kind != HandleKind.TypeReference) continue;

      var typeReference = myMetadataReader.GetTypeReference((TypeReferenceHandle)memberReference.Parent);

      var nameHash = GetOrComputeStringHash(typeReference.Name);
      if (nameHash == InternalsVisibleToAttributeNameHash
          && GetOrComputeStringHash(typeReference.Namespace) == SystemRuntimeCompilerServicesNamespaceHash
          && myMetadataReader.StringComparer.Equals(typeReference.Name, InternalsVisibleToAttributeName)
          && myMetadataReader.StringComparer.Equals(typeReference.Namespace, SystemRuntimeCompilerServicesNamespace))
      {
        myInternalsAreVisible = true;
      }
    }
  }

  #endregion
  #region Well-known types

  private const string SystemNamespace = "System";
  private const string SystemRuntimeCompilerServicesNamespace = "System.Runtime.CompilerServices";
  private const string SystemDiagnosticsCodeAnalysisNamespace = "System.Diagnostics.CodeAnalysis";

  private const string ValueTypeClassName = nameof(ValueType);
  private const string InternalsVisibleToAttributeName = nameof(InternalsVisibleToAttribute);
  private const string CompilerGeneratedAttributeName = nameof(CompilerGeneratedAttribute);

  private static readonly ulong SystemNamespaceHash
    = LongHashCode.FromUtf8String("System"u8);
  private static readonly ulong SystemRuntimeCompilerServicesNamespaceHash
    = LongHashCode.FromUtf8String("System.Runtime.CompilerServices"u8);
  private static readonly ulong SystemDiagnosticsCodeAnalysisNamespaceHash
    = LongHashCode.FromUtf8String("System.Diagnostics.CodeAnalysis"u8);

  private static readonly ulong ValueTypeClassNameHash
    = LongHashCode.FromUtf8String("ValueType"u8);
  private static readonly ulong ObsoleteAttributeNameHash
    = LongHashCode.FromUtf8String("ObsoleteAttribute"u8);
  private static readonly ulong AttributeUsageAttributeNameHash
    = LongHashCode.FromUtf8String("AttributeUsageAttribute"u8);
  private static readonly ulong FlagsAttributeNameHash
    = LongHashCode.FromUtf8String("FlagsAttribute"u8);
  private static readonly ulong ParamArrayAttributeNameHash
    = LongHashCode.FromUtf8String("ParamArrayAttribute"u8);
  private static readonly ulong InternalsVisibleToAttributeNameHash
    = LongHashCode.FromUtf8String("InternalsVisibleToAttribute"u8);
  private static readonly ulong CompilerGeneratedAttributeNameHash
    = LongHashCode.FromUtf8String("CompilerGeneratedAttribute"u8);
  private static readonly ulong CtorNameHash
    = LongHashCode.FromUtf8String(".ctor"u8);

  #endregion
}

[Flags]
public enum AssemblyHasherOptions
{
  None = 0,
  IncludeAllAttributes = 1 << 0
}

/// Thin abstraction to make list poolable
file readonly struct SortedHashesSet : IDisposable
{
  private readonly List<ulong> myHashes;

  // ReSharper disable once ConvertToPrimaryConstructor
  public SortedHashesSet()
  {
    myHashes = new List<ulong>();
  }

  public void Dispose()
  {
    // can return to pool here
  }

  public void Add(ulong hash)
  {
    myHashes.Add(hash);
  }

  public void Clear()
  {
    myHashes.Clear();
  }

  public ulong CombineAll()
  {
    myHashes.Sort();
    return LongHashCode.Combine(myHashes);
  }
}
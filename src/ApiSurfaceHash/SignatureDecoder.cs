using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;

/// <summary>
/// Decodes signature blobs.
/// See Metadata Specification section II.23.2: Blobs and signatures.
/// </summary>
public readonly struct SignatureDecoder<TType, TGenericContext>
{
  private readonly ISignatureTypeProvider<TType, TGenericContext> myProvider;
  private readonly MetadataReader myMetadataReaderOpt;
  private readonly TGenericContext myGenericContext;

  /// <summary>
  /// Creates a new SignatureDecoder.
  /// </summary>
  /// <param name="provider">The provider used to obtain type symbols as the signature is decoded.</param>
  /// <param name="metadataReader">
  /// The metadata reader from which the signature was obtained. It may be null if the given provider allows it.
  /// </param>
  /// <param name="genericContext">
  /// Additional context needed to resolve generic parameters.
  /// </param>
  public SignatureDecoder(
    ISignatureTypeProvider<TType, TGenericContext> provider,
    MetadataReader metadataReader,
    TGenericContext genericContext)
  {
    myMetadataReaderOpt = metadataReader;
    myProvider = provider;
    myGenericContext = genericContext;
  }

  /// <summary>
  /// Decodes a type embedded in a signature and advances the reader past the type.
  /// </summary>
  /// <param name="blobReader">The blob reader positioned at the leading SignatureTypeCode</param>
  /// <param name="allowTypeSpecifications">Allow a <see cref="TypeSpecificationHandle"/> to follow a (CLASS | VALUETYPE) in the signature.
  /// At present, the only context where that would be valid is in a LocalConstantSig as defined by the Portable PDB specification.
  /// </param>
  /// <returns>The decoded type.</returns>
  /// <exception cref="System.BadImageFormatException">The reader was not positioned at a valid signature type.</exception>
  public TType DecodeType(ref BlobReader blobReader, bool allowTypeSpecifications = false)
  {
    return DecodeType(ref blobReader, allowTypeSpecifications, blobReader.ReadCompressedInteger());
  }

  private TType DecodeType(ref BlobReader blobReader, bool allowTypeSpecifications, int typeCode)
  {
    TType elementType;
    int index;

    switch (typeCode)
    {
      case (int)SignatureTypeCode.Boolean:
      case (int)SignatureTypeCode.Char:
      case (int)SignatureTypeCode.SByte:
      case (int)SignatureTypeCode.Byte:
      case (int)SignatureTypeCode.Int16:
      case (int)SignatureTypeCode.UInt16:
      case (int)SignatureTypeCode.Int32:
      case (int)SignatureTypeCode.UInt32:
      case (int)SignatureTypeCode.Int64:
      case (int)SignatureTypeCode.UInt64:
      case (int)SignatureTypeCode.Single:
      case (int)SignatureTypeCode.Double:
      case (int)SignatureTypeCode.IntPtr:
      case (int)SignatureTypeCode.UIntPtr:
      case (int)SignatureTypeCode.Object:
      case (int)SignatureTypeCode.String:
      case (int)SignatureTypeCode.Void:
      case (int)SignatureTypeCode.TypedReference:
        return myProvider.GetPrimitiveType((PrimitiveTypeCode)typeCode);

      case (int)SignatureTypeCode.Pointer:
        elementType = DecodeType(ref blobReader);
        return myProvider.GetPointerType(elementType);

      case (int)SignatureTypeCode.ByReference:
        elementType = DecodeType(ref blobReader);
        return myProvider.GetByReferenceType(elementType);

      case (int)SignatureTypeCode.Pinned:
        elementType = DecodeType(ref blobReader);
        return myProvider.GetPinnedType(elementType);

      case (int)SignatureTypeCode.SZArray:
        elementType = DecodeType(ref blobReader);
        return myProvider.GetSZArrayType(elementType);

      case (int)SignatureTypeCode.FunctionPointer:
        var methodSignature = DecodeMethodSignature(ref blobReader);
        return myProvider.GetFunctionPointerType(methodSignature);

      case (int)SignatureTypeCode.Array:
        return DecodeArrayType(ref blobReader);

      case (int)SignatureTypeCode.RequiredModifier:
        return DecodeModifiedType(ref blobReader, isRequired: true);

      case (int)SignatureTypeCode.OptionalModifier:
        return DecodeModifiedType(ref blobReader, isRequired: false);

      case (int)SignatureTypeCode.GenericTypeInstance:
        return DecodeGenericTypeInstance(ref blobReader);

      case (int)SignatureTypeCode.GenericTypeParameter:
        index = blobReader.ReadCompressedInteger();
        return myProvider.GetGenericTypeParameter(myGenericContext, index);

      case (int)SignatureTypeCode.GenericMethodParameter:
        index = blobReader.ReadCompressedInteger();
        return myProvider.GetGenericMethodParameter(myGenericContext, index);

      case (int)SignatureTypeKind.Class:
      case (int)SignatureTypeKind.ValueType:
        return DecodeTypeHandle(ref blobReader, (byte)typeCode, allowTypeSpecifications);

      default:
        throw new BadImageFormatException("SR.Format(SR.UnexpectedSignatureTypeCode, typeCode)");
    }
  }

  /// <summary>
  /// Decodes a list of types, with at least one instance that is preceded by its count as a compressed integer.
  /// </summary>
  private ImmutableArray<TType> DecodeTypeSequence(ref BlobReader blobReader)
  {
    var count = blobReader.ReadCompressedInteger();
    if (count == 0)
    {
      // This method is used for Local signatures and method specs, neither of which can have
      // 0 elements. Parameter sequences can have 0 elements, but they are handled separately
      // to deal with the sentinel/varargs case.
      throw new BadImageFormatException("SR.SignatureTypeSequenceMustHaveAtLeastOneElement");
    }

    var types = ImmutableArray.CreateBuilder<TType>(count);

    for (var i = 0; i < count; i++)
    {
      types.Add(DecodeType(ref blobReader));
    }

    return types.MoveToImmutable();
  }

  /// <summary>
  /// Decodes a method (definition, reference, or standalone) or property signature blob.
  /// </summary>
  /// <param name="blobReader">BlobReader positioned at a method signature.</param>
  /// <returns>The decoded method signature.</returns>
  /// <exception cref="System.BadImageFormatException">The method signature is invalid.</exception>
  public MethodSignature<TType> DecodeMethodSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckMethodOrPropertyHeader(header);

    var genericParameterCount = 0;
    if (header.IsGeneric)
    {
      genericParameterCount = blobReader.ReadCompressedInteger();
    }

    var parameterCount = blobReader.ReadCompressedInteger();
    var returnType = DecodeType(ref blobReader);
    ImmutableArray<TType> parameterTypes;
    int requiredParameterCount;

    if (parameterCount == 0)
    {
      requiredParameterCount = 0;
      parameterTypes = ImmutableArray<TType>.Empty;
    }
    else
    {
      var parameterBuilder = ImmutableArray.CreateBuilder<TType>(parameterCount);
      int parameterIndex;

      for (parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
      {
        var typeCode = blobReader.ReadCompressedInteger();
        if (typeCode == (int)SignatureTypeCode.Sentinel)
        {
          break;
        }

        parameterBuilder.Add(DecodeType(ref blobReader, allowTypeSpecifications: false, typeCode: typeCode));
      }

      requiredParameterCount = parameterIndex;
      for (; parameterIndex < parameterCount; parameterIndex++)
      {
        parameterBuilder.Add(DecodeType(ref blobReader));
      }

      parameterTypes = parameterBuilder.MoveToImmutable();
    }

    return new MethodSignature<TType>(header, returnType, requiredParameterCount, genericParameterCount,
      parameterTypes);
  }

  /// <summary>
  /// Decodes a method specification signature blob and advances the reader past the signature.
  /// </summary>
  /// <param name="blobReader">A BlobReader positioned at a valid method specification signature.</param>
  /// <returns>The types used to instantiate a generic method via the method specification.</returns>
  public ImmutableArray<TType> DecodeMethodSpecificationSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckHeader(header, SignatureKind.MethodSpecification);
    return DecodeTypeSequence(ref blobReader);
  }

  /// <summary>
  /// Decodes a local variable signature blob and advances the reader past the signature.
  /// </summary>
  /// <param name="blobReader">The blob reader positioned at a local variable signature.</param>
  /// <returns>The local variable types.</returns>
  /// <exception cref="System.BadImageFormatException">The local variable signature is invalid.</exception>
  public ImmutableArray<TType> DecodeLocalSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckHeader(header, SignatureKind.LocalVariables);
    return DecodeTypeSequence(ref blobReader);
  }

  /// <summary>
  /// Decodes a field signature blob and advances the reader past the signature.
  /// </summary>
  /// <param name="blobReader">The blob reader positioned at a field signature.</param>
  /// <returns>The decoded field type.</returns>
  public TType DecodeFieldSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckHeader(header, SignatureKind.Field);
    return DecodeType(ref blobReader);
  }

  private TType DecodeArrayType(ref BlobReader blobReader)
  {
    // PERF_TODO: Cache/reuse common case of small number of all-zero lower-bounds.

    var elementType = DecodeType(ref blobReader);
    var rank = blobReader.ReadCompressedInteger();
    var sizes = ImmutableArray<int>.Empty;
    var lowerBounds = ImmutableArray<int>.Empty;

    var sizesCount = blobReader.ReadCompressedInteger();
    if (sizesCount > 0)
    {
      var builder = ImmutableArray.CreateBuilder<int>(sizesCount);
      for (var i = 0; i < sizesCount; i++)
      {
        builder.Add(blobReader.ReadCompressedInteger());
      }

      sizes = builder.MoveToImmutable();
    }

    var lowerBoundsCount = blobReader.ReadCompressedInteger();
    if (lowerBoundsCount > 0)
    {
      var builder = ImmutableArray.CreateBuilder<int>(lowerBoundsCount);
      for (var i = 0; i < lowerBoundsCount; i++)
      {
        builder.Add(blobReader.ReadCompressedSignedInteger());
      }

      lowerBounds = builder.MoveToImmutable();
    }

    var arrayShape = new ArrayShape(rank, sizes, lowerBounds);
    return myProvider.GetArrayType(elementType, arrayShape);
  }

  private TType DecodeGenericTypeInstance(ref BlobReader blobReader)
  {
    var genericType = DecodeType(ref blobReader);
    var types = DecodeTypeSequence(ref blobReader);
    return myProvider.GetGenericInstantiation(genericType, types);
  }

  private TType DecodeModifiedType(ref BlobReader blobReader, bool isRequired)
  {
    var modifier = DecodeTypeHandle(ref blobReader, 0, allowTypeSpecifications: true);
    var unmodifiedType = DecodeType(ref blobReader);

    return myProvider.GetModifiedType(modifier, unmodifiedType, isRequired);
  }

  private TType DecodeTypeHandle(ref BlobReader blobReader, byte rawTypeKind, bool allowTypeSpecifications)
  {
    var handle = blobReader.ReadTypeHandle();
    if (!handle.IsNil)
    {
      switch (handle.Kind)
      {
        case HandleKind.TypeDefinition:
          return myProvider.GetTypeFromDefinition(myMetadataReaderOpt, (TypeDefinitionHandle)handle, rawTypeKind);

        case HandleKind.TypeReference:
          return myProvider.GetTypeFromReference(myMetadataReaderOpt, (TypeReferenceHandle)handle, rawTypeKind);

        case HandleKind.TypeSpecification:
          if (!allowTypeSpecifications)
          {
            // To prevent cycles, the token following (CLASS | VALUETYPE) must not be a type spec.
            // https://github.com/dotnet/coreclr/blob/8ff2389204d7c41b17eff0e9536267aea8d6496f/src/md/compiler/mdvalidator.cpp#L6154-L6160
            throw new BadImageFormatException("SR.NotTypeDefOrRefHandle");
          }

          return myProvider.GetTypeFromSpecification(myMetadataReaderOpt, myGenericContext,
            (TypeSpecificationHandle)handle, rawTypeKind);

        default:
          // indicates an error returned from ReadTypeHandle, otherwise unreachable.
          Debug.Assert(handle.IsNil); // will fall through to throw in release.
          break;
      }
    }

    throw new BadImageFormatException("SR.NotTypeDefOrRefOrSpecHandle");
  }

  private static void CheckHeader(SignatureHeader header, SignatureKind expectedKind)
  {
    if (header.Kind != expectedKind)
    {
      throw new BadImageFormatException(
        "SR.Format(SR.UnexpectedSignatureHeader, expectedKind, header.Kind, header.RawValue)");
    }
  }

  private static void CheckMethodOrPropertyHeader(SignatureHeader header)
  {
    var kind = header.Kind;
    if (kind != SignatureKind.Method && kind != SignatureKind.Property)
    {
      throw new BadImageFormatException(
        "SR.Format(SR.UnexpectedSignatureHeader2, SignatureKind.Property, SignatureKind.Method, header.Kind, header.RawValue)");
    }
  }
}
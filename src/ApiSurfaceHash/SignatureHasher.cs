using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.Metadata;

/// <summary>
/// Decodes signature blobs.
/// See Metadata Specification section II.23.2: Blobs and signatures.
/// </summary>
internal struct SignatureHasher<TSignatureHashProvider>
  where TSignatureHashProvider : struct, ISignatureHashProvider
{
  private TSignatureHashProvider myProvider;
  private readonly MetadataReader myMetadataReader;

  /// <summary>Creates a new SignatureDecoder.</summary>
  /// <param name="provider">The provider used to obtain type symbols as the signature is decoded.</param>
  /// <param name="metadataReader">
  /// The metadata reader from which the signature was obtained. It may be null if the given provider allows it.
  /// </param>
  public SignatureHasher(TSignatureHashProvider provider, MetadataReader metadataReader)
  {
    myProvider = provider;
    myMetadataReader = metadataReader;
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
  public ulong DecodeType(ref BlobReader blobReader, bool allowTypeSpecifications = false)
  {
    var typeCode = blobReader.ReadCompressedInteger();
    return HashType(ref blobReader, allowTypeSpecifications, typeCode);
  }

  private ulong HashType(ref BlobReader blobReader, bool allowTypeSpecifications, int typeCode)
  {
    ulong elementTypeHash;
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
      {
        return (ulong)typeCode; // use code itself as a hash
      }

      case (int)SignatureTypeCode.Pointer:
      {
        elementTypeHash = DecodeType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 3);
      }

      case (int)SignatureTypeCode.ByReference:
      {
        elementTypeHash = DecodeType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 2);
      }

      case (int)SignatureTypeCode.Pinned:
      {
        elementTypeHash = DecodeType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 4);
      }

      case (int)SignatureTypeCode.SZArray:
      {
        elementTypeHash = DecodeType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 1);
      }

      case (int)SignatureTypeCode.FunctionPointer:
      {
        var methodSignature = DecodeMethodSignature(ref blobReader);

        var returnTypeHash = methodSignature.ReturnType;
        var parameterTypesHash = LongHashCode.Combine(methodSignature.ParameterTypes);
        var genericParametersCountHash = (ulong)methodSignature.GenericParameterCount;
        var callingConventionHash = (ulong)methodSignature.Header.CallingConvention;

        return LongHashCode.Combine(
          returnTypeHash, parameterTypesHash, genericParametersCountHash, callingConventionHash);
      }

      case (int)SignatureTypeCode.Array:
      {
        return HashArrayType(ref blobReader);
      }

      case (int)SignatureTypeCode.RequiredModifier:
      {
        return HashModifiedType(ref blobReader, isRequired: true);
      }

      case (int)SignatureTypeCode.OptionalModifier:
      {
        return HashModifiedType(ref blobReader, isRequired: false);
      }

      case (int)SignatureTypeCode.GenericTypeInstance:
      {
        return HashGenericTypeInstance(ref blobReader);
      }

      case (int)SignatureTypeCode.GenericTypeParameter:
      {
        index = blobReader.ReadCompressedInteger();

        return LongHashCode.Combine((ulong)index, 1000);
      }

      case (int)SignatureTypeCode.GenericMethodParameter:
      {
        index = blobReader.ReadCompressedInteger();

        return LongHashCode.Combine((ulong)index, 1000_000);
      }

      case (int)SignatureTypeKind.Class:
      case (int)SignatureTypeKind.ValueType:
      {
        return HashTypeHandle(ref blobReader, (byte)typeCode, allowTypeSpecifications);
      }

      default:
        throw new BadImageFormatException("Unexpected signature type code: " + typeCode);
    }
  }

  /// <summary>
  /// Hashes a list of types, with at least one instance that is preceded by its count as a compressed integer.
  /// </summary>
  private ulong HashTypeSequence(ref BlobReader blobReader)
  {
    var count = blobReader.ReadCompressedInteger();
    if (count == 0)
    {
      // This method is used for Local signatures and method specs, neither of which can have
      // 0 elements. Parameter sequences can have 0 elements, but they are handled separately
      // to deal with the sentinel/varargs case.
      throw new BadImageFormatException("Signature type sequence must have at least one element");
    }

    var typesHash = LongHashCode.FnvOffset;

    for (var index = 0; index < count; index++)
    {
      var nextTypeHash = DecodeType(ref blobReader);
      typesHash = LongHashCode.Combine(typesHash, nextTypeHash);
    }

    return typesHash;
  }

  /// <summary>
  /// Decodes a method (definition, reference, or standalone) or property signature blob.
  /// </summary>
  /// <param name="blobReader">BlobReader positioned at a method signature.</param>
  /// <returns>The decoded method signature.</returns>
  /// <exception cref="System.BadImageFormatException">The method signature is invalid.</exception>
  public MethodSignature<ulong> DecodeMethodSignature(ref BlobReader blobReader)
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
    ImmutableArray<ulong> parameterTypes;
    int requiredParameterCount;

    if (parameterCount == 0)
    {
      requiredParameterCount = 0;
      parameterTypes = ImmutableArray<ulong>.Empty;
    }
    else
    {
      var parameterBuilder = ImmutableArray.CreateBuilder<ulong>(parameterCount);
      int parameterIndex;

      for (parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
      {
        var typeCode = blobReader.ReadCompressedInteger();
        if (typeCode == (int)SignatureTypeCode.Sentinel)
        {
          break;
        }

        parameterBuilder.Add(HashType(ref blobReader, allowTypeSpecifications: false, typeCode: typeCode));
      }

      requiredParameterCount = parameterIndex;
      for (; parameterIndex < parameterCount; parameterIndex++)
      {
        parameterBuilder.Add(DecodeType(ref blobReader));
      }

      parameterTypes = parameterBuilder.MoveToImmutable();
    }

    return new MethodSignature<ulong>(header, returnType, requiredParameterCount, genericParameterCount,
      parameterTypes);
  }

  /// <summary>
  /// Decodes a method specification signature blob and advances the reader past the signature.
  /// </summary>
  /// <param name="blobReader">A BlobReader positioned at a valid method specification signature.</param>
  /// <returns>The types used to instantiate a generic method via the method specification.</returns>
  public ulong DecodeMethodSpecificationSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckHeader(header, SignatureKind.MethodSpecification);
    return HashTypeSequence(ref blobReader);
  }

  /// <summary>
  /// Decodes a local variable signature blob and advances the reader past the signature.
  /// </summary>
  /// <param name="blobReader">The blob reader positioned at a local variable signature.</param>
  /// <returns>The local variable types.</returns>
  /// <exception cref="System.BadImageFormatException">The local variable signature is invalid.</exception>
  public ulong DecodeLocalSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckHeader(header, SignatureKind.LocalVariables);
    return HashTypeSequence(ref blobReader);
  }

  /// <summary>
  /// Decodes a field signature blob and advances the reader past the signature.
  /// </summary>
  /// <param name="blobReader">The blob reader positioned at a field signature.</param>
  /// <returns>The decoded field type.</returns>
  public ulong DecodeFieldSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckHeader(header, SignatureKind.Field);
    return DecodeType(ref blobReader);
  }

  private ulong HashArrayType(ref BlobReader blobReader)
  {
    var elementTypeHash = DecodeType(ref blobReader);
    var rank = blobReader.ReadCompressedInteger();
    var sizesHash = LongHashCode.FnvOffset;
    var lowerBoundsHash = LongHashCode.FnvOffset;

    var sizesCount = blobReader.ReadCompressedInteger();
    if (sizesCount > 0)
    {
      for (var index = 0; index < sizesCount; index++)
      {
        var size = blobReader.ReadCompressedInteger();
        sizesHash = LongHashCode.Combine(sizesHash, (ulong)size);
      }
    }

    var lowerBoundsCount = blobReader.ReadCompressedInteger();
    if (lowerBoundsCount > 0)
    {
      for (var index = 0; index < lowerBoundsCount; index++)
      {
        var lowerBound = blobReader.ReadCompressedSignedInteger();
        lowerBoundsHash = LongHashCode.Combine(lowerBoundsHash, (ulong)lowerBound);
      }
    }

    return LongHashCode.Combine(
      elementTypeHash,
      (ulong)rank,
      lowerBoundsHash,
      sizesHash);
  }

  private ulong HashGenericTypeInstance(ref BlobReader blobReader)
  {
    var genericTypeHash = DecodeType(ref blobReader);
    var typeArgumentsHash = HashTypeSequence(ref blobReader);

    return LongHashCode.Combine(genericTypeHash, typeArgumentsHash);
  }

  private ulong HashModifiedType(ref BlobReader blobReader, bool isRequired)
  {
    var modifierHash = HashTypeHandle(ref blobReader, 0, allowTypeSpecifications: true);
    var unmodifiedTypeHash = DecodeType(ref blobReader);

    // note: `ref readonly` returns are encoded via T& modreq([InAttribute])
    return LongHashCode.Combine(unmodifiedTypeHash, modifierHash, isRequired ? 42UL : 0UL);
  }

  private ulong HashTypeHandle(ref BlobReader blobReader, byte rawTypeKind, bool allowTypeSpecifications)
  {
    var handle = blobReader.ReadTypeHandle();
    if (!handle.IsNil)
    {
      switch (handle.Kind)
      {
        case HandleKind.TypeDefinition:
        {
          return myProvider.HashTypeDefinition((TypeDefinitionHandle)handle, rawTypeKind);
        }

        case HandleKind.TypeReference:
        {
          return myProvider.HashTypeReference((TypeReferenceHandle)handle, rawTypeKind);
        }

        case HandleKind.TypeSpecification:
        {
          if (!allowTypeSpecifications)
          {
            // To prevent cycles, the token following (CLASS | VALUETYPE) must not be a type spec.
            // https://github.com/dotnet/coreclr/blob/8ff2389204d7c41b17eff0e9536267aea8d6496f/src/md/compiler/mdvalidator.cpp#L6154-L6160
            throw new BadImageFormatException("Not typedef or typeref handle");
          }

          // note: normally should be unreachable
          var typeSpecification = myMetadataReader.GetTypeSpecification((TypeSpecificationHandle)handle);
          var typeSpecBlobReader = myMetadataReader.GetBlobReader(typeSpecification.Signature);
          return DecodeType(ref typeSpecBlobReader);
        }

        default:
        {
          // indicates an error returned from ReadTypeHandle, otherwise unreachable.
          Debug.Assert(handle.IsNil); // will fall through to throw in release.
          break;
        }
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

public interface ISignatureHashProvider
{
  ulong HashTypeDefinition(TypeDefinitionHandle handle, byte rawTypeKind);
  ulong HashTypeReference(TypeReferenceHandle handle, byte rawTypeKind);
}
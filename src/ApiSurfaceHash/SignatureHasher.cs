using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Reflection.Metadata;

/// Decodes and hashes signature blobs.
/// Zero-allocating version of the 'SignatureDecoder' type.
/// See Metadata Specification section II.23.2: Blobs and signatures
internal struct SignatureHasher<TSignatureHashProvider>
  where TSignatureHashProvider : struct, ITypeUsageHashProvider
{
  private TSignatureHashProvider myProvider;
  private readonly MetadataReader myMetadataReader;

  /// Creates a new SignatureHasher
  public SignatureHasher(TSignatureHashProvider provider, MetadataReader metadataReader)
  {
    myProvider = provider;
    myMetadataReader = metadataReader;
  }

  [Pure]
  public ulong HashType(BlobHandle blobHandle)
  {
    var blobReader = myMetadataReader.GetBlobReader(blobHandle);

    return HashType(ref blobReader);
  }

  private ulong HashType(ref BlobReader blobReader, bool allowTypeSpecifications = false)
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
        elementTypeHash = HashType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 3);
      }

      case (int)SignatureTypeCode.ByReference:
      {
        elementTypeHash = HashType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 2);
      }

      case (int)SignatureTypeCode.Pinned:
      {
        elementTypeHash = HashType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 4);
      }

      case (int)SignatureTypeCode.SZArray:
      {
        elementTypeHash = HashType(ref blobReader);
        return LongHashCode.Combine(elementTypeHash, 1);
      }

      case (int)SignatureTypeCode.FunctionPointer:
      {
        var methodSignature = HashMethodSignature(ref blobReader);

        var returnTypeHash = methodSignature.ReturnTypeHash;
        var parameterTypesHash = methodSignature.ParameterTypesHash;
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
        return HashTypeHandle(ref blobReader, allowTypeSpecifications);
      }

      default:
        throw new BadImageFormatException("Unexpected signature type code: " + typeCode);
    }
  }

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
      var nextTypeHash = HashType(ref blobReader);
      typesHash = LongHashCode.Combine(typesHash, nextTypeHash);
    }

    return typesHash;
  }

  /// Decodes and hashes a method (definition, reference, or standalone) or property signature blob
  [Pure] public MethodSignatureHash HashMethodSignature(BlobHandle signatureBlob)
  {
    var blobReader = myMetadataReader.GetBlobReader(signatureBlob);

    return HashMethodSignature(ref blobReader);
  }

  private MethodSignatureHash HashMethodSignature(ref BlobReader blobReader)
  {
    var header = blobReader.ReadSignatureHeader();
    CheckMethodOrPropertyHeader(header);

    var genericParameterCount = 0;
    if (header.IsGeneric)
    {
      genericParameterCount = blobReader.ReadCompressedInteger();
    }

    var parameterCount = blobReader.ReadCompressedInteger();
    var returnType = HashType(ref blobReader);
    var parameterTypesHash = LongHashCode.FnvOffset;

    if (parameterCount != 0)
    {
      int parameterIndex;

      for (parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
      {
        var typeCode = blobReader.ReadCompressedInteger();
        if (typeCode == (int)SignatureTypeCode.Sentinel) continue;

        var parameterTypeHash = HashType(ref blobReader, allowTypeSpecifications: false, typeCode: typeCode);
        parameterTypesHash = LongHashCode.Combine(parameterTypesHash, parameterTypeHash);
      }
    }

    return new MethodSignatureHash(header, returnType, genericParameterCount, parameterTypesHash);
  }

  /// Decodes and hashes a field signature blob and advances the reader past the signature.
  [Pure] public ulong HashFieldSignature(BlobHandle signatureBlob)
  {
    var blobReader = myMetadataReader.GetBlobReader(signatureBlob);

    var header = blobReader.ReadSignatureHeader();
    CheckHeader(header, SignatureKind.Field);
    return HashType(ref blobReader);
  }

  private ulong HashArrayType(ref BlobReader blobReader)
  {
    var elementTypeHash = HashType(ref blobReader);
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
    var genericTypeHash = HashType(ref blobReader);
    var typeArgumentsHash = HashTypeSequence(ref blobReader);

    return LongHashCode.Combine(genericTypeHash, typeArgumentsHash);
  }

  private ulong HashModifiedType(ref BlobReader blobReader, bool isRequired)
  {
    var modifierHash = HashTypeHandle(ref blobReader, allowTypeSpecifications: true);
    var unmodifiedTypeHash = HashType(ref blobReader);

    // note: `ref readonly` returns are encoded via T& modreq([InAttribute])
    return LongHashCode.Combine(unmodifiedTypeHash, modifierHash, isRequired ? 42UL : 0UL);
  }

  private ulong HashTypeHandle(ref BlobReader blobReader, bool allowTypeSpecifications)
  {
    var handle = blobReader.ReadTypeHandle();
    if (!handle.IsNil)
    {
      switch (handle.Kind)
      {
        case HandleKind.TypeDefinition:
        {
          return myProvider.HashTypeDefinition((TypeDefinitionHandle)handle);
        }

        case HandleKind.TypeReference:
        {
          return myProvider.HashTypeReference((TypeReferenceHandle)handle);
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
          return HashType(ref typeSpecBlobReader);
        }

        default:
        {
          // indicates an error returned from ReadTypeHandle, otherwise unreachable.
          Debug.Assert(handle.IsNil); // will fall through to throw in release.
          break;
        }
      }
    }

    throw new BadImageFormatException("Not typedef, typeref or typespec handle");
  }

  private static void CheckHeader(SignatureHeader header, SignatureKind expectedKind)
  {
    if (header.Kind != expectedKind)
    {
      throw new BadImageFormatException(
        $"Unexpected signature header: {expectedKind}, {header.Kind}, {header.RawValue})");
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

public readonly struct MethodSignatureHash(
  SignatureHeader header,
  ulong returnTypeHash,
  int genericParameterCount,
  ulong parameterTypesHash)
{
  public SignatureHeader Header { get; } = header;
  public ulong ReturnTypeHash { get; } = returnTypeHash;
  public int GenericParameterCount { get; } = genericParameterCount;
  public ulong ParameterTypesHash { get; } = parameterTypesHash;
}

public interface ITypeUsageHashProvider
{
  ulong HashTypeDefinition(TypeDefinitionHandle handle);
  ulong HashTypeReference(TypeReferenceHandle handle);
}
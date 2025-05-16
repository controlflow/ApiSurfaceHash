using System.Collections.Immutable;
using System.Reflection.Metadata;

// Hashes custom attribute blob
internal readonly struct CustomAttributeDecoder<TType>
{
  private readonly MetadataReader myReader;

  public CustomAttributeDecoder(MetadataReader reader)
  {
    myReader = reader;
  }

  public ulong DecodeValue(EntityHandle constructor, BlobHandle value)
  {
    BlobHandle signature;
    BlobHandle attributeOwningTypeSpec = default;
    switch (constructor.Kind)
    {
      case HandleKind.MethodDefinition:
      {
        var methodDefinition = myReader.GetMethodDefinition((MethodDefinitionHandle)constructor);
        signature = methodDefinition.Signature;
        break;
      }

      case HandleKind.MemberReference:
      {
        var memberReference = myReader.GetMemberReference((MemberReferenceHandle)constructor);
        signature = memberReference.Signature;

        // If this is a generic attribute, we'll need its instantiation to decode the signatures
        if (memberReference.Parent.Kind == HandleKind.TypeSpecification)
        {
          var genericOwner =
            myReader.GetTypeSpecification((TypeSpecificationHandle)memberReference.Parent);
          attributeOwningTypeSpec = genericOwner.Signature;
        }

        break;
      }

      default:
        throw new BadImageFormatException();
    }

    var signatureReader = myReader.GetBlobReader(signature);
    var valueReader = myReader.GetBlobReader(value);

    var prolog = valueReader.ReadUInt16();
    if (prolog != 1)
    {
      throw new BadImageFormatException();
    }

    var header = signatureReader.ReadSignatureHeader();
    if (header.Kind != SignatureKind.Method || header.IsGeneric)
    {
      throw new BadImageFormatException();
    }

    var parameterCount = signatureReader.ReadCompressedInteger();
    var returnType = signatureReader.ReadSignatureTypeCode();
    if (returnType != SignatureTypeCode.Void)
    {
      throw new BadImageFormatException();
    }

    BlobReader genericContextReader = default;
    if (!attributeOwningTypeSpec.IsNil)
    {
      // If this is a generic attribute, grab the instantiation arguments so that we can
      // interpret the constructor signature, should it refer to the generic context.
      genericContextReader = myReader.GetBlobReader(attributeOwningTypeSpec);
      if (genericContextReader.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
      {
        var kind = genericContextReader.ReadCompressedInteger();
        if (kind != (int)SignatureTypeKind.Class && kind != (int)SignatureTypeKind.ValueType)
        {
          throw new BadImageFormatException();
        }

        genericContextReader.ReadTypeHandle();

        // At this point, the reader points to the "GenArgCount Type Type*" part of the signature.
      }
      else
      {
        // Some other invalid TypeSpec. Don't accidentally allow resolving generic parameters
        // from the constructor signature into a broken blob.
        genericContextReader = default;
      }
    }

    DecodeFixedArguments(ref signatureReader, ref valueReader, parameterCount, genericContextReader);
    DecodeNamedArguments(ref valueReader);

    return 0;
  }

  private ImmutableArray<CustomAttributeTypedArgument<TType>> DecodeFixedArguments(ref BlobReader signatureReader,
    ref BlobReader valueReader, int count, BlobReader genericContextReader)
  {
    if (count == 0)
    {
      return ImmutableArray<CustomAttributeTypedArgument<TType>>.Empty;
    }

    var arguments = ImmutableArray.CreateBuilder<CustomAttributeTypedArgument<TType>>(count);

    for (var index = 0; index < count; index++)
    {
      var info = DecodeFixedArgumentType(ref signatureReader, genericContextReader);
      arguments.Add(DecodeArgument(ref valueReader, info));
    }

    return arguments.MoveToImmutable();
  }

  private ImmutableArray<CustomAttributeNamedArgument<TType>> DecodeNamedArguments(ref BlobReader valueReader)
  {
    int count = valueReader.ReadUInt16();
    if (count == 0)
    {
      return ImmutableArray<CustomAttributeNamedArgument<TType>>.Empty;
    }

    var arguments = ImmutableArray.CreateBuilder<CustomAttributeNamedArgument<TType>>(count);
    for (var i = 0; i < count; i++)
    {
      var kind = (CustomAttributeNamedArgumentKind)valueReader.ReadSerializationTypeCode();
      if (kind != CustomAttributeNamedArgumentKind.Field && kind != CustomAttributeNamedArgumentKind.Property)
      {
        throw new BadImageFormatException();
      }

      var info = DecodeNamedArgumentType(ref valueReader);
      var name = valueReader.ReadSerializedString();
      var argument = DecodeArgument(ref valueReader, info);
      arguments.Add(new CustomAttributeNamedArgument<TType>(name, kind, argument.Type, argument.Value));
    }

    return arguments.MoveToImmutable();
  }

  private struct ArgumentTypeInfo
  {
    //public TType Type;
    //public TType ElementType;
    public SerializationTypeCode TypeCode;
    public SerializationTypeCode ElementTypeCode;
  }

  // Decodes a fixed argument type of a custom attribute from its constructor signature.
  //
  // Note that we do not decode the full constructor signature using DecodeMethodSignature
  // but instead decode one parameter at a time as we read the value blob. This is both
  // better perf-wise, but even more important is that we can't actually reason about
  // a method signature with opaque TType values without adding some unnecessary chatter
  // with the provider.
  private ArgumentTypeInfo DecodeFixedArgumentType(
    ref BlobReader signatureReader, BlobReader genericContextReader, bool isElementType = false)
  {
    var signatureTypeCode = signatureReader.ReadSignatureTypeCode();

    var info = new ArgumentTypeInfo
    {
      TypeCode = (SerializationTypeCode)signatureTypeCode,
    };

    switch (signatureTypeCode)
    {
      case SignatureTypeCode.Boolean:
      case SignatureTypeCode.Byte:
      case SignatureTypeCode.Char:
      case SignatureTypeCode.Double:
      case SignatureTypeCode.Int16:
      case SignatureTypeCode.Int32:
      case SignatureTypeCode.Int64:
      case SignatureTypeCode.SByte:
      case SignatureTypeCode.Single:
      case SignatureTypeCode.String:
      case SignatureTypeCode.UInt16:
      case SignatureTypeCode.UInt32:
      case SignatureTypeCode.UInt64:
      {
        //info.Type = _provider.GetPrimitiveType((PrimitiveTypeCode)signatureTypeCode);
        break;
      }

      case SignatureTypeCode.Object:
      {
        info.TypeCode = SerializationTypeCode.TaggedObject;
        //info.Type = _provider.GetPrimitiveType(PrimitiveTypeCode.Object);
        break;
      }

      case SignatureTypeCode.TypeHandle:
      {
        // Parameter is type def or ref and is only allowed to be System.Type or Enum.
        var handle = signatureReader.ReadTypeHandle();
        //info.Type = GetTypeFromHandle(handle);
        //info.TypeCode = _provider.IsSystemType(info.Type)
        //  ? SerializationTypeCode.Type
        //  : (SerializationTypeCode)_provider.GetUnderlyingEnumType(info.Type);
        break;
      }

      case SignatureTypeCode.SZArray:
        if (isElementType)
        {
          // jagged arrays are not allowed.
          throw new BadImageFormatException();
        }

        var elementInfo = DecodeFixedArgumentType(ref signatureReader, genericContextReader, isElementType: true);
        //info.ElementType = elementInfo.Type;
        info.ElementTypeCode = elementInfo.TypeCode;
        //info.Type = _provider.GetSZArrayType(info.ElementType);
        break;

      case SignatureTypeCode.GenericTypeParameter:
        if (genericContextReader.Length == 0)
        {
          throw new BadImageFormatException();
        }

        var parameterIndex = signatureReader.ReadCompressedInteger();
        var numGenericParameters = genericContextReader.ReadCompressedInteger();
        if (parameterIndex >= numGenericParameters)
        {
          throw new BadImageFormatException();
        }

        while (parameterIndex > 0)
        {
          SkipType(ref genericContextReader);
          parameterIndex--;
        }

        return DecodeFixedArgumentType(ref genericContextReader, default, isElementType);

      default:
        throw new BadImageFormatException();
    }

    return info;
  }

  private ArgumentTypeInfo DecodeNamedArgumentType(ref BlobReader valueReader, bool isElementType = false)
  {
    var info = new ArgumentTypeInfo
    {
      TypeCode = valueReader.ReadSerializationTypeCode(),
    };

    switch (info.TypeCode)
    {
      case SerializationTypeCode.Boolean:
      case SerializationTypeCode.Byte:
      case SerializationTypeCode.Char:
      case SerializationTypeCode.Double:
      case SerializationTypeCode.Int16:
      case SerializationTypeCode.Int32:
      case SerializationTypeCode.Int64:
      case SerializationTypeCode.SByte:
      case SerializationTypeCode.Single:
      case SerializationTypeCode.String:
      case SerializationTypeCode.UInt16:
      case SerializationTypeCode.UInt32:
      case SerializationTypeCode.UInt64:
        //info.Type = _provider.GetPrimitiveType((PrimitiveTypeCode)info.TypeCode);
        break;

      case SerializationTypeCode.Type:
        //info.Type = _provider.GetSystemType();
        break;

      case SerializationTypeCode.TaggedObject:
        //info.Type = _provider.GetPrimitiveType(PrimitiveTypeCode.Object);
        break;

      case SerializationTypeCode.SZArray:
        if (isElementType)
        {
          // jagged arrays are not allowed.
          throw new BadImageFormatException();
        }

        var elementInfo = DecodeNamedArgumentType(ref valueReader, isElementType: true);
        //info.ElementType = elementInfo.Type;
        info.ElementTypeCode = elementInfo.TypeCode;
        //info.Type = _provider.GetSZArrayType(info.ElementType);
        break;

      case SerializationTypeCode.Enum:
        var typeName = valueReader.ReadSerializedString();
        //info.Type = _provider.GetTypeFromSerializedName(typeName!);
        //info.TypeCode = (SerializationTypeCode)_provider.GetUnderlyingEnumType(info.Type);
        break;

      default:
        throw new BadImageFormatException();
    }

    return info;
  }

  private CustomAttributeTypedArgument<TType> DecodeArgument(ref BlobReader valueReader, ArgumentTypeInfo info)
  {
    if (info.TypeCode == SerializationTypeCode.TaggedObject)
    {
      info = DecodeNamedArgumentType(ref valueReader);
    }

    // PERF_TODO: https://github.com/dotnet/runtime/issues/16551
    //   Cache /reuse common arguments to avoid boxing (small integers, true, false).
    object? value;
    switch (info.TypeCode)
    {
      case SerializationTypeCode.Boolean:
        value = valueReader.ReadBoolean();
        break;

      case SerializationTypeCode.Byte:
        value = valueReader.ReadByte();
        break;

      case SerializationTypeCode.Char:
        value = valueReader.ReadChar();
        break;

      case SerializationTypeCode.Double:
        value = valueReader.ReadDouble();
        break;

      case SerializationTypeCode.Int16:
        value = valueReader.ReadInt16();
        break;

      case SerializationTypeCode.Int32:
        value = valueReader.ReadInt32();
        break;

      case SerializationTypeCode.Int64:
        value = valueReader.ReadInt64();
        break;

      case SerializationTypeCode.SByte:
        value = valueReader.ReadSByte();
        break;

      case SerializationTypeCode.Single:
        value = valueReader.ReadSingle();
        break;

      case SerializationTypeCode.UInt16:
        value = valueReader.ReadUInt16();
        break;

      case SerializationTypeCode.UInt32:
        value = valueReader.ReadUInt32();
        break;

      case SerializationTypeCode.UInt64:
        value = valueReader.ReadUInt64();
        break;

      case SerializationTypeCode.String:
        value = valueReader.ReadSerializedString();
        break;

      case SerializationTypeCode.Type:
        var typeName = valueReader.ReadSerializedString();
        //value = _provider.GetTypeFromSerializedName(typeName!);
        value = null!;
        break;

      case SerializationTypeCode.SZArray:
        value = DecodeArrayArgument(ref valueReader, info);
        break;

      default:
        throw new BadImageFormatException();
    }

    return new CustomAttributeTypedArgument<TType>(default!, value);
  }

  private ImmutableArray<CustomAttributeTypedArgument<TType>>? DecodeArrayArgument(ref BlobReader blobReader,
    ArgumentTypeInfo info)
  {
    var count = blobReader.ReadInt32();
    if (count == -1)
    {
      return null;
    }

    if (count == 0)
    {
      return ImmutableArray<CustomAttributeTypedArgument<TType>>.Empty;
    }

    if (count < 0)
    {
      throw new BadImageFormatException();
    }

    var elementInfo = new ArgumentTypeInfo
    {
      //Type = info.ElementType,
      TypeCode = info.ElementTypeCode,
    };

    var array = ImmutableArray.CreateBuilder<CustomAttributeTypedArgument<TType>>(count);

    for (var index = 0; index < count; index++)
    {
      array.Add(DecodeArgument(ref blobReader, elementInfo));
    }

    return array.MoveToImmutable();
  }

  // private TType GetTypeFromHandle(EntityHandle handle)
  // {
  //   return handle.Kind switch
  //   {
  //     HandleKind.TypeDefinition => _provider.GetTypeFromDefinition(_reader, (TypeDefinitionHandle)handle, 0),
  //     HandleKind.TypeReference => _provider.GetTypeFromReference(_reader, (TypeReferenceHandle)handle, 0),
  //     _ => throw new BadImageFormatException("SR.NotTypeDefOrRefHandle"),
  //   };
  // }

  private static void SkipType(ref BlobReader blobReader)
  {
    var typeCode = blobReader.ReadCompressedInteger();

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
        return;

      case (int)SignatureTypeCode.Pointer:
      case (int)SignatureTypeCode.ByReference:
      case (int)SignatureTypeCode.Pinned:
      case (int)SignatureTypeCode.SZArray:
        SkipType(ref blobReader);
        return;

      case (int)SignatureTypeCode.FunctionPointer:
        var header = blobReader.ReadSignatureHeader();
        if (header.IsGeneric)
        {
          blobReader.ReadCompressedInteger(); // arity
        }

        var paramCount = blobReader.ReadCompressedInteger();
        SkipType(ref blobReader);
        for (var i = 0; i < paramCount; i++)
          SkipType(ref blobReader);
        return;

      case (int)SignatureTypeCode.Array:
        SkipType(ref blobReader);
        blobReader.ReadCompressedInteger(); // rank
        var boundsCount = blobReader.ReadCompressedInteger();
        for (var i = 0; i < boundsCount; i++)
        {
          blobReader.ReadCompressedInteger();
        }

        var lowerBoundsCount = blobReader.ReadCompressedInteger();
        for (var i = 0; i < lowerBoundsCount; i++)
        {
          blobReader.ReadCompressedSignedInteger();
        }

        return;

      case (int)SignatureTypeCode.RequiredModifier:
      case (int)SignatureTypeCode.OptionalModifier:
        blobReader.ReadTypeHandle();
        SkipType(ref blobReader);
        return;

      case (int)SignatureTypeCode.GenericTypeInstance:
        SkipType(ref blobReader);
        var count = blobReader.ReadCompressedInteger();
        for (var i = 0; i < count; i++)
        {
          SkipType(ref blobReader);
        }

        return;

      case (int)SignatureTypeCode.GenericTypeParameter:
        blobReader.ReadCompressedInteger();
        return;

      case (int)SignatureTypeKind.Class:
      case (int)SignatureTypeKind.ValueType:
        SkipType(ref blobReader);
        break;

      default:
        throw new BadImageFormatException();
    }
  }
}
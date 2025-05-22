using System.Collections.Immutable;
using System.Diagnostics.Contracts;
using System.Reflection.Metadata;

internal static class LongHashCode
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
        hash = hash * FnvPrime ^ bytePtr[index];
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
        hash = hash * FnvPrime ^ itemHash;
      }

      return hash;
    }
  }

  [Pure]
  public static ulong Combine(ImmutableArray<ulong> hashes)
  {
    unchecked
    {
      var hash = FnvOffset;

      foreach (var itemHash in hashes)
      {
        hash = hash * FnvPrime ^ itemHash;
      }

      return hash;
    }
  }

  [Pure]
  public static ulong Combine(ulong a, ulong b)
  {
    return unchecked(a * FnvPrime ^ b);
  }

  [Pure]
  public static ulong Combine(ulong a, ulong b, ulong c)
  {
    return unchecked((a * FnvPrime ^ b) * FnvPrime ^ c);
  }

  [Pure]
  public static ulong Combine(ulong a, ulong b, ulong c, ulong d)
  {
    return unchecked(((a * FnvPrime ^ b) * FnvPrime ^ c) * FnvPrime ^ d);
  }

  [Pure]
  public static ulong Combine(ulong a, ulong b, ulong c, ulong d, ulong e)
  {
    return unchecked((((a * FnvPrime ^ b) * FnvPrime ^ c) * FnvPrime ^ d) * FnvPrime ^ e);
  }
}
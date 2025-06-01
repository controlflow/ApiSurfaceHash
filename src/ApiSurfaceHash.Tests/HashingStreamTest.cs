using System.Security.Cryptography;
using NUnit.Framework;

namespace ApiSurfaceHash.Tests;

[TestFixture]
public class HashingStreamTest
{
  [Test]
  public void TestStreaming()
  {
    var stream = new HashingWriteOnlyStream(MD5.Create());

    stream.WriteByte(1);
    stream.WriteByte(2);
    stream.WriteByte(3);
    stream.Write([7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);

    var bytes = stream.GetHash();

    stream.WriteByte(1);
    stream.Write([2, 3]);
    stream.Write([]);
    stream.Write([7]);
    stream.Write([8, 9, 10, 11, 12, 13, 14, 15, 16]);

    var bytes2 = stream.GetHash();
    Assert.That(bytes, Is.EqualTo(bytes2));
  }
}

public sealed class HashingWriteOnlyStream : Stream
{
  private readonly HashAlgorithm myHashAlgorithm;

  public HashingWriteOnlyStream(HashAlgorithm hashAlgorithm)
  {
    myHashAlgorithm = hashAlgorithm;
    myHashAlgorithm.Initialize();
  }

  public override bool CanRead => false;
  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override bool CanSeek => false;
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

  public override void Flush() { }
  public override void SetLength(long value) => throw new NotSupportedException();

  public override long Length => throw new NotSupportedException();

  public override long Position
  {
    get => throw new NotSupportedException();
    set => throw new NotSupportedException();
  }

  public override bool CanWrite => true;

  public override void Write(byte[] buffer, int offset, int count)
  {
    myHashAlgorithm.TransformBlock(buffer, offset, count, null, 0);
  }

  public byte[] GetHash()
  {
    myHashAlgorithm.TransformFinalBlock([], 0, 0);
    return myHashAlgorithm.Hash!;
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
      myHashAlgorithm.Dispose();

    base.Dispose(disposing);
  }
}
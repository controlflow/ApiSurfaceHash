using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace ApiSurfaceHash.Tests;

[TestFixture]
public class SmokeTests
{
  [Test]
  public void TestBasicSmoke()
  {
    Type[] sampleTypes = [
      typeof(object), typeof(DynamicAttribute), typeof(Task), typeof(IEnumerable<>), typeof(Console)
    ];

    var locations = sampleTypes
      .Select(type => type.Assembly).Distinct()
      .Select(assembly => assembly.Location).ToList();

    var thisAssembly = Assembly.GetExecutingAssembly();
    var assemblyName = thisAssembly.GetName().Name;
    var directory = new DirectoryInfo(
      Path.GetDirectoryName(thisAssembly.Location) ?? throw new ArgumentException());

    while (!directory.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
    {
      directory = directory.Parent ?? throw new ArgumentException();
    }

    var smokeDir = Path.Combine(directory.FullName, "smoke");
    locations.AddRange(Directory.EnumerateFiles(smokeDir, "*.dll", SearchOption.AllDirectories));
    locations.AddRange(Directory.EnumerateFiles(smokeDir, "*.zip", SearchOption.AllDirectories));

    foreach (var location in locations)
    {
      using var fileStream = File.OpenRead(location);

      // dig inside the zip archive
      if (string.Equals(Path.GetExtension(location), ".zip", StringComparison.OrdinalIgnoreCase))
      {
        var zipArchive = new ZipArchive(fileStream, ZipArchiveMode.Read);

        foreach (var entry in zipArchive.Entries)
        {
          if (string.Equals(Path.GetExtension(entry.Name), ".dll", StringComparison.OrdinalIgnoreCase))
          {
            using var memoryStream = new MemoryStream();

            using (var entryStream = entry.Open())
            {
              entryStream.CopyTo(memoryStream);
            }

            memoryStream.Position = 0;

            _ = AssemblyHasher.Run(memoryStream);
          }
        }
      }
      else
      {
        _ = AssemblyHasher.Run(fileStream);
      }
    }
  }

}
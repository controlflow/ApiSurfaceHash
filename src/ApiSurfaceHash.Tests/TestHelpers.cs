using System.Diagnostics.Contracts;
using System.Reflection;

namespace ApiSurfaceHash.Tests;

public static class TestHelpers
{
  [Pure]
  public static string GetTestPath(string name)
  {
    var thisAssembly = Assembly.GetExecutingAssembly();
    var assemblyName = thisAssembly.GetName().Name;
    var directory = new DirectoryInfo(
      Path.GetDirectoryName(thisAssembly.Location) ?? throw new ArgumentException());

    while (!directory.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
    {
      directory = directory.Parent ?? throw new ArgumentException();
    }

    return Path.Combine(directory.FullName, name);
  }
}
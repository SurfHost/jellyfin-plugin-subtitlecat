using System;
using System.IO;

namespace Jellyfin.Plugin.SubtitleCat.Tests;

/// <summary>Loads the saved subtitlecat pages that the parser tests run against.</summary>
internal static class FixtureFile
{
    /// <summary>Reads a fixture by file name.</summary>
    /// <param name="name">The file name inside the Fixtures folder.</param>
    public static string Read(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Fixture '{name}' was not copied to the test output. Check the None Include in the csproj.",
                path);
        }

        return File.ReadAllText(path);
    }
}

using Library.Web;
using Library.Web.Indexing;

namespace Library.Tests;

public class PathAndIgnoreTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Stuff", "Stuff")]
    [InlineData("/Stuff//Kintsu Shizu 261107/", "Stuff/Kintsu Shizu 261107")]
    [InlineData("Aquarium/水草球.3mf", "Aquarium/水草球.3mf")]
    public void Normalizes_library_paths(string? input, string expected)
    {
        Assert.True(LibraryPaths.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("Stuff/../../etc")]
    [InlineData("./Stuff")]
    [InlineData("Stuff\\..\\x")]
    [InlineData("a\0b")]
    public void Rejects_paths_that_could_escape(string input)
    {
        Assert.False(LibraryPaths.TryNormalize(input, out _));
    }

    [Fact]
    public void Full_paths_stay_inside_the_library()
    {
        var root = Path.Combine(Path.GetTempPath(), "lib-" + Guid.NewGuid().ToString("N"));
        var paths = new LibraryPaths(new LibraryOptions { LibraryPath = root, DataPath = root + "-data" });

        Assert.True(paths.TryGetFullPath("Stuff/a.3mf", out var full));
        Assert.Equal(Path.Combine(root, "Stuff", "a.3mf"), full);
        Assert.False(paths.TryGetFullPath("../outside", out _));
    }

    [Theory]
    [InlineData("datapackage.json", true)]
    [InlineData("DataPackage.JSON", true)]
    [InlineData("._R2-D2.3mf", true)]
    [InlineData(".DS_Store", true)]
    [InlineData("@eaDir", true)]
    [InlineData("#recycle", true)]
    [InlineData("upload.tmp", true)]
    [InlineData("R2-D2.3mf", false)]
    [InlineData("_fusion360", false)]
    public void Ignores_sync_and_nas_clutter(string name, bool ignored)
    {
        var matcher = new IgnoreMatcher(LibraryOptions.DefaultIgnore);

        Assert.Equal(ignored, matcher.IsIgnored(name));
    }
}

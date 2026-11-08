using Library.Web.Data;
using Library.Web.Indexing;

namespace Library.Tests;

public class DescriptionAndSearchTests
{
    private readonly DescriptionSanitizer _sanitizer = new();

    [Fact]
    public void Keeps_text_structure_and_links()
    {
        var html = _sanitizer.Sanitize("""<h2><strong>Assembly</strong></h2><ul><li>Glue</li></ul><p>See <a href="https://example.com/x">guide</a></p>""");

        Assert.Equal(
            """<h2><strong>Assembly</strong></h2><ul><li>Glue</li></ul><p>See <a href="https://example.com/x" target="_blank" rel="noopener noreferrer nofollow">guide</a></p>""",
            html);
    }

    [Fact]
    public void Drops_promotions_scripts_styles_and_remote_images()
    {
        var html = _sanitizer.Sanitize("""
            <commercialme><commercialtitle>Membership</commercialtitle><commercialcontent>Buy now</commercialcontent></commercialme>
            <boostme><boosttitle>Boost me</boosttitle></boostme>
            <script>alert(1)</script><style>p{color:red}</style>
            <figure class="image"><img src="https://makerworld.example/a.jpg"></figure>
            <p onclick="x()"><span style="color: red">Red</span> text</p>
            <a href="javascript:alert(1)">bad link</a>
            """);

        Assert.NotNull(html);
        Assert.DoesNotContain("Membership", html);
        Assert.DoesNotContain("Boost", html);
        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("color", html);
        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("javascript", html);
        Assert.Contains("<p>Red text</p>", html);
        Assert.Contains("bad link", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("<p>&nbsp;</p><p><br></p>")]
    [InlineData("<commercialme>only an advert</commercialme>")]
    public void Returns_null_when_nothing_is_left(string? input)
    {
        Assert.Null(_sanitizer.Sanitize(input));
    }

    [Theory]
    [InlineData("lamp", "\"lamp\"*")]
    [InlineData("  sir   patrick ", "\"sir\"* \"patrick\"*")]
    [InlineData("R2-D2", "\"R2-D2\"*")]
    [InlineData("say \"hi\"", "\"say\"* \"\"\"hi\"\"\"*")]
    [InlineData("a OR b", "\"a\"* \"OR\"* \"b\"*")]
    [InlineData("- --", null)]
    [InlineData("", null)]
    public void Builds_safe_prefix_queries(string input, string? expected)
    {
        Assert.Equal(expected, LibraryQueries.ToFtsQuery(input));
    }
}

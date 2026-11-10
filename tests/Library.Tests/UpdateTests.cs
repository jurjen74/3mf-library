using System.Net;
using System.Text;
using Library.Web;
using Library.Web.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Library.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.1", true)]
    [InlineData("v1.2.3", "1.2.3", true)]
    [InlineData("1.0.1-2-gf43c89b", "1.0.1", false)]   // a main-branch build after 1.0.1
    [InlineData("dev", null, false)]
    [InlineData("1.0", null, false)]
    [InlineData("", null, false)]
    public void Reads_the_release_from_a_version(string text, string? release, bool isRelease)
    {
        var version = new AppVersion(text);

        Assert.Equal(release, version.Release?.ToString(3));
        Assert.Equal(isRelease, version.IsRelease);
    }

    [Theory]
    [InlineData("1.0.1", "v1.0.2", "1.0.2")]
    [InlineData("1.0.1", "v1.1.0", "1.1.0")]
    [InlineData("1.9.0", "v1.10.0", "1.10.0")]          // numbers, not text
    [InlineData("1.0.1", "v1.0.1", null)]
    [InlineData("1.0.2", "v1.0.1", null)]
    [InlineData("1.0.1-2-gf43c89b", "v1.0.1", null)]    // main after 1.0.1 isn't behind 1.0.1
    [InlineData("1.0.1-2-gf43c89b", "v1.0.2", "1.0.2")]
    [InlineData("dev", "v9.9.9", null)]                 // local builds don't compare
    [InlineData("1.0.1", "nightly", null)]
    public void Only_a_newer_release_is_an_update(string running, string latestTag, string? expected)
    {
        var update = UpdateChecker.Newer(new AppVersion(running), latestTag, "https://github.com/jurjen74/3mf-library/releases/tag/" + latestTag);

        Assert.Equal(expected, update?.Version);
    }

    [Fact]
    public void An_unusable_release_link_falls_back_to_the_releases_page()
    {
        var update = UpdateChecker.Newer(new AppVersion("1.0.0"), "v1.0.1", "javascript:alert(1)");

        Assert.Equal("https://github.com/jurjen74/3mf-library/releases/latest", update?.Url);
    }

    [Fact]
    public async Task Remembers_a_newer_release_from_github()
    {
        var checker = Checker(new Answer(HttpStatusCode.OK,
            """{ "tag_name": "v1.2.0", "html_url": "https://github.com/jurjen74/3mf-library/releases/tag/v1.2.0" }"""));

        await checker.CheckAsync(new AppVersion("1.0.1"), CancellationToken.None);

        Assert.Equal(new UpdateInfo("1.2.0", "https://github.com/jurjen74/3mf-library/releases/tag/v1.2.0"), checker.Available);
        Assert.NotNull(checker.LastChecked);
    }

    [Fact]
    public async Task Forgets_an_update_once_it_is_installed()
    {
        var checker = Checker(new Answer(HttpStatusCode.OK, """{ "tag_name": "v1.2.0", "html_url": "https://example.com/" }"""));
        await checker.CheckAsync(new AppVersion("1.0.1"), CancellationToken.None);

        await checker.CheckAsync(new AppVersion("1.2.0"), CancellationToken.None);

        Assert.Null(checker.Available);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{ "message": "rate limit exceeded" }""")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{ "unexpected": true }""")]
    public async Task A_failed_check_changes_nothing_and_does_not_throw(HttpStatusCode status, string body)
    {
        var checker = Checker(new Answer(status, body));

        await checker.CheckAsync(new AppVersion("1.0.1"), CancellationToken.None);

        Assert.Null(checker.Available);
    }

    [Fact]
    public async Task Being_offline_changes_nothing_and_does_not_throw()
    {
        var checker = Checker(new Offline());

        await checker.CheckAsync(new AppVersion("1.0.1"), CancellationToken.None);

        Assert.Null(checker.Available);
        Assert.Null(checker.LastChecked);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("OFF", false)]
    [InlineData("0", false)]
    public void The_check_can_be_turned_off(string? setting, bool enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("UPDATE_CHECK", setting)]).Build();
        var options = LibraryOptions.FromConfiguration(config, Path.GetTempPath());

        Assert.Equal(enabled, options.UpdateCheck);
        Assert.Equal(enabled, UpdateChecker.IsEnabled(options, new AppVersion("1.0.1")));
        Assert.False(UpdateChecker.IsEnabled(options, new AppVersion("dev")));
    }

    private static UpdateChecker Checker(HttpMessageHandler handler) =>
        new(new Factory(handler), new LibraryOptions { LibraryPath = "/library", DataPath = "/data" }, NullLogger<UpdateChecker>.Instance);

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(UpdateChecker.LatestReleaseUrl, request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("No route to host");
    }
}

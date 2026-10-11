using Microsoft.AspNetCore.Http;

namespace FileMover.Tests;

public class WebOptionsTests
{
    [Fact]
    public void Defaults_OpenBrowserWithNoToken()
    {
        var o = WebOptions.Parse([]);
        Assert.True(o.OpenBrowser);
        Assert.Null(o.Token);
        Assert.Empty(o.HostArgs);
    }

    [Fact]
    public void OwnSwitches_AreConsumed_AndRestPassedToHost()
    {
        var o = WebOptions.Parse(["--urls", "http://*:8080", "--no-browser", "--token", "0123456789abcdef"]);
        Assert.False(o.OpenBrowser);
        Assert.Equal("0123456789abcdef", o.Token);
        Assert.Equal(["--urls", "http://*:8080"], o.HostArgs);
    }

    [Fact]
    public void TokenEqualsSyntax_IsSupported() =>
        Assert.Equal("0123456789abcdef", WebOptions.Parse(["--token=0123456789abcdef"]).Token);

    [Fact]
    public void TokenWithoutValue_Throws() =>
        Assert.Throws<ArgumentException>(() => WebOptions.Parse(["--token"]));

    [Fact]
    public void ShortToken_Throws() =>
        Assert.Throws<ArgumentException>(() => WebOptions.Parse(["--token", "short"]));
}

public class WebSecurityTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void GenerateToken_Is64HexCharsAndUnique()
    {
        var a = WebSecurity.GenerateToken();
        var b = WebSecurity.GenerateToken();
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(Token, true)]
    [InlineData("wrong", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TokenMatches_ComparesExactly(string? supplied, bool expected) =>
        Assert.Equal(expected, WebSecurity.TokenMatches(supplied, Token));

    [Theory]
    [InlineData(null, "localhost:5050", true)]
    [InlineData("", "localhost:5050", true)]
    [InlineData("http://localhost:5050", "localhost:5050", true)]
    [InlineData("http://LOCALHOST:5050", "localhost:5050", true)]
    [InlineData("http://localhost", "localhost", true)]
    [InlineData("http://evil.example", "localhost:5050", false)]
    [InlineData("http://localhost:6000", "localhost:5050", false)]
    [InlineData("null", "localhost:5050", false)]
    [InlineData("http://localhost:5050", "", false)]
    public void OriginMatchesHost_RejectsCrossOrigin(string? origin, string host, bool expected) =>
        Assert.Equal(expected, WebSecurity.OriginMatchesHost(origin, host.Length == 0 ? default : new HostString(host)));

    [Fact]
    public void ExtractToken_PrefersBearerHeader()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer abc";
        ctx.Request.QueryString = new QueryString("?access_token=xyz");
        Assert.Equal("abc", WebSecurity.ExtractToken(ctx.Request));
    }

    [Fact]
    public void ExtractToken_FallsBackToQuery()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.QueryString = new QueryString("?access_token=xyz");
        Assert.Equal("xyz", WebSecurity.ExtractToken(ctx.Request));
    }

    private static async Task<(int Status, bool NextCalled, HttpContext Ctx)> Invoke(
        string path, string? token = null, string? origin = null, string host = "localhost:5050")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Host = new HostString(host);
        if (token is not null) ctx.Request.QueryString = new QueryString("?access_token=" + token);
        if (origin is not null) ctx.Request.Headers.Origin = origin;

        var nextCalled = false;
        await WebSecurity.Middleware(Token)(ctx, () => { nextCalled = true; return Task.CompletedTask; });
        return (ctx.Response.StatusCode, nextCalled, ctx);
    }

    [Fact]
    public async Task Hub_WithoutToken_Is401()
    {
        var (status, next, _) = await Invoke("/copyhub/negotiate");
        Assert.Equal(401, status);
        Assert.False(next);
    }

    [Fact]
    public async Task Hub_WithWrongToken_Is401()
    {
        var (status, next, _) = await Invoke("/copyhub", token: "nope");
        Assert.Equal(401, status);
        Assert.False(next);
    }

    [Fact]
    public async Task Hub_FromForeignOrigin_Is403_EvenWithToken()
    {
        var (status, next, _) = await Invoke("/copyhub", token: Token, origin: "https://evil.example");
        Assert.Equal(403, status);
        Assert.False(next);
    }

    [Fact]
    public async Task Hub_WithTokenAndSameOrigin_PassesThrough()
    {
        var (_, next, _) = await Invoke("/copyhub", token: Token, origin: "http://localhost:5050");
        Assert.True(next);
    }

    [Fact]
    public async Task StaticFiles_DoNotNeedToken_ButGetSecurityHeaders()
    {
        var (_, next, ctx) = await Invoke("/index.html");
        Assert.True(next);
        Assert.Equal("nosniff", ctx.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("DENY", ctx.Response.Headers["X-Frame-Options"]);
        Assert.Contains("frame-ancestors 'none'", ctx.Response.Headers.ContentSecurityPolicy.ToString());
    }
}

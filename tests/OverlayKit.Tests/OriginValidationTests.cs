using Xunit;

namespace OverlayKit.Tests;

public class OriginValidationTests
{
    private const int Port = 17893;

    // --- Allowed loopback origins ---

    [Theory]
    [InlineData("http://127.0.0.1:17893")]
    [InlineData("http://localhost:17893")]
    [InlineData("http://LOCALHOST:17893")]
    [InlineData("http://[::1]:17893")]
    public void Accepts_loopback_origins(string origin)
    {
        Assert.True(OverlayServer.IsAllowedOrigin(origin, Port));
    }

    // --- Wrong port ---

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:80")]
    public void Rejects_wrong_port(string origin)
    {
        Assert.False(OverlayServer.IsAllowedOrigin(origin, Port));
    }

    // --- External / non-loopback hosts ---

    [Theory]
    [InlineData("http://192.168.1.100:17893")]
    [InlineData("http://example.com:17893")]
    [InlineData("http://10.0.0.1:17893")]
    [InlineData("https://evil.com:17893")]
    [InlineData("http://0.0.0.0:17893")]
    public void Rejects_non_loopback_hosts(string origin)
    {
        Assert.False(OverlayServer.IsAllowedOrigin(origin, Port));
    }

    // --- Malformed / empty ---

    [Theory]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("ftp://127.0.0.1:17893")]
    [InlineData("://127.0.0.1:17893")]
    public void Rejects_malformed_origins(string origin)
    {
        Assert.False(OverlayServer.IsAllowedOrigin(origin, Port));
    }

    // --- Custom port ---

    [Fact]
    public void Accepts_custom_port_when_matching()
    {
        Assert.True(OverlayServer.IsAllowedOrigin("http://127.0.0.1:9999", 9999));
        Assert.True(OverlayServer.IsAllowedOrigin("http://localhost:9999", 9999));
    }

    [Fact]
    public void Rejects_default_port_when_custom_expected()
    {
        Assert.False(OverlayServer.IsAllowedOrigin("http://127.0.0.1:17893", 9999));
    }
}


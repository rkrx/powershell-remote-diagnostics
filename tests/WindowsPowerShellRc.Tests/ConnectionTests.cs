using WindowsPowerShellRc.Transport;

namespace WindowsPowerShellRc.Tests;
public class ConnectionTests
{
    [Fact]
    public void ACompleteConnectionUrlIsRecognized()
    {
        Assert.NotNull(ConnectionAddress.Parse($"grpc://v1.{new string('a',64)}.{new string('b',64)}@localhost:8080"));
    }
    [Fact]
    public void ConnectionRoundTripsIpv6AndRedactsDefaultStringRendering()
    {
        string text = $"grpc://v1.{new string('a',64)}.{new string('b',64)}@[::1]:8080";
        var address = ConnectionAddress.Parse(text);
        Assert.Equal(text, address.ToConnectionString());
        Assert.Equal("[redacted connection]", address.ToString());
    }
    [Theory]
    [InlineData("http://secret@localhost:8080")]
    [InlineData("grpc://secret@localhost:8080")]
    [InlineData("not a URL")]
    public void InvalidUrlsAreRejectedWithoutEchoingTheirContents(string text)
    {
        var error = Assert.Throws<FormatException>(() => ConnectionAddress.Parse(text));
        Assert.DoesNotContain(text, error.Message);
    }
}

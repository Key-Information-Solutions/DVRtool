using System.Net;
using DVRTool.Core;
using DVRTool.Vendors.Hikvision;
using Xunit;

namespace DVRTool.Tests;

public class HikvisionUserWriteTests
{
    private static readonly NvrConnection Conn = new()
    {
        Host = "192.0.2.10",
        Username = "admin",
        Password = "p@ss:word",
    };

    private const string UsersPath = "/ISAPI/Security/users";

    private static string UserList(params string[] names) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <UserList version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        {string.Join("\n", names.Select((n, i) => $"""
            <User><id>{i + 1}</id><userName>{n}</userName>
            <userLevel>{(n == "admin" ? "Administrator" : "Operator")}</userLevel></User>
            """))}
        </UserList>
        """;

    private static string Status(int code, string? sub = null) => $"""
        <?xml version="1.0" encoding="UTF-8" ?>
        <ResponseStatus version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
        <requestURL>{UsersPath}</requestURL><statusCode>{code}</statusCode>
        <statusString>{(code == 1 ? "OK" : "Invalid Operation")}</statusString>
        {(sub is null ? "" : $"<subStatusCode>{sub}</subStatusCode>")}
        </ResponseStatus>
        """;

    /// <summary>Accepts the create, then reports the account on the next read.</summary>
    private static MockHttpHandler Accepting() =>
        new((req, _) => req.Method == HttpMethod.Post
            ? MockHttpHandler.Xml(Status(1))
            : MockHttpHandler.Xml(UserList("admin", "jordan")));

    [Fact]
    public async Task SendsThePlaceholderIdAndTheRequestedLevel()
    {
        var handler = Accepting();
        using var client = new HikvisionClient(Conn, handler);

        await client.CreateUserAsync(new NewUser("jordan", "Sunflower9", UserRole.Operator));

        var (request, body) = handler.Requests.First(r => r.Request.Method == HttpMethod.Post);
        Assert.Equal(UsersPath, request.RequestUri!.AbsolutePath);
        // The device assigns the id; the placeholder has to be there or some firmware refuses.
        Assert.Contains("<id>0</id>", body);
        Assert.Contains("<userName>jordan</userName>", body);
        Assert.Contains("<password>Sunflower9</password>", body);
        Assert.Contains("<userLevel>Operator</userLevel>", body);
    }

    [Theory]
    [InlineData(UserRole.Admin, "Administrator")]
    [InlineData(UserRole.Operator, "Operator")]
    [InlineData(UserRole.Viewer, "Viewer")]
    public async Task WritesTheIsapiSpellingOfEachRole(UserRole role, string expected)
    {
        var handler = Accepting();
        using var client = new HikvisionClient(Conn, handler);

        await client.CreateUserAsync(new NewUser("jordan", "Sunflower9", role));

        var (_, body) = handler.Requests.First(r => r.Request.Method == HttpMethod.Post);
        Assert.Contains($"<userLevel>{expected}</userLevel>", body);
    }

    [Fact]
    public async Task ReturnsTheAccountAsTheDeviceReportsItNotAsAsked()
    {
        // Asked for an Operator; the read-back is what the caller is told about.
        var handler = new MockHttpHandler((req, _) => req.Method == HttpMethod.Post
            ? MockHttpHandler.Xml(Status(1))
            : MockHttpHandler.Xml("""
                <UserList version="2.0" xmlns="http://www.hikvision.com/ver20/XMLSchema">
                <User><id>7</id><userName>jordan</userName><userLevel>Viewer</userLevel></User>
                </UserList>
                """));
        using var client = new HikvisionClient(Conn, handler);

        var created = await client.CreateUserAsync(new NewUser("jordan", "Sunflower9", UserRole.Operator));

        Assert.Equal("7", created.Id);
        Assert.Equal("Viewer", created.NativeLevel);
        Assert.Equal(UserRole.Viewer, created.Role);
    }

    [Fact]
    public async Task TreatsANonOneStatusCodeUnderHttp200AsARejection()
    {
        // The trap: a refused password is HTTP 200. Checking only the HTTP status would
        // report success for an account that was never made.
        var handler = new MockHttpHandler((req, _) => req.Method == HttpMethod.Post
            ? MockHttpHandler.Xml(Status(6, "riskPassword"))
            : MockHttpHandler.Xml(UserList("admin")));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(
            () => client.CreateUserAsync(new NewUser("jordan", "Sunflower9", UserRole.Operator)));

        // The device's own words reach the operator verbatim.
        Assert.Contains("riskPassword", ex.Message);
        Assert.Contains("statusCode 6", ex.Message);
    }

    [Fact]
    public async Task FailsWhenAnAcceptedCreateLeavesNoAccountBehind()
    {
        var handler = new MockHttpHandler((req, _) => req.Method == HttpMethod.Post
            ? MockHttpHandler.Xml(Status(1))
            : MockHttpHandler.Xml(UserList("admin")));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(
            () => client.CreateUserAsync(new NewUser("jordan", "Sunflower9", UserRole.Operator)));

        Assert.Contains("not in the account list read back", ex.Message);
    }

    [Fact]
    public async Task FailsOnAnHttpError()
    {
        var handler = new MockHttpHandler((req, _) => req.Method == HttpMethod.Post
            ? MockHttpHandler.Xml(Status(4), HttpStatusCode.Forbidden)
            : MockHttpHandler.Xml(UserList("admin")));
        using var client = new HikvisionClient(Conn, handler);

        var ex = await Assert.ThrowsAsync<NvrException>(
            () => client.CreateUserAsync(new NewUser("jordan", "Sunflower9", UserRole.Operator)));

        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task RefusesToWriteACustomLevel()
    {
        using var client = new HikvisionClient(Conn, Accepting());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.CreateUserAsync(new NewUser("jordan", "Sunflower9", UserRole.Custom)));
    }

    [Theory]
    [InlineData("", "Sunflower9")]
    [InlineData("jordan", "")]
    public async Task RefusesABlankNameOrPasswordWithoutTouchingTheDevice(string name, string password)
    {
        var handler = Accepting();
        using var client = new HikvisionClient(Conn, handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.CreateUserAsync(new NewUser(name, password, UserRole.Operator)));
        Assert.Empty(handler.Requests);
    }
}

using System.Net;
using System.Text;

namespace DVRTool.Tests;

/// <summary>Scripted HTTP handler: routes each request through a delegate and records it.</summary>
public sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _respond;

    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    public MockHttpHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        string body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return _respond(request, body);
    }

    public static HttpResponseMessage Xml(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/xml") };

    public static HttpResponseMessage Text(string content, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "text/plain") };
}

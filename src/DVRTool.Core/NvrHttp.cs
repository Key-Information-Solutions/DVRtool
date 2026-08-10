using System.Net;

namespace DVRTool.Core;

public static class NvrHttp
{
    /// <summary>
    /// HttpClient set up for NVR digest auth (Hikvision and Dahua both default to
    /// Digest/MD5 on their HTTP APIs). Pass a custom <paramref name="handler"/> only
    /// from tests; it then bypasses the credential setup.
    /// </summary>
    public static HttpClient Create(NvrConnection conn, TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            var credentials = new CredentialCache
            {
                { new Uri(conn.HttpBase), "Digest", new NetworkCredential(conn.Username, conn.Password) },
            };
            var httpHandler = new HttpClientHandler
            {
                Credentials = credentials,
                // Reuse the digest challenge instead of eating a 401 round-trip per request.
                PreAuthenticate = true,
            };
            if (conn.UseTls)
            {
                // NVR certs are self-signed; pin trust-on-first-use instead of chain validation.
                httpHandler.ServerCertificateCustomValidationCallback =
                    CertificatePins.CreateValidator(conn.Host, conn.HttpPort);
            }
            handler = httpHandler;
        }

        return new HttpClient(handler)
        {
            BaseAddress = new Uri(conn.HttpBase),
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        };
    }
}

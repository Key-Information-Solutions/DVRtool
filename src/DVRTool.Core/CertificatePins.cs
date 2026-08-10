using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace DVRTool.Core;

/// <summary>
/// Trust-on-first-use certificate pinning for NVR HTTPS. NVR certificates are
/// almost always self-signed, so chain validation would reject every device;
/// instead the first connection to a host pins the cert's SHA-256 and later
/// connections must present the same cert. Pins live in %APPDATA%\DVRTool\pins.json.
/// </summary>
public static class CertificatePins
{
    private static readonly object Gate = new();

    private static string PinPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DVRTool", "pins.json");

    public static Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool>
        CreateValidator(string host, int port)
    {
        string key = $"{host}:{port}";
        return (_, cert, _, _) =>
        {
            if (cert is null)
                return false;
            string thumbprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
            lock (Gate)
            {
                var pins = Load();
                if (pins.TryGetValue(key, out string? pinned))
                {
                    if (string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase))
                        return true;
                    throw new AuthenticationException(
                        $"TLS certificate for {key} changed (pinned {pinned[..12]}…, now {thumbprint[..12]}…). " +
                        $"If the device certificate was legitimately replaced, delete its entry from {PinPath}.");
                }
                pins[key] = thumbprint;
                Save(pins);
                return true;
            }
        };
    }

    private static Dictionary<string, string> Load()
    {
        if (!File.Exists(PinPath))
            return new(StringComparer.OrdinalIgnoreCase);

        // A read failure on an EXISTING pin file must fail closed (propagate), not
        // return an empty set — an empty set would route an already-pinned host into
        // the first-use branch and silently trust + re-pin whatever cert is presented.
        // FileShare.ReadWrite tolerates a concurrent writer (CLI + GUI both pin).
        string json;
        using (var fs = new FileStream(PinPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(fs))
            json = reader.ReadToEnd();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            // Corrupt/hand-edited file: starting fresh is acceptable.
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void Save(Dictionary<string, string> pins)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PinPath)!);
            File.WriteAllText(PinPath, JsonSerializer.Serialize(pins,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Failing to persist a pin must not break the connection; the next
            // successful connect will simply pin again.
        }
    }
}

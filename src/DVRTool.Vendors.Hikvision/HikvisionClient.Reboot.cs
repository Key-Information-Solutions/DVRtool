using DVRTool.Core;

namespace DVRTool.Vendors.Hikvision;

/// <summary>
/// The <see cref="IDeviceRebooter"/> face of the Hikvision client: <c>PUT
/// /ISAPI/System/reboot</c> with an empty body. See <c>docs/device-config.md</c> §Reboot.
/// </summary>
public sealed partial class HikvisionClient : IDeviceRebooter
{
    private const string RebootPath = "/ISAPI/System/reboot";

    public async Task RebootAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PutAsync(RebootPath, content: null, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new NvrException(
                $"PUT {RebootPath} → {(int)resp.StatusCode} {resp.ReasonPhrase}: " +
                DescribeRejection(text, (int)resp.StatusCode), text, (int)resp.StatusCode);

        // A refusal can arrive under HTTP 200 as a non-1 statusCode, like every ISAPI write.
        if (TryParseStatusCode(text) is { } status && status != 1)
            throw new NvrException(
                $"the recorder refused to restart: {DescribeRejection(text, status)}", text);
    }
}

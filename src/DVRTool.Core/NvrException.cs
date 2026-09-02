namespace DVRTool.Core;

public sealed class NvrException : Exception
{
    /// <summary>
    /// True for the failures a per-device or per-camera "carry on with the rest" handler
    /// should absorb: a device-level <see cref="NvrException"/>, a transport error
    /// (<see cref="HttpRequestException"/>: refused, reset, DNS), or an HttpClient timeout —
    /// which surfaces as <see cref="TaskCanceledException"/> even though the caller's own
    /// token was never cancelled. A real cancellation is never absorbed. Identity failures
    /// (<c>DeviceIdentityException</c>) are deliberately not <see cref="NvrException"/>s and
    /// fall through here too.
    /// </summary>
    public static bool IsPerDeviceFailure(Exception ex, CancellationToken ct) => ex switch
    {
        NvrException => true,
        HttpRequestException => true,
        TaskCanceledException => !ct.IsCancellationRequested,
        _ => false,
    };

    public int? StatusCode { get; }
    public string? ResponseBody { get; }

    public NvrException(string message, string? responseBody = null, int? statusCode = null,
        Exception? inner = null)
        : base(message, inner)
    {
        ResponseBody = responseBody;
        StatusCode = statusCode;
    }
}

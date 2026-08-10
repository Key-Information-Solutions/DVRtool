namespace DVRTool.Core;

public sealed class NvrException : Exception
{
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

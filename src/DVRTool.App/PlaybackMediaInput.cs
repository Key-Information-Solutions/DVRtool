using System.IO;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace DVRTool.App;

/// <summary>
/// A recorded-footage body handed to LibVLC as an input it may read <i>at its own pace</i>:
/// forward-only, never seekable, and never faster than the picture needs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists instead of <see cref="StreamMediaInput"/>.</b> A playback body is a
/// plain HTTP response and arrives far faster than real time — measured on the lab recorder,
/// 1.28 GB of a 1.49 GB body inside 30 seconds, an hour and three quarters of footage. LibVLC
/// decides whether it may control an input's pace from one thing only: whether a seek callback
/// was registered. <see cref="StreamMediaInput"/> sets <see cref="MediaInput.CanSeek"/> from
/// <see cref="Stream.CanSeek"/>, so a forward-only body registers none, and libvlc then treats
/// the input as a <i>live source that paces itself</i> — it reads flat out and slaves its clock
/// to the arrival rate. The clock runs about seventy times too fast, every picture is late the
/// moment it is decoded, and the video output shows the handful that land: footage skipping
/// forward in jumps, which is what this looked like from the Playback tab.
/// </para>
/// <para>
/// Registering the callback and refusing the seeks says the true thing about an HTTP body: it
/// cannot be seeked, but it certainly can be paced, because a reader that stops reading fills
/// the socket and the recorder waits. Measured on the same body, same 30 seconds: 6.3 MB read
/// instead of 1.28 GB, the clock at 0.99×, and not one picture lost. Over 90 s: 18.9 MB, 1.00×,
/// zero lost, zero late. It also makes the speed buttons real — at 4× libvlc now pulls four
/// times the bytes and the clock runs at 3.97×, where before it was already reading as fast as
/// the network allowed and had nothing left to give.
/// </para>
/// <para>
/// <b>The one cost.</b> A demuxer that genuinely needs to seek gets a refusal and gives up on
/// the media, where a live-mode input would have limped on. That is a loud failure — no picture
/// at all, reported on the status line — not a silent one, and it does not arise for the bodies
/// we play: Hikvision's program stream needs no seek once the IMKH envelope is dropped
/// (<c>HikvisionClient.OpenPlaybackAsync</c>), which is exactly why that envelope is dropped.
/// Leave the envelope on and libvlc seeks looking for the pack header, is refused, and shows
/// nothing — verified.
/// </para>
/// <para>
/// This is the playback path only. Live video is genuinely live: it arrives in real time, so
/// libvlc's live handling is the right one and the Live tab keeps <see cref="StreamMediaInput"/>.
/// </para>
/// </remarks>
internal sealed class PlaybackMediaInput : MediaInput
{
    private readonly Stream _body;
    private byte[] _buffer = new byte[64 * 1024];

    public PlaybackMediaInput(Stream body)
    {
        _body = body;
        // The load-bearing line: LibVLCSharp registers the native seek callback only when this
        // is set, and libvlc reads the presence of that callback as "this input can be paced".
        CanSeek = true;
    }

    /// <summary>How many bytes were handed over, for the status line and for tests.</summary>
    public long BytesRead { get; private set; }

    /// <summary>The length is genuinely unknown: the recorder is still writing the body.</summary>
    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue;
        return true;
    }

    public override int Read(IntPtr buf, uint len)
    {
        try
        {
            if (_buffer.Length < len)
                _buffer = new byte[len];
            int n = _body.Read(_buffer, 0, (int)len);
            if (n > 0)
            {
                Marshal.Copy(_buffer, 0, buf, n);
                BytesRead += n;
            }
            return n;
        }
        catch (Exception)
        {
            // The body failed or was closed under us — a stop, a new seek, or the recorder
            // dropping the connection. -1 is libvlc's "this input is finished".
            return -1;
        }
    }

    /// <summary>
    /// Always refused. The callback exists to tell libvlc it may pace the input, not to
    /// promise a seek; a seek in this tab is a fresh request from a new time, never a move
    /// inside a body that is still arriving.
    /// </summary>
    public override bool Seek(ulong offset) => false;

    public override void Close()
    {
    }
}

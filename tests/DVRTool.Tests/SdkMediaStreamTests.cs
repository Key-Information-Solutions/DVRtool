using System.Runtime.InteropServices;
using DVRTool.Vendors.HikvisionSdk;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The buffer between the SDK's native callback thread and the demuxer. Its two hard rules —
/// <c>Append</c> never blocks, and a silent stream ends rather than hangs — are what keep a
/// slow viewer from stalling the shared TCP session or freezing the app.
/// </summary>
public class SdkMediaStreamTests
{
    /// <summary>Feeds bytes in the way the SDK does: an unmanaged buffer plus a length.</summary>
    private static void Append(SdkMediaStream stream, params byte[] bytes)
    {
        IntPtr native = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, native, bytes.Length);
            stream.Append(native, bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    private static byte[] Bytes(int count, byte value = 0x41) =>
        Enumerable.Repeat(value, count).ToArray();

    [Fact]
    public void Reads_back_what_was_appended_in_order()
    {
        using var stream = new SdkMediaStream();
        Append(stream, 1, 2, 3);
        Append(stream, 4, 5);
        stream.Complete();

        var read = new byte[16];
        int first = stream.Read(read, 0, read.Length);
        int second = stream.Read(read, first, read.Length - first);

        Assert.Equal(3, first);
        Assert.Equal(2, second);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, read[..5]);
        Assert.Equal(5, stream.BytesReceived);
        Assert.Equal(0, stream.BytesDropped);
    }

    [Fact]
    public void A_chunk_larger_than_the_read_buffer_is_delivered_across_reads()
    {
        // The SDK's chunks are its own size, not the reader's; a partially consumed chunk has
        // to resume where it left off rather than lose the tail.
        using var stream = new SdkMediaStream();
        Append(stream, Bytes(10, 0x7F));
        stream.Complete();

        var read = new byte[4];
        Assert.Equal(4, stream.Read(read, 0, 4));
        Assert.Equal(4, stream.Read(read, 0, 4));
        Assert.Equal(2, stream.Read(read, 0, 4));
        Assert.Equal(0, stream.Read(read, 0, 4));
    }

    [Fact]
    public void Read_returns_zero_once_completed_and_drained()
    {
        using var stream = new SdkMediaStream();
        Append(stream, 9);
        stream.Complete();

        var read = new byte[4];
        Assert.Equal(1, stream.Read(read, 0, 4));
        Assert.Equal(0, stream.Read(read, 0, 4));
    }

    [Fact]
    public void Overflow_drops_the_oldest_bytes_and_counts_them()
    {
        // Latency, not integrity, is what the choice protects: a live viewer wants the newest
        // video, so the front of the queue goes.
        using var stream = new SdkMediaStream(capacityBytes: 100);
        Append(stream, Bytes(80, 0xAA));
        Append(stream, Bytes(80, 0xBB));

        Assert.Equal(160, stream.BytesReceived);
        Assert.Equal(80, stream.BytesDropped);

        var read = new byte[80];
        Assert.Equal(80, stream.Read(read, 0, 80));
        Assert.All(read, b => Assert.Equal(0xBB, b)); // the newest survived
    }

    [Fact]
    public void A_chunk_bigger_than_the_whole_buffer_is_still_delivered()
    {
        // Dropping down to nothing would mean a device whose chunks exceed Capacity never
        // delivered a single byte — a silent, permanent black screen.
        using var stream = new SdkMediaStream(capacityBytes: 10);
        Append(stream, Bytes(50, 0xCC));

        var read = new byte[50];
        Assert.Equal(50, stream.Read(read, 0, 50));
        Assert.Equal(0, stream.BytesDropped);
    }

    [Fact]
    public void Append_after_Complete_is_ignored()
    {
        // StopRealPlay joins the SDK's receive thread, but a callback already in flight must
        // not resurrect a finished stream.
        using var stream = new SdkMediaStream();
        stream.Complete();
        Append(stream, 1, 2, 3);

        Assert.Equal(0, stream.BytesReceived);
        Assert.Equal(0, stream.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void A_null_or_empty_callback_buffer_is_ignored()
    {
        using var stream = new SdkMediaStream();
        stream.Append(IntPtr.Zero, 100);
        stream.Append(new IntPtr(1234), 0);
        stream.Append(new IntPtr(1234), -5);

        Assert.Equal(0, stream.BytesReceived);
    }

    [Fact]
    public void A_silent_stream_ends_the_read_instead_of_hanging()
    {
        // The failure this exists for: the device accepts the preview and sends nothing.
        using var stream = new SdkMediaStream(stallTimeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(0, stream.Read(new byte[16], 0, 16));
        Assert.True(stream.Stalled);
    }

    [Fact]
    public void A_clean_end_is_not_reported_as_a_stall()
    {
        using var stream = new SdkMediaStream(stallTimeout: TimeSpan.FromSeconds(30));
        stream.Complete();

        Assert.Equal(0, stream.Read(new byte[16], 0, 16));
        Assert.False(stream.Stalled);
    }

    [Fact]
    public void A_fault_surfaces_to_the_reader_once_the_buffer_drains()
    {
        using var stream = new SdkMediaStream();
        Append(stream, 1, 2);
        stream.Complete(new InvalidOperationException("stream died"));

        var read = new byte[4];
        Assert.Equal(2, stream.Read(read, 0, 4)); // buffered bytes first
        var ex = Assert.Throws<InvalidOperationException>(() => stream.Read(read, 0, 4));
        Assert.Equal("stream died", ex.Message);
        // Reported once; a retry then sees an ordinary end of stream.
        Assert.Equal(0, stream.Read(read, 0, 4));
    }

    [Fact]
    public async Task Read_wakes_as_soon_as_the_callback_delivers()
    {
        using var stream = new SdkMediaStream(stallTimeout: TimeSpan.FromSeconds(30));
        var reader = Task.Run(() =>
        {
            var buffer = new byte[8];
            return stream.Read(buffer, 0, buffer.Length);
        });

        await Task.Delay(50);
        Append(stream, 1, 2, 3, 4);

        Assert.Equal(4, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitForData_reports_whether_video_ever_arrived()
    {
        using var arrived = new SdkMediaStream();
        var waiting = arrived.WaitForDataAsync(TimeSpan.FromSeconds(5));
        Append(arrived, 1);
        Assert.True(await waiting);

        using var never = new SdkMediaStream();
        Assert.False(await never.WaitForDataAsync(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task WaitForData_gives_up_as_soon_as_the_stream_ends()
    {
        using var stream = new SdkMediaStream();
        var waiting = stream.WaitForDataAsync(TimeSpan.FromSeconds(30));
        stream.Complete();

        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Looks_like_a_live_source_to_a_demuxer()
    {
        // LibVLC and ffmpeg decide whether a source is seekable from exactly these.
        using var stream = new SdkMediaStream();

        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Throws<NotSupportedException>(() => stream.Length);
        Assert.Throws<NotSupportedException>(() => stream.Position);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
    }

    [Fact]
    public void Dispose_ends_the_stream()
    {
        var stream = new SdkMediaStream(stallTimeout: TimeSpan.FromSeconds(30));
        stream.Dispose();

        // Not a stall: disposal is a clean end, and a blocked reader must not sit through the
        // stall timeout on the way out.
        Assert.Equal(0, stream.Read(new byte[4], 0, 4));
        Assert.False(stream.Stalled);
    }

    [Fact]
    public async Task Concurrent_appends_and_reads_lose_nothing()
    {
        // The real shape: one native producer, one managed consumer, capacity never exceeded.
        const int chunks = 500;
        const int chunkSize = 64;
        using var stream = new SdkMediaStream(capacityBytes: 1 << 20,
            stallTimeout: TimeSpan.FromSeconds(10));

        var producer = Task.Run(() =>
        {
            for (int i = 0; i < chunks; i++)
                Append(stream, Bytes(chunkSize, 0x5A));
            stream.Complete();
        });

        long total = 0;
        var buffer = new byte[100];
        await Task.Run(() =>
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                total += read;
        });
        await producer;

        Assert.Equal(chunks * chunkSize, total);
        Assert.Equal(0, stream.BytesDropped);
    }
}

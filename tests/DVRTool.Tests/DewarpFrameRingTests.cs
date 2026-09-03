using DVRTool.Core;
using Xunit;

namespace DVRTool.Tests;

/// <summary>
/// The three-slot ring between the decoder thread and the UI thread. Every case is a sequence
/// of the calls LibVLC's vmem output and the UI actually make, in the order they make them.
/// </summary>
public class DewarpFrameRingTests
{
    private static DewarpFrameRing Configured()
    {
        var ring = new DewarpFrameRing();
        ring.Configure(64, 48, yPitch: 64, yLines: 48, chromaPitch: 32, chromaLines: 24);
        return ring;
    }

    [Fact]
    public void NothingToTakeBeforeAFrameIsComplete()
    {
        var ring = Configured();
        Assert.False(ring.TryTakeLatest(out _));
        int slot = ring.AcquireForWrite();
        // Locked but not yet displayed: still nothing for the UI.
        Assert.False(ring.TryTakeLatest(out _));
        ring.CompleteWrite(slot);
        Assert.True(ring.TryTakeLatest(out int taken));
        Assert.Equal(slot, taken);
    }

    [Fact]
    public void TheDecoderNeverGetsTheSlotOnScreenOrTheOneWaiting()
    {
        var ring = Configured();
        int first = ring.AcquireForWrite();
        ring.CompleteWrite(first);
        Assert.True(ring.TryTakeLatest(out _)); // first is on screen

        int second = ring.AcquireForWrite();
        Assert.NotEqual(first, second);
        ring.CompleteWrite(second); // second is waiting

        int third = ring.AcquireForWrite();
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void ADecoderThatGetsAheadOverwritesTheWaitingFrameAndCountsIt()
    {
        var ring = Configured();
        int a = ring.AcquireForWrite();
        ring.CompleteWrite(a);
        int b = ring.AcquireForWrite();
        ring.CompleteWrite(b);

        Assert.Equal(1, ring.Dropped);
        Assert.Equal(2, ring.Completed);
        Assert.True(ring.TryTakeLatest(out int taken));
        Assert.Equal(b, taken); // the newest, never the backlog
        Assert.False(ring.TryTakeLatest(out _));
    }

    [Fact]
    public void TakingTheNextFrameFreesTheOnePreviouslyOnScreen()
    {
        var ring = Configured();
        int a = ring.AcquireForWrite();
        ring.CompleteWrite(a);
        Assert.True(ring.TryTakeLatest(out _));
        int b = ring.AcquireForWrite();
        ring.CompleteWrite(b);
        Assert.True(ring.TryTakeLatest(out int onScreen));
        Assert.Equal(b, onScreen);
        Assert.Equal(b, ring.PresentingSlot);

        // With b on screen and nothing waiting, both other slots are free again — a is one of
        // them, so two acquisitions in a row can never collide with b.
        int c = ring.AcquireForWrite();
        ring.CompleteWrite(c);
        int d = ring.AcquireForWrite();
        Assert.NotEqual(b, c);
        Assert.NotEqual(b, d);
        Assert.NotEqual(c, d);
    }

    [Fact]
    public void ALockWhoseDisplayNeverCameIsReclaimed()
    {
        // vmem is sequential, so a slot still "being written" when the next lock arrives was
        // abandoned. Without reclaiming it the ring would leak a slot per abandoned lock and hit
        // the "every slot in use" wall after three.
        var ring = Configured();
        for (int i = 0; i < 20; i++)
            ring.AcquireForWrite(); // never completed
        int slot = ring.AcquireForWrite();
        ring.CompleteWrite(slot);
        Assert.True(ring.TryTakeLatest(out int taken));
        Assert.Equal(slot, taken);
    }

    [Fact]
    public void CompletingASlotThatIsNotBeingWrittenIsAnError()
    {
        var ring = Configured();
        Assert.Throws<ArgumentException>(() => ring.CompleteWrite(0));
        int slot = ring.AcquireForWrite();
        ring.CompleteWrite(slot);
        Assert.Throws<ArgumentException>(() => ring.CompleteWrite(slot));
    }

    [Fact]
    public void FramesCarryTheConfiguredLayoutAndTheSlotsOwnBuffers()
    {
        var ring = new DewarpFrameRing();
        ring.Configure(100, 51, yPitch: 128, yLines: 64, chromaPitch: 64, chromaLines: 32,
            YuvRange.Bt601Full);
        int slot = ring.AcquireForWrite();
        var frame = ring.FrameOf(slot);

        Assert.Equal(DewarpFrameFormat.I420, frame.Format);
        Assert.Equal(100, frame.Width);
        Assert.Equal(51, frame.Height);
        Assert.Equal(128, frame.YPitch);
        Assert.Equal(64, frame.ChromaPitch);
        Assert.Equal(YuvRange.Bt601Full, frame.Range);
        Assert.Same(ring.YPlane(slot), frame.YPlane);
        Assert.Same(ring.UPlane(slot), frame.UPlane);
        Assert.Same(ring.VPlane(slot), frame.VPlane);
        Assert.Equal(128 * 64, frame.YPlane.Length);
        Assert.Equal(64 * 32, frame.UPlane!.Length);

        // Three distinct sets of buffers, so the decoder and the UI never share one.
        Assert.NotSame(ring.YPlane(0), ring.YPlane(1));
        Assert.NotSame(ring.YPlane(1), ring.YPlane(2));
    }

    [Fact]
    public void ReconfiguringToTheSameSizeKeepsTheBuffers()
    {
        // LibVLC renegotiates the format on every restart; 30 MB of pinned heap should not churn
        // for a stream that has not changed.
        var ring = Configured();
        var before = ring.YPlane(0);
        ring.Configure(64, 48, 64, 48, 32, 24);
        Assert.Same(before, ring.YPlane(0));

        ring.Configure(128, 96, 128, 96, 64, 48);
        Assert.NotSame(before, ring.YPlane(0));
        Assert.Equal(128 * 96, ring.YPlane(0).Length);
    }

    [Fact]
    public void ReconfiguringForgetsFramesInFlight()
    {
        var ring = Configured();
        int slot = ring.AcquireForWrite();
        ring.CompleteWrite(slot);
        ring.Configure(64, 48, 64, 48, 32, 24);
        Assert.False(ring.TryTakeLatest(out _));
        Assert.Equal(-1, ring.PresentingSlot);
    }

    [Fact]
    public void UnconfiguredRingRefusesEverythingButTake()
    {
        var ring = new DewarpFrameRing();
        Assert.False(ring.IsConfigured);
        Assert.Throws<InvalidOperationException>(() => ring.AcquireForWrite());
        Assert.Throws<InvalidOperationException>(() => ring.FrameOf(0));
        Assert.False(ring.TryTakeLatest(out _));
    }

    [Theory]
    [InlineData(0, 48, 64, 48, 32, 24)]
    [InlineData(64, 48, 63, 48, 32, 24)]
    [InlineData(64, 48, 64, 47, 32, 24)]
    [InlineData(64, 48, 64, 48, 31, 24)]
    [InlineData(65, 48, 65, 48, 32, 24)] // odd width needs 33 chroma bytes
    [InlineData(64, 49, 64, 49, 32, 24)] // odd height needs 25 chroma rows
    public void ConfigureRejectsBuffersSmallerThanTheFrame(int w, int h, int yPitch, int yLines,
        int cPitch, int cLines)
    {
        var ring = new DewarpFrameRing();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ring.Configure(w, h, yPitch, yLines, cPitch, cLines));
    }

    [Fact]
    public void ConcurrentWriterAndReaderNeverCollide()
    {
        // The property the whole type exists for: across thousands of frames written on one
        // thread and taken on another, the slot handed to the writer is never the one the reader
        // holds. Checked by having the writer stamp its slot and the reader verify the stamp of
        // the slot it holds is unchanged while it holds it.
        var ring = Configured();
        const int frames = 20000;
        var failures = 0;

        var writer = Task.Run(() =>
        {
            for (int i = 1; i <= frames; i++)
            {
                int slot = ring.AcquireForWrite();
                var plane = ring.YPlane(slot);
                plane[0] = (byte)i;
                plane[1] = (byte)(i >> 8);
                ring.CompleteWrite(slot);
            }
        });

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted || ring.TryTakeLatest(out _))
            {
                if (!ring.TryTakeLatest(out int slot))
                    continue;
                var plane = ring.YPlane(slot);
                int stamp = plane[0] | (plane[1] << 8);
                Thread.SpinWait(200);
                int again = plane[0] | (plane[1] << 8);
                if (stamp != again)
                    Interlocked.Increment(ref failures);
            }
        });

        Task.WaitAll(writer, reader);
        Assert.Equal(0, failures);
        Assert.Equal(frames, ring.Completed);
        Assert.Equal(frames, ring.Dropped + ring.Taken);
    }
}

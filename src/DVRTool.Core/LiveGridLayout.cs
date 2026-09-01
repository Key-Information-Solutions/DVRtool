namespace DVRTool.Core;

/// <summary>
/// The arithmetic behind a multi-camera live grid: how many tiles fit on a page, which
/// channels land on which page, and how the tiles are arranged.
/// </summary>
/// <remarks>
/// <para>
/// The page size is the load-bearing number here, and it is a measurement rather than a
/// preference. Every tile is an independent LibVLC player, which on Windows costs roughly
/// 85 threads and 35–40 MB, and running them side by side against a live recorder showed a
/// cliff: 16 and 20 tiles held at about 9 % CPU on a fast workstation, 21 tiles jumped to
/// 35–53 %. A page of 16 keeps every machine on the flat part of that curve, and happens to
/// match the 4×4 view iVMS-4200 defaults to. The recorder side and the SDK side are not the
/// constraint — 16 previews on one login cost nothing measurable — so the cap is about the
/// viewer, not the device. See <c>docs/hikvision-sdk-live.md</c> §7.
/// </para>
/// <para>
/// Pure so it can be tested without a window. The GUI owns the tiles; this decides where
/// they go.
/// </para>
/// </remarks>
public static class LiveGridLayout
{
    /// <summary>Most tiles one page shows. See the class remarks for where 16 comes from.</summary>
    public const int MaxTilesPerPage = 16;

    /// <summary>
    /// How long to wait between starting consecutive tiles. Every preview opens with a
    /// keyframe, and sixteen keyframes in the same instant were measured as a 25–34 Mbps
    /// burst on a stream that averages 7 Mbps — enough to stall a site's uplink for the
    /// first second. Spreading the starts flattens the burst without a visible delay.
    /// </summary>
    public static readonly TimeSpan StartStagger = TimeSpan.FromMilliseconds(100);

    /// <summary>Pages needed for <paramref name="channelCount"/> channels (0 for none).</summary>
    public static int PageCount(int channelCount) =>
        channelCount <= 0 ? 0 : (channelCount + MaxTilesPerPage - 1) / MaxTilesPerPage;

    /// <summary>
    /// The channels on a page. <paramref name="page"/> is 0-based and clamped, so a page
    /// index left over from a bigger system is never an error.
    /// </summary>
    public static IReadOnlyList<T> Page<T>(IReadOnlyList<T> items, int page)
    {
        ArgumentNullException.ThrowIfNull(items);
        int pages = PageCount(items.Count);
        if (pages == 0)
            return [];
        page = Math.Clamp(page, 0, pages - 1);
        int start = page * MaxTilesPerPage;
        int count = Math.Min(MaxTilesPerPage, items.Count - start);
        var slice = new T[count];
        for (int i = 0; i < count; i++)
            slice[i] = items[start + i];
        return slice;
    }

    /// <summary>Clamps a page index into range for a channel count; 0 when there is nothing.</summary>
    public static int ClampPage(int page, int channelCount)
    {
        int pages = PageCount(channelCount);
        return pages == 0 ? 0 : Math.Clamp(page, 0, pages - 1);
    }

    /// <summary>
    /// Columns for a tile count: the smallest square-ish arrangement, so one camera fills
    /// the pane, two sit side by side, up to four make a 2×2, up to nine a 3×3, and a full
    /// page is 4×4.
    /// </summary>
    public static int Columns(int tileCount) =>
        tileCount <= 1 ? 1 : (int)Math.Ceiling(Math.Sqrt(tileCount));

    /// <summary>Rows for a tile count at <see cref="Columns"/> columns.</summary>
    public static int Rows(int tileCount)
    {
        if (tileCount <= 1)
            return 1;
        int columns = Columns(tileCount);
        return (tileCount + columns - 1) / columns;
    }

    /// <summary>"Page 2 / 3 · cameras 17–32 of 40", or "" when there is nothing to show.</summary>
    public static string Describe(int page, int channelCount)
    {
        int pages = PageCount(channelCount);
        if (pages == 0)
            return "";
        page = Math.Clamp(page, 0, pages - 1);
        int first = page * MaxTilesPerPage + 1;
        int last = Math.Min(channelCount, first + MaxTilesPerPage - 1);
        return pages == 1
            ? $"{channelCount} camera(s)"
            : $"Page {page + 1} / {pages} · cameras {first}–{last} of {channelCount}";
    }
}

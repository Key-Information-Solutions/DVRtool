namespace DVRTool.Core;

/// <summary>
/// Streams a download to a ".part" sibling and promotes it to the real name only
/// after the copy completes, so a failed or canceled transfer never leaves a
/// truncated file at the destination (and never clobbers a previous good export).
/// </summary>
public static class AtomicDownload
{
    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destinationPath"/> via a
    /// same-directory temp file (atomic rename on completion). Returns total bytes
    /// copied; when 0, the destination is left untouched and no file is created.
    /// </summary>
    public static async Task<long> WriteAsync(Stream source, string destinationPath,
        IProgress<long>? bytesProgress, CancellationToken ct)
    {
        string tempPath = destinationPath + ".part";
        try
        {
            long total = 0;
            var file = File.Create(tempPath);
            await using (file)
            {
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    bytesProgress?.Report(total);
                }
            }
            // The stream must be fully closed before the rename.
            if (total > 0)
                File.Move(tempPath, destinationPath, overwrite: true);
            else
                File.Delete(tempPath);
            return total;
        }
        catch
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}

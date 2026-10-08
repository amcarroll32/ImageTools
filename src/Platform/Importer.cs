using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ImageTools.Model;
using ImageTools.Scanning;

namespace ImageTools.Platform;

public enum CopyOutcome
{
    Copied,
    Skipped,  // something is already at the target name: nothing is overwritten
    Failed,
}

public sealed record CopyResult(PlannedCopy Copy, CopyOutcome Outcome, string? Reason);

public sealed record ImportProgress(int FilesDone, long BytesDone, string Current, CopyResult? Last);

/// <summary>
/// Carries out an import plan. Copy only: the card is opened read-only, nothing is ever
/// overwritten, moved or deleted. Each file is copied under a temporary name, re-read and
/// checked against the SHA-256 of the original, given the original's dates, and only then
/// renamed to its final name, a rename that fails rather than replace anything. The only file
/// it ever removes is its own unfinished temporary copy. Every result goes to a log file.
/// </summary>
public sealed class Importer(ImportPlan plan, HashCache? cache)
{
    private const int BufferSize = 1 << 20;
    private const string TempSuffix = ".imagetools-partial";

    public string? LogPath { get; private set; }

    public List<CopyResult> Run(IProgress<ImportProgress> progress, CancellationToken ct)
    {
        var results = new List<CopyResult>();
        using var log = OpenLog();
        log?.WriteLine(ImportPlanner.Report(plan));
        log?.WriteLine($"Applied {DateTime.Now:d MMM yyyy HH:mm:ss}");
        log?.Flush();

        long bytesDone = 0;
        int filesDone = 0;
        foreach (var copy in plan.Copies)
        {
            if (ct.IsCancellationRequested)
                break;
            progress.Report(new ImportProgress(filesDone, bytesDone, copy.Source, null));

            CopyResult result;
            try
            {
                result = CopyOne(copy, read => progress.Report(new ImportProgress(filesDone, bytesDone + read, copy.Source, null)), ct);
            }
            catch (OperationCanceledException)
            {
                log?.WriteLine($"Stopped\t{copy.Source}\t(stopped before this file finished; nothing was left behind)");
                break;
            }
            results.Add(result);
            filesDone++;
            bytesDone += copy.Bytes;
            log?.WriteLine(result.Outcome switch
            {
                CopyOutcome.Copied => $"Copied\t{copy.Source}\t→\t{copy.Target}\tverified" + (copy.Note != null ? $"\t({copy.Note})" : ""),
                CopyOutcome.Skipped => $"Skipped\t{copy.Source}\t→\t{copy.Target}\t({result.Reason})",
                _ => $"Failed\t{copy.Source}\t→\t{copy.Target}\t({result.Reason})",
            });
            log?.Flush();
            progress.Report(new ImportProgress(filesDone, bytesDone, copy.Source, result));
        }

        log?.WriteLine();
        log?.WriteLine($"{results.Count(r => r.Outcome == CopyOutcome.Copied)} copied, {results.Count(r => r.Outcome == CopyOutcome.Skipped)} skipped, " +
                       $"{results.Count(r => r.Outcome == CopyOutcome.Failed)} failed" +
                       (results.Count < plan.Copies.Count ? $", {plan.Copies.Count - results.Count} not started (stopped)" : ""));
        return results;
    }

    private CopyResult CopyOne(PlannedCopy copy, Action<long> onBytes, CancellationToken ct)
    {
        if (File.Exists(copy.Target) || Directory.Exists(copy.Target))
            return new CopyResult(copy, CopyOutcome.Skipped, "something with this name appeared in the archive folder after the preview; nothing was overwritten");
        if (!File.Exists(copy.Source))
            return new CopyResult(copy, CopyOutcome.Failed, "the file is no longer on the card");

        string folder = Path.GetDirectoryName(copy.Target)!;
        string temp = Path.Join(folder, $"{Path.GetFileName(copy.Target)}.{Guid.NewGuid():N}{TempSuffix}");
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Directory.CreateDirectory(folder); // creates only; does nothing if it's there

            // One read of the card: copy and hash together.
            UInt128 full, head;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            using (var headSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                using var source = new FileStream(copy.Source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
                using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                long read = 0, lastReport = 0;
                int n;
                while ((n = source.Read(buffer, 0, BufferSize)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.AppendData(buffer, 0, n);
                    if (read < Analyzer.HeadBytes)
                        headSha.AppendData(buffer, 0, (int)Math.Min(n, Analyzer.HeadBytes - read));
                    target.Write(buffer, 0, n);
                    read += n;
                    if (read - lastReport >= 8 << 20)
                    {
                        onBytes(read);
                        lastReport = read;
                    }
                }
                target.Flush(flushToDisk: true);
                full = Analyzer.ToHash(sha.GetHashAndReset());
                head = Analyzer.ToHash(headSha.GetHashAndReset());
            }

            // Re-read what was written; it must match the original exactly.
            if (HashFile(temp, buffer, ct) != full)
            {
                TryDeleteTemp(temp);
                return new CopyResult(copy, CopyOutcome.Failed, "the copy didn't match the original when checked, so it wasn't kept");
            }

            File.SetCreationTimeUtc(temp, File.GetCreationTimeUtc(copy.Source));
            File.SetLastWriteTimeUtc(temp, File.GetLastWriteTimeUtc(copy.Source));

            try
            {
                File.Move(temp, copy.Target, overwrite: false);
            }
            catch (IOException) when (File.Exists(copy.Target))
            {
                TryDeleteTemp(temp);
                return new CopyResult(copy, CopyOutcome.Skipped, "something with this name appeared in the archive folder during the import; nothing was overwritten");
            }

            Remember(copy, head, full);
            return new CopyResult(copy, CopyOutcome.Copied, null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteTemp(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemp(temp);
            return new CopyResult(copy, CopyOutcome.Failed, ex.Message.TrimEnd('.', ' ', '\r', '\n'));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static UInt128 HashFile(string path, byte[] buffer, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        int n;
        while ((n = stream.Read(buffer, 0, BufferSize)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
        }
        return Analyzer.ToHash(sha.GetHashAndReset());
    }

    /// <summary>The copy's hashes and photo details go straight into the cache, so the next check doesn't read it again.</summary>
    private void Remember(PlannedCopy copy, UInt128 head, UInt128 full)
    {
        if (cache == null || copy.IsSidecar)
            return;
        var info = new FileInfo(copy.Target);
        var entry = cache.Get(copy.Target, info.Length, info.LastWriteTimeUtc.Ticks);
        entry.Head = head;
        entry.HasHead = true;
        entry.Full = full;
        entry.HasFull = true;
        entry.Photo = copy.Image?.Photo;
        cache.MarkDirty();
    }

    /// <summary>Removes this import's own unfinished temporary file; never anything else.</summary>
    private static void TryDeleteTemp(string temp)
    {
        if (!temp.EndsWith(TempSuffix, StringComparison.Ordinal))
            return;
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind with a name that makes clear it's an unfinished copy.
        }
    }

    /// <summary>A new log file next to the exe ("ImageTools logs"), or in %LocalAppData%\ImageTools\logs.</summary>
    private StreamWriter? OpenLog()
    {
        string name = $"import {DateTime.Now:yyyy-MM-dd HHmmss}.txt";
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "ImageTools logs"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageTools", "logs"),
                 })
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, name);
                var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                LogPath = path;
                return writer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the next place.
            }
        }
        return null;
    }
}

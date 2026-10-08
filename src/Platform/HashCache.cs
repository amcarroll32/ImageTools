using System.Collections.Concurrent;
using System.IO;
using System.Text;
using ImageTools.Model;

namespace ImageTools.Platform;

/// <summary>
/// Remembers hashes and photo details per file path, keyed on size and last-modified time, so a
/// rescan only reads files that changed. Stored next to the exe (ImageTools.cache) like the
/// settings, or in %LocalAppData%\ImageTools when that folder isn't writable.
/// </summary>
public sealed class HashCache
{
    private const string Magic = "ITC1";

    private static readonly string PortablePath = Path.Combine(AppContext.BaseDirectory, "ImageTools.cache");

    private static readonly string LocalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImageTools", "cache.bin");

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _saveLock = new();
    private volatile bool _dirty;
    private int _run;

    public sealed class Entry(long size, long modified)
    {
        public long Size { get; } = size;
        public long Modified { get; } = modified;
        public UInt128 Head { get; set; }
        public UInt128 Full { get; set; }
        public bool HasHead { get; set; }
        public bool HasFull { get; set; }
        public PhotoInfo? Photo { get; set; }

        /// <summary>Looked up during this session, so it still describes a file that exists.</summary>
        public bool Seen { get; set; }

        /// <summary>The check (see <see cref="BeginRun"/>) that first saw the file as it is now; 0 = loaded from disk.</summary>
        public int CreatedInRun { get; set; }
    }

    public int Count => _entries.Count;

    /// <summary>Starts a new duplicate check; entries created from now on are counted as new or changed files.</summary>
    public int BeginRun() => Interlocked.Increment(ref _run);

    /// <summary>The entry for a file as it is now; a stale entry (size or date changed) is replaced.</summary>
    public Entry Get(string path, long size, long modified)
    {
        if (!_entries.TryGetValue(path, out var entry) || entry.Size != size || entry.Modified != modified)
        {
            entry = new Entry(size, modified) { CreatedInRun = _run };
            _entries[path] = entry;
            _dirty = true;
        }
        entry.Seen = true;
        return entry;
    }

    /// <summary>The cached entry for a file if it's still current, without marking it as seen.</summary>
    public Entry? Peek(string path, long size, long modified) =>
        _entries.TryGetValue(path, out var entry) && entry.Size == size && entry.Modified == modified ? entry : null;

    /// <summary>Forgets every hash and photo detail, so the next check reads everything again.</summary>
    public void Clear()
    {
        _entries.Clear();
        _dirty = true;
    }

    public void MarkDirty() => _dirty = true;

    public static HashCache Load()
    {
        var cache = new HashCache();
        foreach (var path in new[] { PortablePath, LocalPath })
        {
            try
            {
                if (!File.Exists(path))
                    continue;
                using var reader = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 16), Encoding.UTF8);
                if (new string(reader.ReadChars(4)) != Magic)
                    continue;
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string key = reader.ReadString();
                    var entry = new Entry(reader.ReadInt64(), reader.ReadInt64());
                    byte flags = reader.ReadByte();
                    if ((flags & 1) != 0)
                    {
                        entry.HasHead = true;
                        entry.Head = ReadHash(reader);
                    }
                    if ((flags & 2) != 0)
                    {
                        entry.HasFull = true;
                        entry.Full = ReadHash(reader);
                    }
                    if ((flags & 4) != 0)
                    {
                        long taken = reader.ReadInt64();
                        int width = reader.ReadInt32(), height = reader.ReadInt32();
                        byte rating = reader.ReadByte(), orientation = reader.ReadByte();
                        string camera = reader.ReadString(), tags = reader.ReadString();
                        entry.Photo = new PhotoInfo(taken, camera.Length > 0 ? camera : null, width, height, rating,
                            tags.Length > 0 ? tags : null, orientation);
                    }
                    cache._entries[key] = entry;
                }
                return cache;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException or FormatException)
            {
                // A damaged cache only costs a slower check: start again with what was read.
                cache._entries.Clear();
            }
        }
        return cache;
    }

    /// <summary>
    /// Writes the cache, dropping entries under <paramref name="scannedRoots"/> that this session
    /// didn't see (deleted or moved files). Entries for drives that weren't scanned are kept.
    /// </summary>
    public void Save(IReadOnlyCollection<string> scannedRoots)
    {
        lock (_saveLock)
        {
            if (!_dirty)
                return;
            _dirty = false; // set again by any change made while writing
            var keep = _entries.Where(kv => kv.Value.Seen || !scannedRoots.Any(r => kv.Key.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                           .ToList();
            if (!TryWrite(PortablePath, keep) && !TryWrite(LocalPath, keep))
                _dirty = true;
        }
    }

    private static bool TryWrite(string path, List<KeyValuePair<string, Entry>> entries)
    {
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var writer = new BinaryWriter(new BufferedStream(File.Create(temp), 1 << 16), Encoding.UTF8))
            {
                writer.Write(Magic.ToCharArray());
                writer.Write(entries.Count);
                foreach (var (key, e) in entries)
                {
                    writer.Write(key);
                    writer.Write(e.Size);
                    writer.Write(e.Modified);
                    var photo = e.Photo;
                    writer.Write((byte)((e.HasHead ? 1 : 0) | (e.HasFull ? 2 : 0) | (photo != null ? 4 : 0)));
                    if (e.HasHead) WriteHash(writer, e.Head);
                    if (e.HasFull) WriteHash(writer, e.Full);
                    if (photo != null)
                    {
                        writer.Write(photo.DateTaken);
                        writer.Write(photo.Width);
                        writer.Write(photo.Height);
                        writer.Write((byte)photo.Rating);
                        writer.Write((byte)photo.Orientation);
                        writer.Write(photo.Camera ?? "");
                        writer.Write(photo.Tags ?? "");
                    }
                }
            }
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private static UInt128 ReadHash(BinaryReader reader)
    {
        ulong upper = reader.ReadUInt64(), lower = reader.ReadUInt64();
        return new UInt128(upper, lower);
    }

    private static void WriteHash(BinaryWriter writer, UInt128 hash)
    {
        writer.Write((ulong)(hash >> 64));
        writer.Write((ulong)hash);
    }
}

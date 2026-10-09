using System.IO;
using ImageTools.Platform;

string root = Path.Combine(Path.GetTempPath(), "ImageToolsImporterTest");
if (Directory.Exists(root)) Directory.Delete(root, true);
string card = Path.Combine(root, "card"), archive = Path.Combine(root, "archive", "100CANON");
Directory.CreateDirectory(card);
var rnd = new Random(1);
string Make(string name, int bytes) { var p = Path.Combine(card, name); var b = new byte[bytes]; rnd.NextBytes(b); File.WriteAllBytes(p, b); File.SetLastWriteTimeUtc(p, new DateTime(2025, 6, 18, 10, 30, 0, DateTimeKind.Utc)); return p; }
var a = Make("IMG_0001.JPG", 3_000_000);
var b = Make("IMG_0002.CR2", 40_000);
var c = Make("IMG_0003.JPG", 500_000);
var big = Make("IMG_0004.CR2", 200_000_000);
int fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }
var noProgress = new Progress<ImportProgress>(_ => { });

// 1. Normal copies into a folder that doesn't exist yet, plus a target that appears after the preview, plus a vanished source.
var plan = new ImportPlan("T:", [archive], [
    new PlannedCopy(a, Path.Combine(archive, "IMG_0001.JPG"), 3_000_000, null, false),
    new PlannedCopy(b, Path.Combine(archive, "IMG_0002.CR2"), 40_000, null, false),
    new PlannedCopy(c, Path.Combine(archive, "IMG_0003.JPG"), 500_000, null, false),
    new PlannedCopy(Path.Combine(card, "missing.JPG"), Path.Combine(archive, "missing.JPG"), 1, null, false),
], 3_540_001, []);
Directory.CreateDirectory(archive);
File.WriteAllText(Path.Combine(archive, "IMG_0003.JPG"), "someone else's file"); // appeared after the preview
var importer = new Importer(plan, null);
var results = importer.Run(noProgress, CancellationToken.None);
Check(results[0].Outcome == CopyOutcome.Copied && File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(Path.Combine(archive, "IMG_0001.JPG"))), "copy is byte-identical");
Check(File.GetLastWriteTimeUtc(Path.Combine(archive, "IMG_0001.JPG")) == File.GetLastWriteTimeUtc(a), "modified date kept");
Check(results[1].Outcome == CopyOutcome.Copied, "small file copied");
Check(results[2].Outcome == CopyOutcome.Skipped && File.ReadAllText(Path.Combine(archive, "IMG_0003.JPG")) == "someone else's file", "existing target skipped and left untouched");
Check(results[3].Outcome == CopyOutcome.Failed, "missing source reported as failed");
Check(File.Exists(a) && File.Exists(b) && File.Exists(c), "sources untouched");
Check(!Directory.EnumerateFiles(archive, "*.imagetools-partial").Any(), "no temporary files left");
Check(importer.LogPath != null && File.ReadAllText(importer.LogPath).Contains("Skipped"), $"log written: {importer.LogPath}");

// 2. Stopping mid-file leaves nothing behind.
var plan2 = new ImportPlan("T:", [], [new PlannedCopy(big, Path.Combine(archive, "IMG_0004.CR2"), 200_000_000, null, false)], 200_000_000, []);
using var cts = new CancellationTokenSource();
var importer2 = new Importer(plan2, null);
var run = Task.Run(() => importer2.Run(new Progress<ImportProgress>(p => { if (p.BytesDone > 0) cts.Cancel(); }), cts.Token));
var results2 = run.Result;
Check(results2.Count == 0 && !File.Exists(Path.Combine(archive, "IMG_0004.CR2")), "stopped copy has no final file");
Check(!Directory.EnumerateFiles(archive, "*.imagetools-partial").Any(), "stopped copy left no temporary file");

// 3. The hash cache learns the copy.
var cache = new HashCache();
var plan3 = new ImportPlan("T:", [], [new PlannedCopy(a, Path.Combine(root, "archive", "IMG_0001-cached.JPG"), 3_000_000, null, false)], 3_000_000, []);
new Importer(plan3, cache).Run(noProgress, CancellationToken.None);
var info = new FileInfo(Path.Combine(root, "archive", "IMG_0001-cached.JPG"));
var entry = cache.Peek(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
Check(entry is { HasFull: true, HasHead: true }, "copy's hashes are cached");

foreach (var log in new[] { importer.LogPath, importer2.LogPath }) if (log != null) File.Delete(log);
Directory.Delete(root, true);
Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails;


using System.IO;
using ImageTools.Model;
using ImageTools.Platform;
using ImageTools.Scanning;

string root = Path.Combine(Path.GetTempPath(), "ImageToolsAnalyzerTest");
if (Directory.Exists(root)) Directory.Delete(root, true);
string cardDir = Path.Combine(root, "card") + "\\", archDir = Path.Combine(root, "arch") + "\\";
Directory.CreateDirectory(Path.Combine(cardDir, "100CANON"));
Directory.CreateDirectory(Path.Combine(archDir, "100CANON"));
var rnd = new Random(7);
byte[] Bytes(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
void Write(string path, byte[] b) { File.WriteAllBytes(path, b); File.SetLastWriteTimeUtc(path, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)); }
const int Size = 2_000_000;

var a = Bytes(Size);                       // identical on card and archive
var b = Bytes(Size); var b2 = (byte[])b.Clone(); b2[Size - 10] ^= 0xFF;          // differs at the very end
var c = Bytes(Size); var c2 = (byte[])c.Clone(); c2[Size / 8] ^= 0xFF;           // differs where nothing is sampled
var d = Bytes(Size); var d2 = (byte[])d.Clone(); d2[Size / 8 + 7] ^= 0xFF;       // two archive files differing unsampled
Write(cardDir + @"100CANON\a.jpg", a); Write(archDir + @"100CANON\a.jpg", a);
Write(cardDir + @"100CANON\b.jpg", b); Write(archDir + @"100CANON\b.jpg", b2);
Write(cardDir + @"100CANON\c.jpg", c); Write(archDir + @"100CANON\c.jpg", c2);
Write(cardDir + @"100CANON\d.jpg", d); Write(archDir + @"100CANON\d1.jpg", d); Write(archDir + @"100CANON\d2.jpg", d2);

FsNode Tree(string dir, bool card)
{
    var drive = new FsNode(dir, NodeKind.Drive, null) { Hardware = card ? new DriveHardware(DriveKind.MemoryCard, null, 'X') : new DriveHardware(DriveKind.Ssd, null, 'Y') };
    var folder = new FsNode("100CANON", NodeKind.Directory, drive);
    folder.Children = Directory.GetFiles(Path.Combine(dir, "100CANON")).Select(f => new FsNode(Path.GetFileName(f), NodeKind.File, folder)
        { Size = new FileInfo(f).Length, FileCount = 1, LastWrite = File.GetLastWriteTimeUtc(f).Ticks, Format = ImageFormat.Jpeg }).ToList();
    drive.Children = [folder];
    return drive;
}
var cardTree = Tree(cardDir, true); var archTree = Tree(archDir, false);
FsNode File_(FsNode drive, string name) => drive.Children![0].Children!.First(f => f.Name == name);

var cache = new HashCache();
var result = new Analyzer([cardTree, archTree], cache).FindDuplicates(CancellationToken.None);
int fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }
HashCache.Entry E(FsNode n) => cache.Peek(n.FullPath, n.Size, n.LastWrite)!;

var ca = File_(cardTree, "a.jpg");
Check(ca.Dup == DupStatus.Duplicate && ca.Group!.ByFingerprint && ca.Group.Members.Contains(File_(archTree, "a.jpg")), "identical card file matched to its archive copy by fingerprint");
Check(!E(ca).HasFull, "card file was not read in full");
Check(E(File_(archTree, "a.jpg")).HasFull, "archive copy was hashed in full");
Check(File_(cardTree, "b.jpg").Dup == DupStatus.Unique && File_(archTree, "b.jpg").Dup == DupStatus.Unique, "difference at the end is caught by the fingerprint (no full read needed)");
Check(!E(File_(archTree, "b.jpg")).HasFull, "near-miss archive file wasn't read in full either");
Check(File_(cardTree, "c.jpg").Dup == DupStatus.Duplicate, "known limit: a difference in unsampled bytes matches by fingerprint (card side only)");
Check(File_(archTree, "d1.jpg").Dup == DupStatus.Unique && File_(archTree, "d2.jpg").Dup == DupStatus.Unique, "fixed-drive files differing in unsampled bytes are told apart by the full hash");
Check(File_(cardTree, "d.jpg").Dup == DupStatus.Unique, "ambiguous card file isn't treated as archived (it would be copied)");

Directory.Delete(root, true);
Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails;

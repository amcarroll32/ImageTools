using ImageTools.Model;

// Pairing rules on an in-memory scan: no files are read, duplicate groups are set up by hand.
// Card X: has DCIM\100CANON (p1–p6) and DCIM\101CANON (q1–q2, archived nowhere).
// PC E: has Archive\100CANON (p1–p4, the real archive) and Backup\Old (p1–p3, also enough to match).
var card = new FsNode(@"X:\", NodeKind.Drive, null) { Hardware = new DriveHardware(DriveKind.MemoryCard, null, 'X'), VolumeSerial = 0x1234ABCD, Capacity = 64_000_000_000 };
var pc = new FsNode(@"E:\", NodeKind.Drive, null) { Hardware = new DriveHardware(DriveKind.Ssd, null, 'E') };
FsNode Dir(FsNode parent, string name) { var d = new FsNode(name, NodeKind.Directory, parent); (parent.Children ??= []).Add(d); return d; }
FsNode Img(FsNode parent, string name, int day)
{
    var f = new FsNode(name, NodeKind.File, parent) { Size = 1000 + name.GetHashCode() % 500 + 500, FileCount = 1, Dup = DupStatus.Unique, LastWrite = new DateTime(2025, 1, day).Ticks };
    (parent.Children ??= []).Add(f);
    return f;
}
var c100 = Dir(Dir(card, "DCIM"), "100CANON");
var c101 = Dir(c100.Parent!, "101CANON");
var archive = Dir(Dir(pc, "Archive"), "100CANON");
var backup = Dir(Dir(pc, "Backup"), "Old");
var cardImages = Enumerable.Range(1, 6).Select(i => Img(c100, $"p{i}.jpg", i)).ToList();
Img(c101, "q1.jpg", 10);
Img(c101, "q2.jpg", 11);
void Same(params FsNode[] files) { var g = new DuplicateGroup(files[0].Size, [.. files], true); foreach (var f in files) (f.Dup, f.Group) = (DupStatus.Duplicate, g); }
for (int i = 0; i < 4; i++)
{
    var copies = new List<FsNode> { cardImages[i], Img(archive, cardImages[i].Name, i + 1) };
    if (i < 3)
        copies.Add(Img(backup, cardImages[i].Name, i + 1));
    Same([.. copies]);
}
FsNode[] drives = [card, pc];

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fails++; }
var remembered = new Dictionary<string, string>();
string k100 = CardSync.Key(card, c100), k101 = CardSync.Key(card, c101);
(CardFolder? F100, CardFolder? F101, CardStatus S) Run(PairingChoices choices)
{
    var s = CardSync.Find(drives, remembered, choices, out _).Single();
    return (s.Folders.FirstOrDefault(f => f.Folder == c100), s.Folders.FirstOrDefault(f => f.Folder == c101), s);
}
PairingChoices None() => new([], []);

// 1. Automatic: the folder holding most of the card's photos wins; 101CANON goes next to it.
var (a, b, _) = Run(None());
Check(a is { Source: ArchiveSource.Matched } && a.Archive == archive && a.Shared == 4, "automatic match picks the folder with most photos");
Check(remembered.GetValueOrDefault(k100) == @"E:\Archive\100CANON", "automatic match is remembered");
Check(b is { Source: ArchiveSource.NextToSiblings } && b.ArchivePath == @"E:\Archive\101CANON" && b.Archive == null, "sibling folder goes next to it");

// 2. Ruling out the archive falls back to the next folder that qualifies, and forgets the remembered pairing.
var ruleOut = None();
ruleOut.Rejected[k100] = [@"e:\archive\100canon"]; // case doesn't matter
(a, b, _) = Run(ruleOut);
Check(a is { Source: ArchiveSource.Matched } && a.Archive == backup, "ruled-out folder skipped; next best match used");
Check(!remembered.ContainsKey(k100) || remembered[k100] != @"E:\Archive\100CANON", "ruled-out folder no longer remembered");
Check(a!.Items.Count(i => i.Bucket == ImportBucket.Elsewhere) == 1, "photo only in the ruled-out folder counts as elsewhere");
Check(b?.ArchivePath == @"E:\Backup\101CANON", "sibling follows the new match");

// 3. Ruling out both leaves the folder unmatched (the remembered one isn't used either).
remembered[k100] = @"E:\Archive\100CANON";
ruleOut.Rejected[k100].Add(@"E:\Backup\Old");
var (a3, b3, s3) = Run(ruleOut);
Check(a3 == null && s3.Unmatched.Contains(c100), "both ruled out: no archive folder");
Check(!remembered.ContainsKey(k100), "remembered pairing to a ruled-out folder is dropped");
Check(b3 == null && s3.Unmatched.Contains(c101), "no sibling to follow either");

// 4. A chosen folder beats the automatic match, even when it doesn't exist yet.
var choose = None();
choose.Chosen[k100] = @"E:\Pictures\Canon";
var (a4, b4, _) = Run(choose);
Check(a4 is { Source: ArchiveSource.Chosen, Archive: null } && a4.ArchivePath == @"E:\Pictures\Canon", "chosen folder wins, even if new");
Check(a4!.Items.Count == 6 && a4.Count(ImportBucket.Elsewhere) == 4 && a4.Count(ImportBucket.New) == 2, "new folder: copies elsewhere aren't ticked, the rest are new");
Check(b4?.ArchivePath == @"E:\Pictures\101CANON", "sibling goes next to the chosen folder");
Check(remembered.GetValueOrDefault(k100) != @"E:\Pictures\Canon", "a choice isn't written into the automatic memory");

// 5. Choosing an existing folder finds it in the scan; ruling out a sibling's spot leaves it unmatched.
var both = None();
both.Chosen[k100] = @"E:\Backup\Old";
both.Rejected[k101] = [@"E:\Backup\101CANON"];
var (a5, b5, s5) = Run(both);
Check(a5 is { Source: ArchiveSource.Chosen } && a5.Archive == backup && a5.Shared == 3, "chosen existing folder is found and compared");
Check(b5 == null && s5.Unmatched.Contains(c101), "ruled-out sibling spot isn't used");

// 6. Cards without a serial number can't be paired by hand: choices are ignored.
card.VolumeSerial = 0;
var choose0 = None();
choose0.Chosen[CardSync.Key(card, c100)] = @"E:\Pictures\Canon";
var (a6, _, _) = Run(choose0);
Check(a6 is { Source: ArchiveSource.Matched } && a6.Archive == archive, "no serial: choice ignored, automatic match used");
Check(!CardSync.CanRemember(card), "no serial: can't remember");

Console.WriteLine(fails == 0 ? "ALL PASSED" : $"{fails} FAILED");
return fails;

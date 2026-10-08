using System.Runtime.InteropServices;

namespace ImageTools.Platform;

public static class VolumeInfo
{
    /// <summary>The volume serial number (changes only when the card is formatted on a PC), or 0 if unknown.</summary>
    public static uint Serial(string root) =>
        GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0) ? serial : 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, char[]? volumeName, int volumeNameSize,
        out uint serialNumber, out uint maxComponentLength, out uint fileSystemFlags, char[]? fileSystemName, int fileSystemNameSize);
}

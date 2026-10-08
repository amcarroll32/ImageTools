using System.Runtime.InteropServices;
using System.Text;

namespace ImageTools.Platform;

public static class NetworkDrives
{
    /// <summary>
    /// The UNC path a mapped drive points at (e.g. \\nas\media), read from the local mapping
    /// table, so it never waits on the network. Null if the letter isn't a mapped drive.
    /// </summary>
    public static string? RemotePath(string driveName)
    {
        var buffer = new StringBuilder(512);
        int length = buffer.Capacity;
        return WNetGetConnection(driveName.TrimEnd('\\'), buffer, ref length) == 0 ? buffer.ToString() : null;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
}

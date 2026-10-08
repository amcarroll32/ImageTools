using System.Runtime.InteropServices;
using ImageTools.Model;

namespace ImageTools.Platform;

/// <summary>
/// Reads date taken, camera, size, rating and tags through the Windows property system: the
/// same values Explorer shows, from the same property handlers (so HEIC and RAW work wherever
/// Windows has the codec). Read-only: the store is opened without write access.
/// </summary>
public static class PhotoProperties
{
    private static readonly Guid PhotoFmtid = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99");
    private static readonly Guid ImageFmtid = new("6444048F-4C8B-11D1-8B70-080036B11A03");

    private static PROPERTYKEY DateTaken = new(PhotoFmtid, 36867);
    private static PROPERTYKEY CameraMaker = new(PhotoFmtid, 271);
    private static PROPERTYKEY CameraModel = new(PhotoFmtid, 272);
    private static PROPERTYKEY Orientation = new(PhotoFmtid, 274);
    private static PROPERTYKEY Width = new(ImageFmtid, 3);
    private static PROPERTYKEY Height = new(ImageFmtid, 4);
    private static PROPERTYKEY Keywords = new(new Guid("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 5);
    private static PROPERTYKEY Rating = new(new Guid("64440492-4C8B-11D1-8B70-080036B11A03"), 9);

    private const int GPS_DEFAULT = 0;

    /// <summary>The photo's details, or null when Windows can't read the file's properties.</summary>
    public static PhotoInfo? Read(string path)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_DEFAULT, ref iid, out var store) != 0 || store == null)
            return null;
        try
        {
            long taken = GetFileTime(store, ref DateTaken);
            string? maker = GetString(store, ref CameraMaker)?.Trim();
            string? model = GetString(store, ref CameraModel)?.Trim();
            string? camera = (maker, model) switch
            {
                (null or "", null or "") => null,
                (null or "", _) => model,
                (_, null or "") => maker,
                _ when model!.StartsWith(maker!, StringComparison.OrdinalIgnoreCase) => model,
                _ => $"{maker} {model}",
            };
            var tags = GetStrings(store, ref Keywords);
            return new PhotoInfo(
                taken,
                camera,
                (int)GetUInt(store, ref Width),
                (int)GetUInt(store, ref Height),
                StarsFrom(GetUInt(store, ref Rating)),
                tags is { Count: > 0 } ? string.Join("; ", tags) : null,
                (int)GetUInt(store, ref Orientation));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>Windows stores ratings as 1–99; Explorer shows 1, 25, 50, 75 and 99 as one to five stars.</summary>
    private static int StarsFrom(uint rating) => rating switch
    {
        0 => 0,
        < 13 => 1,
        < 38 => 2,
        < 63 => 3,
        < 88 => 4,
        _ => 5,
    };

    private static long GetFileTime(IPropertyStore store, ref PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var v) != 0)
            return 0;
        try
        {
            return v.vt == VT_FILETIME && v.longVal > 0 ? DateTime.FromFileTimeUtc(v.longVal).Ticks : 0;
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    private static uint GetUInt(IPropertyStore store, ref PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var v) != 0)
            return 0;
        try
        {
            return v.vt switch
            {
                VT_UI4 or VT_I4 => v.uintVal,
                VT_UI2 or VT_I2 => v.uintVal & 0xFFFF,
                _ => 0,
            };
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    private static string? GetString(IPropertyStore store, ref PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var v) != 0)
            return null;
        try
        {
            return v.vt is VT_LPWSTR or VT_BSTR && v.p != IntPtr.Zero ? Marshal.PtrToStringUni(v.p) : null;
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    private static List<string>? GetStrings(IPropertyStore store, ref PROPERTYKEY key)
    {
        if (store.GetValue(ref key, out var v) != 0)
            return null;
        try
        {
            if (v.vt == (VT_VECTOR | VT_LPWSTR) && v.pElems != IntPtr.Zero)
            {
                var list = new List<string>((int)v.cElems);
                for (int i = 0; i < v.cElems; i++)
                {
                    var s = Marshal.PtrToStringUni(Marshal.ReadIntPtr(v.pElems, i * IntPtr.Size));
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s.Trim());
                }
                return list;
            }
            if (v.vt == VT_LPWSTR && v.p != IntPtr.Zero && Marshal.PtrToStringUni(v.p) is { Length: > 0 } one)
                return [one];
            return null;
        }
        finally
        {
            PropVariantClear(ref v);
        }
    }

    // ---- Interop ----

    private const ushort VT_I2 = 2, VT_I4 = 3, VT_BSTR = 8, VT_UI2 = 18, VT_UI4 = 19, VT_LPWSTR = 31, VT_FILETIME = 64, VT_VECTOR = 0x1000;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr p;
        [FieldOffset(8)] public uint uintVal;
        [FieldOffset(8)] public long longVal;
        [FieldOffset(8)] public uint cElems;
        [FieldOffset(16)] public IntPtr pElems;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);
}

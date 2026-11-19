// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace UeBar.Modules.QuickAccess.Platform;

/// <summary>
/// The real Windows shell: the "Apps" folder for the Start menu (classic and Store apps), starting through
/// <c>ShellExecuteEx</c> (via <see cref="Process.Start(ProcessStartInfo)"/>, which calls it in a
/// single-threaded apartment as shell extensions expect), shortcuts through <c>IShellLink</c>, icons
/// through <c>IShellItemImageFactory</c>. Checked: all of it works on thread-pool threads.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsShell : IQuickShell
{
    private const int ErrorCancelled = 1223;
    private const int ErrorNoAssociation = 1155; // also reported for a verb the file type does not have

    public Task<IReadOnlyList<AppInfo>> GetAppsAsync(CancellationToken cancellationToken)
    {
        var apps = new List<AppInfo>();
        var iid = typeof(IShellItem).GUID;
        Native.SHCreateItemFromParsingName("shell:AppsFolder", IntPtr.Zero, ref iid, out var folder);
        var bhid = Native.BhidEnumItems;
        var enumIid = typeof(IEnumShellItems).GUID;
        folder.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var enumObject);
        var items = (IEnumShellItems)enumObject;
        try
        {
            while (!cancellationToken.IsCancellationRequested && items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    item.GetDisplayName(Native.SigdnNormalDisplay, out var name);
                    item.GetDisplayName(Native.SigdnParentRelativeParsing, out var id);
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id) && !id.StartsWith("::", StringComparison.Ordinal))
                    {
                        apps.Add(new AppInfo(id, name));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }

        return Task.FromResult<IReadOnlyList<AppInfo>>(apps);
    }

    public LaunchResult Launch(LaunchRequest request)
    {
        // The started app may take the focus although the bar (in front a moment ago) started it (P2-4).
        Native.AllowSetForegroundWindow(Native.AsfwAny);
        try
        {
            var info = new ProcessStartInfo(request.File)
            {
                UseShellExecute = true,
                Arguments = request.Arguments ?? "",
                Verb = request.Verb ?? "",
                ErrorDialog = false,
            };
            using var process = Process.Start(info);
            return LaunchResult.Ok;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new LaunchResult(false, ex.Message, Cancelled: true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation && request.Verb is not null)
        {
            return new LaunchResult(false, ex.Message, NoSuchVerb: true);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new LaunchResult(false, ex.Message);
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool FolderExists(string path) => Directory.Exists(path);

    public ShortcutInfo? ReadShortcut(string path)
    {
        object? link = null;
        try
        {
            link = Activator.CreateInstance(Type.GetTypeFromCLSID(Native.ClsidShellLink, throwOnError: true)!);
            ((IPersistFile)link!).Load(path, 0);
            var shell = (IShellLinkW)link;
            var target = new StringBuilder(1024);
            shell.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(2048);
            shell.GetArguments(arguments, arguments.Capacity);
            return new ShortcutInfo(target.Length == 0 ? null : target.ToString(), arguments.Length == 0 ? null : arguments.ToString());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.ReleaseComObject(link);
            }
        }
    }

    public string? ReadUrlShortcut(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[]? GetIconPng(string parsingName, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (Native.CreateImageFactory(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory is null)
        {
            return null;
        }

        var bitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new Native.Size { Width = size, Height = size }, Native.SiigbfIconOnly | Native.SiigbfBiggerSizeOk, out bitmap) != 0 || bitmap == IntPtr.Zero)
            {
                return null;
            }

            return BitmapToPng(bitmap);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
            {
                Native.DeleteObject(bitmap);
            }

            Marshal.ReleaseComObject(factory);
        }
    }

    private static unsafe byte[]? BitmapToPng(IntPtr bitmap)
    {
        Native.Bitmap info;
        if (Native.GetObject(bitmap, sizeof(Native.Bitmap), out info) == 0 || info.Width <= 0 || info.Height <= 0)
        {
            return null;
        }

        var width = info.Width;
        var height = info.Height;
        var header = new Native.BitmapInfoHeader
        {
            Size = sizeof(Native.BitmapInfoHeader),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = Native.GetDC(IntPtr.Zero);
        try
        {
            fixed (byte* data = pixels)
            {
                if (Native.GetDIBits(dc, bitmap, 0, (uint)height, data, ref header, 0) == 0)
                {
                    return null;
                }
            }
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, dc);
        }

        return Png.FromBgra(pixels, width, height);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        void GetParent(out IShellItem ppsi);

        void GetDisplayName(int sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);

        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);

        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);

        void Skip(uint celt);

        void Reset();

        void Clone(out IEnumShellItems ppenum);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(Native.Size size, int flags, out IntPtr phbm);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    }

    private static class Native
    {
        public const int SigdnNormalDisplay = 0;
        public const int SigdnParentRelativeParsing = unchecked((int)0x80018001);
        public const int SiigbfBiggerSizeOk = 0x1;
        public const int SiigbfIconOnly = 0x4;
        public const int AsfwAny = -1;

        public static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
        public static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

        [StructLayout(LayoutKind.Sequential)]
        public struct Size
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Bitmap
        {
            public int Type;
            public int Width;
            public int Height;
            public int WidthBytes;
            public ushort Planes;
            public ushort BitsPixel;
            public IntPtr Bits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHCreateItemFromParsingName")]
        public static extern int CreateImageFactory(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory item);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        public static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        public static extern int GetObject(IntPtr handle, int size, out Bitmap bitmap);

        [DllImport("gdi32.dll")]
        public static extern unsafe int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte* bits, ref BitmapInfoHeader info, uint usage);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    }
}

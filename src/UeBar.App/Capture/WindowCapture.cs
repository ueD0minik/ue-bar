// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices;
using UeBar.App.Interop;
using UeBar.Bar;

namespace UeBar.App.Capture;

/// <summary>
/// The picture of one window, without what lies in front of it (P2-6-Fix 1). First Windows.Graphics.Capture (one
/// frame, no recording, on Windows 11 without the yellow frame where Windows allows it), then <c>PrintWindow</c> with
/// <c>PW_RENDERFULLCONTENT</c>; null if neither gives a picture (the picker then cuts from the frozen screen). Runs on
/// its own MTA thread with a time limit. The result is cut to what DWM shows (no invisible frame, no shadow) and gets
/// clean rounded corners.
/// </summary>
internal static class WindowCapture
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    public static (CapturedPixels Image, WindowCaptureMethod Method, PixelRect Bounds)? Capture(IntPtr hwnd, double scale, PixelRect monitor, Action<string> log)
    {
        if (!Win32.GetWindowRect(hwnd, out var r) || Win32.WindowBounds(hwnd) is not { } f)
        {
            return null;
        }

        var windowRect = PixelRect.FromEdges(r.Left, r.Top, r.Right, r.Bottom);
        var frame = PixelRect.FromEdges(f.Left, f.Top, f.Right, f.Bottom);
        (CapturedPixels Image, WindowCaptureMethod Method)? found = null;
        var thread = new Thread(() =>
        {
            found = WindowCaptureRules.FirstWorking(
                () => GraphicsCapture.Capture(hwnd),
                () => PrintWindowCapture(hwnd, windowRect),
                (method, why) => log($"Window capture: {method} did not work ({why})"));
        })
        {
            IsBackground = true,
            Name = "ueBar window capture",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!thread.Join(Limit) || found is not { } result)
        {
            return null;
        }

        if (WindowCaptureRules.VisiblePart(windowRect, frame, result.Image.Width, result.Image.Height) is not { } visible)
        {
            return null;
        }

        // P2-6-Fix 2: a maximized window reaches beyond its monitor – that part is not on the picture.
        var (image, bounds) = WindowCaptureRules.ClipToMonitor(WindowCaptureRules.Crop(result.Image, visible), frame, monitor);
        var windows11 = Environment.OSVersion.Version.Build >= 22000;
        int? preference = PinInterop.DwmGetWindowAttribute(hwnd, PinInterop.DWMWA_WINDOW_CORNER_PREFERENCE, out int value, sizeof(int)) == 0 ? value : null;
        WindowCaptureRules.CleanEdges(image, WindowCaptureRules.CornerRadiusPixels(windows11, Win32.IsZoomed(hwnd), preference, scale));
        return (image, result.Method, bounds);
    }

    /// <summary>The window draws itself into a bitmap of its full rectangle.</summary>
    private static CapturedPixels? PrintWindowCapture(IntPtr hwnd, PixelRect windowRect)
    {
        if (windowRect.IsEmpty)
        {
            return null;
        }

        var screen = PinInterop.GetDC(IntPtr.Zero);
        var memory = PinInterop.CreateCompatibleDC(screen);
        var info = new PinInterop.BITMAPINFO
        {
            bmiHeader = new PinInterop.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<PinInterop.BITMAPINFOHEADER>(),
                biWidth = windowRect.Width,
                biHeight = -windowRect.Height, // top-down
                biPlanes = 1,
                biBitCount = 32,
            },
        };
        var bitmap = PinInterop.CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero)
        {
            PinInterop.DeleteDC(memory);
            PinInterop.ReleaseDC(IntPtr.Zero, screen);
            return null;
        }

        var old = PinInterop.SelectObject(memory, bitmap);
        try
        {
            if (!PinInterop.PrintWindow(hwnd, memory, PinInterop.PW_RENDERFULLCONTENT))
            {
                return null;
            }

            PinInterop.GdiFlush();
            var data = new byte[windowRect.Width * windowRect.Height * 4];
            Marshal.Copy(bits, data, 0, data.Length);
            return new CapturedPixels(data, windowRect.Width, windowRect.Height);
        }
        finally
        {
            PinInterop.SelectObject(memory, old);
            PinInterop.DeleteObject(bitmap);
            PinInterop.DeleteDC(memory);
            PinInterop.ReleaseDC(IntPtr.Zero, screen);
        }
    }
}

/// <summary>
/// Windows.Graphics.Capture for a single frame of one window, through the plain COM/WinRT ABI (no projection
/// assembly in the app): D3D11 device → capture item for the window → free-threaded frame pool → session (no cursor,
/// no border where allowed) → first frame → staging texture → bytes. Everything is released at the end.
/// </summary>
internal static unsafe class GraphicsCapture
{
    private const int DirectXPixelFormatB8G8R8A8 = 87;
    private const int D3D11SdkVersion = 7;
    private const int D3DDriverTypeHardware = 1;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const int D3D11UsageStaging = 3;
    private const uint D3D11CpuAccessRead = 0x20000;
    private const int D3D11MapRead = 1;

    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IidFramePoolStatics2 = new("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
    private static readonly Guid IidSession2 = new("2C39AE40-7D2E-5044-804E-8B6799D4CF9E");
    private static readonly Guid IidSession3 = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid IidDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IidTexture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    private static readonly Guid IidDxgiDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid IidDirect3DDevice = new("A37624AB-8D5F-4650-9D3E-9EAE3D9BC670");
    private static readonly Guid IidClosable = new("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

    private static readonly TimeSpan FrameWait = TimeSpan.FromMilliseconds(800);

    /// <summary>One frame of the window, or null (not supported, window not capturable, no frame in time).</summary>
    public static CapturedPixels? Capture(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 18362)
        {
            return null; // Windows 10 1903 and later
        }

        RoInitialize(1); // MTA; "already initialized" is fine
        IntPtr d3d = 0, context = 0, dxgi = 0, inspectable = 0, device = 0, interop = 0, item = 0, statics = 0, pool = 0, session = 0;
        try
        {
            Check(D3D11CreateDevice(0, D3DDriverTypeHardware, 0, D3D11CreateDeviceBgraSupport, 0, 0, D3D11SdkVersion, out d3d, out _, out context));
            Check(QueryInterface(d3d, IidDxgiDevice, out dxgi));
            Check(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable));
            Check(QueryInterface(inspectable, IidDirect3DDevice, out device));

            interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", IidGraphicsCaptureItemInterop);
            var iidItem = IidGraphicsCaptureItem;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Vtbl(interop, 3))(interop, hwnd, &iidItem, &item));
            SizeInt32 size;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, SizeInt32*, int>)Vtbl(item, 7))(item, &size));
            if (size.Width <= 0 || size.Height <= 0)
            {
                return null;
            }

            statics = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", IidFramePoolStatics2);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, SizeInt32, IntPtr*, int>)Vtbl(statics, 6))(statics, device, DirectXPixelFormatB8G8R8A8, 1, size, &pool));
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vtbl(pool, 10))(pool, item, &session));

            // No cursor in the picture; no yellow frame on Windows 11 – if Windows does not allow it, the frame stays (it is
            // only on the screen, which is covered by the picker anyway, never in the picture).
            if (QueryInterface(session, IidSession2, out var session2) == 0)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Vtbl(session2, 7))(session2, 0);
                Release(session2);
            }

            if (QueryInterface(session, IidSession3, out var session3) == 0)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Vtbl(session3, 7))(session3, 0);
                Release(session3);
            }

            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(session, 6))(session)); // StartCapture
            var until = Environment.TickCount64 + (long)FrameWait.TotalMilliseconds;
            IntPtr frame = 0;
            while (frame == 0 && Environment.TickCount64 < until)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(pool, 7))(pool, &frame); // TryGetNextFrame
                if (frame == 0)
                {
                    Thread.Sleep(10); // a single frame, one short wait on our own thread
                }
            }

            if (frame == 0)
            {
                return null;
            }

            try
            {
                return Read(frame, d3d, context);
            }
            finally
            {
                Close(frame);
                Release(frame);
            }
        }
        finally
        {
            Close(session);
            Close(pool);
            Close(item);
            foreach (var p in new[] { session, pool, statics, item, interop, device, inspectable, dxgi, context, d3d })
            {
                Release(p);
            }
        }
    }

    private static CapturedPixels? Read(IntPtr frame, IntPtr d3d, IntPtr context)
    {
        IntPtr surface = 0, access = 0, texture = 0, staging = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vtbl(frame, 6))(frame, &surface));
            Check(QueryInterface(surface, IidDxgiInterfaceAccess, out access));
            var iidTexture = IidTexture2D;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vtbl(access, 3))(access, &iidTexture, &texture));

            Texture2DDesc desc;
            ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Vtbl(texture, 10))(texture, &desc);
            desc.Usage = D3D11UsageStaging;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11CpuAccessRead;
            desc.MiscFlags = 0;
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.SampleCount = 1;
            desc.SampleQuality = 0;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Vtbl(d3d, 5))(d3d, &desc, 0, &staging)); // CreateTexture2D
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Vtbl(context, 47))(context, staging, texture); // CopyResource

            MappedSubresource mapped;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, MappedSubresource*, int>)Vtbl(context, 14))(context, staging, 0, D3D11MapRead, 0, &mapped));
            try
            {
                var width = (int)desc.Width;
                var height = (int)desc.Height;
                var data = new byte[width * height * 4];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(mapped.Data + (nint)(y * (long)mapped.RowPitch), data, y * width * 4, width * 4);
                }

                return new CapturedPixels(data, width, height);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Vtbl(context, 15))(context, staging, 0); // Unmap
            }
        }
        finally
        {
            Release(staging);
            Release(texture);
            Release(access);
            Release(surface);
        }
    }

    private static IntPtr Factory(string className, Guid iid)
    {
        Check(WindowsCreateString(className, className.Length, out var name));
        try
        {
            Check(RoGetActivationFactory(name, ref iid, out var factory));
            return factory;
        }
        finally
        {
            WindowsDeleteString(name);
        }
    }

    private static IntPtr Vtbl(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];

    private static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result) => Marshal.QueryInterface(obj, in iid, out result);

    private static void Release(IntPtr obj)
    {
        if (obj != 0)
        {
            Marshal.Release(obj);
        }
    }

    /// <summary>WinRT objects of the capture hold resources until closed.</summary>
    private static void Close(IntPtr obj)
    {
        if (obj != 0 && QueryInterface(obj, IidClosable, out var closable) == 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl(closable, 6))(closable);
            Release(closable);
        }
    }

    private static void Check(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeInt32
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public IntPtr Data;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, int featureLevelCount, int sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(int type);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);
}

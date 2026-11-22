// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Dominik Poschinger

using System.Runtime.InteropServices;
using System.Text;

namespace UeBar.App.Interop;

/// <summary>Win32 for pins and picking a screen area (P2-5): window styles, sizing, screen capture, windows under the mouse.</summary>
internal static class PinInterop
{
    public const long WS_EX_LAYERED = 0x00080000;
    public const long WS_EX_TRANSPARENT = 0x00000020;

    public const int WM_NCHITTEST = 0x0084;
    public const int WM_SIZING = 0x0214;
    public const int WM_MOVING = 0x0216;
    public const int WM_ENTERSIZEMOVE = 0x0231;
    public const int WM_EXITSIZEMOVE = 0x0232;
    public const int WM_NCLBUTTONDOWN = 0x00A1;

    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    public const int WMSZ_LEFT = 1;
    public const int WMSZ_RIGHT = 2;
    public const int WMSZ_TOP = 3;
    public const int WMSZ_TOPLEFT = 4;
    public const int WMSZ_TOPRIGHT = 5;
    public const int WMSZ_BOTTOM = 6;
    public const int WMSZ_BOTTOMLEFT = 7;
    public const int WMSZ_BOTTOMRIGHT = 8;

    public const int SRCCOPY = 0x00CC0020;
    public const int CAPTUREBLT = 0x40000000;

    public const int DWMWA_CLOAKED = 14;

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, int rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmFlush();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    public static string TitleOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        return GetWindowText(hwnd, text, text.Capacity) > 0 ? text.ToString() : "";
    }

    public static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    /// <summary>Sets or clears extended window style bits.</summary>
    public static void SetExStyle(IntPtr hwnd, long set, long clear)
    {
        var ex = (long)Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        var next = (ex | set) & ~clear;
        if (next != ex)
        {
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(next));
        }
    }
}

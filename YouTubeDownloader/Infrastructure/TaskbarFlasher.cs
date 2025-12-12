using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YouTubeDownloader.Infrastructure;

internal static class TaskbarFlasher
{
    // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-flashwindowex

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_STOP = 0;
    private const uint FLASHW_TRAY = 2;
    private const uint FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    public static void Flash(Window? window, uint count = 3)
    {
        if (window == null)
        {
            return;
        }

        // アクティブなら点滅不要
        if (window.IsActive)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = count,
            dwTimeout = 0
        };

        FlashWindowEx(ref info);
    }

    public static void Stop(Window? window)
    {
        if (window == null)
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = handle,
            dwFlags = FLASHW_STOP,
            uCount = 0,
            dwTimeout = 0
        };

        FlashWindowEx(ref info);
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace TurboDL.UI;

internal static class WindowHelper
{
    static readonly List<Window> Open = new();

    /// <summary>Title bar follows the current theme (GNOME draws a dark or light title bar per window).</summary>
    public static void ApplyChrome(Window w)
    {
        Open.Add(w);
        w.Opened += (_, _) => SetChrome(w);
        w.Closed += (_, _) => Open.Remove(w);
    }

    public static void SetChromeAll()
    {
        foreach (var w in Open.ToArray()) SetChrome(w);
    }

    public static void SetChrome(Window w)
    {
        try
        {
            var h = w.TryGetPlatformHandle();
            if (h == null || h.Handle == IntPtr.Zero || h.HandleDescriptor != "XID") return;
            X11.SetThemeVariant(h.Handle, ThemeManager.IsDark);
        }
        catch { }
    }

    /// <summary>Keeps the window (and its title bar) on screen on small laptop displays / high scaling.</summary>
    public static void FitToScreen(Window w)
    {
        try
        {
            var screen = w.Screens.ScreenFromVisual(w) ?? w.Screens.Primary;
            if (screen == null) return;
            double scale = screen.Scaling > 0 ? screen.Scaling : 1;
            double waW = screen.WorkingArea.Width / scale;
            double waH = screen.WorkingArea.Height / scale;
            w.MinWidth = Math.Min(w.MinWidth, waW);
            w.MinHeight = Math.Min(w.MinHeight, waH);
            if (w.Width > waW * 0.95 || w.Height > waH * 0.95)
            {
                w.Width = Math.Min(w.Width, waW);
                w.Height = Math.Min(w.Height, waH);
                w.WindowState = WindowState.Maximized;
            }
        }
        catch { }
    }

    /// <summary>Sets _GTK_THEME_VARIANT, which GNOME's window manager uses to draw a dark or light title bar.</summary>
    static class X11
    {
        const string Lib = "libX11.so.6";
        [DllImport(Lib)] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Lib)] static extern IntPtr XInternAtom(IntPtr display, string atomName, int onlyIfExists);
        [DllImport(Lib)] static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
            int format, int mode, byte[] data, int nelements);
        [DllImport(Lib)] static extern int XFlush(IntPtr display);

        static IntPtr _display;
        static bool _failed;

        public static void SetThemeVariant(IntPtr window, bool dark)
        {
            if (_failed) return;
            try
            {
                if (_display == IntPtr.Zero)
                {
                    _display = XOpenDisplay(IntPtr.Zero);
                    if (_display == IntPtr.Zero) { _failed = true; return; }
                }
                IntPtr prop = XInternAtom(_display, "_GTK_THEME_VARIANT", 0);
                IntPtr utf8 = XInternAtom(_display, "UTF8_STRING", 0);
                byte[] value = System.Text.Encoding.ASCII.GetBytes(dark ? "dark" : "light");
                XChangeProperty(_display, window, prop, utf8, 8, 0 /* PropModeReplace */, value, value.Length);
                XFlush(_display);
            }
            catch { _failed = true; }
        }
    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using AllsioPush.Config;
using Microsoft.UI.Xaml;

namespace AllsioPush.UI.Windows;

internal static class WindowIcon
{
    private static string? _icoPath;
    private static System.Drawing.Icon? _icon;
    private static nint _hIcon;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    public static void Apply(Window window)
    {
        try
        {
            var path = EnsureIcoFromPng();
            if (path == null) return;

            window.AppWindow.SetIcon(path);

            // AppWindow.SetIcon reliably sets the title-bar icon (ICON_SMALL) but
            // not always the taskbar button. Send WM_SETICON(ICON_BIG) directly.
            if (_hIcon == 0)
            {
                _icon = new System.Drawing.Icon(path);
                _hIcon = _icon.Handle;
            }
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            const int WmSetIcon = 0x0080;
            SendMessage(hwnd, WmSetIcon, 0, _hIcon); // ICON_SMALL
            SendMessage(hwnd, WmSetIcon, 1, _hIcon); // ICON_BIG  → taskbar
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WindowIcon] failed: {ex.Message}"); }
    }

    // The bundled .ico is a low-res 16x16 4bpp image that renders as a black box in the
    // title bar. Wrap the high-res embedded PNG in a valid ICO container (Windows renders
    // PNG-encoded icon entries natively) and cache it to disk for AppWindow.SetIcon.
    private static string? EnsureIcoFromPng()
    {
        if (_icoPath != null && File.Exists(_icoPath)) return _icoPath;

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("icon-on-128.png");
        if (stream == null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var png = ms.ToArray();

        var dir = Path.Combine(SettingsManager.GetAppDataPath(), "Cache");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, "window-icon.ico");

        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            // ICONDIR
            w.Write((ushort)0);   // reserved
            w.Write((ushort)1);   // type = icon
            w.Write((ushort)1);   // image count
            // ICONDIRENTRY
            w.Write((byte)128);   // width
            w.Write((byte)128);   // height
            w.Write((byte)0);     // color count
            w.Write((byte)0);     // reserved
            w.Write((ushort)1);   // color planes
            w.Write((ushort)32);  // bits per pixel
            w.Write((uint)png.Length);
            w.Write((uint)22);    // offset of PNG data (6 + 16)
            w.Write(png);
        }

        _icoPath = outPath;
        return outPath;
    }
}

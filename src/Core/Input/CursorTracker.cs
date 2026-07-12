using System.Runtime.InteropServices;

namespace Core.Input;

public sealed record CursorSnapshot(bool Visible, bool HasPosition, int X, int Y);

public static class CursorTracker
{
    public static CursorSnapshot Get()
    {
        var cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursorInfo))
            return new CursorSnapshot(false, false, 0, 0);

        bool visible = (cursorInfo.flags & CURSOR_SHOWING) != 0;
        return new CursorSnapshot(visible, true, cursorInfo.ptScreenPos.X, cursorInfo.ptScreenPos.Y);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    private const int CURSOR_SHOWING = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }
}

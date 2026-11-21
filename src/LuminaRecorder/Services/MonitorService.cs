using System;
using System.Collections.Generic;
using System.Linq;

namespace LuminaRecorder.Services
{
    /// <summary>A rectangle in physical (device) pixels on the virtual desktop.</summary>
    public struct PixelRect : IEquatable<PixelRect>
    {
        public int X, Y, Width, Height;

        public PixelRect(int x, int y, int w, int h) { X = x; Y = y; Width = w; Height = h; }

        public int Right => X + Width;
        public int Bottom => Y + Height;
        public bool IsEmpty => Width <= 0 || Height <= 0;

        public static PixelRect FromLTRB(int l, int t, int r, int b) => new PixelRect(l, t, r - l, b - t);

        public bool IntersectsWith(PixelRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

        public PixelRect Intersect(PixelRect o)
        {
            int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
            return r > l && b > t ? FromLTRB(l, t, r, b) : new PixelRect();
        }

        public static PixelRect Union(IEnumerable<PixelRect> rects)
        {
            var list = rects.ToList();
            if (list.Count == 0) return new PixelRect();
            return FromLTRB(list.Min(r => r.X), list.Min(r => r.Y), list.Max(r => r.Right), list.Max(r => r.Bottom));
        }

        /// <summary>H.264 needs even dimensions.</summary>
        public PixelRect MakeEven() => new PixelRect(X, Y, Width & ~1, Height & ~1);

        public bool Equals(PixelRect o) => X == o.X && Y == o.Y && Width == o.Width && Height == o.Height;
        public override bool Equals(object obj) => obj is PixelRect p && Equals(p);
        public override int GetHashCode() => (X, Y, Width, Height).GetHashCode();
        public override string ToString() => $"{X},{Y},{Width},{Height}";

        public static bool TryParse(string s, out PixelRect r)
        {
            r = new PixelRect();
            if (string.IsNullOrWhiteSpace(s)) return false;
            var p = s.Split(',');
            if (p.Length != 4) return false;
            if (int.TryParse(p[0], out int x) && int.TryParse(p[1], out int y) && int.TryParse(p[2], out int w) && int.TryParse(p[3], out int h))
            {
                r = new PixelRect(x, y, w, h);
                return true;
            }
            return false;
        }
    }

    public sealed class MonitorInfo
    {
        public int Index { get; set; }               // 1-based, ordered left→right, top→bottom
        public string DeviceName { get; set; }       // e.g. \\.\DISPLAY1
        public PixelRect Bounds { get; set; }        // physical pixels
        public PixelRect WorkArea { get; set; }
        public bool IsPrimary { get; set; }
        public double Scale { get; set; }            // 1.0 = 100 %, 1.5 = 150 %
        public IntPtr Handle { get; set; }

        public int ScalePercent => (int)Math.Round(Scale * 100);
        public string Resolution => $"{Bounds.Width} × {Bounds.Height}";
        public string Description => $"{Resolution} · {ScalePercent}%";
    }

    public static class MonitorService
    {
        public static List<MonitorInfo> GetMonitors()
        {
            var result = new List<MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT rc, IntPtr d) =>
            {
                var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
                if (Native.GetMonitorInfo(h, ref info))
                {
                    double scale = 1.0;
                    try
                    {
                        if (Native.GetDpiForMonitor(h, Native.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
                            scale = dx / 96.0;
                    }
                    catch { /* Windows 7/8: keep 100 % */ }

                    result.Add(new MonitorInfo
                    {
                        Handle = h,
                        DeviceName = info.szDevice,
                        Bounds = PixelRect.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                        WorkArea = PixelRect.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom),
                        IsPrimary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0,
                        Scale = scale
                    });
                }
                return true;
            }, IntPtr.Zero);

            var ordered = result.OrderBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
            for (int i = 0; i < ordered.Count; i++) ordered[i].Index = i + 1;
            return ordered;
        }

        public static PixelRect VirtualScreen(IEnumerable<MonitorInfo> monitors) => PixelRect.Union(monitors.Select(m => m.Bounds));

        public static MonitorInfo MonitorAtCursor(IList<MonitorInfo> monitors)
        {
            Native.GetCursorPos(out var p);
            return monitors.FirstOrDefault(m => p.X >= m.Bounds.X && p.X < m.Bounds.Right && p.Y >= m.Bounds.Y && p.Y < m.Bounds.Bottom)
                   ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        }
    }
}

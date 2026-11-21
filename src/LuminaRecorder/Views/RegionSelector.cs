using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LuminaRecorder.Services;

namespace LuminaRecorder.Views
{
    /// <summary>
    /// Full-screen region picker. One transparent overlay window is placed on every monitor (each at its own DPI),
    /// and all of them share one selection in physical pixels, so a region can be dragged across monitors.
    /// </summary>
    public static class RegionSelector
    {
        public static Task<PixelRect?> SelectAsync(IList<MonitorInfo> monitors, (int w, int h)? fixedSize, PixelRect previous)
        {
            var session = new Session(monitors, fixedSize, previous);
            return session.Run();
        }

        internal sealed class Session
        {
            readonly IList<MonitorInfo> _monitors;
            readonly (int w, int h)? _fixed;
            readonly List<RegionOverlayWindow> _windows = new List<RegionOverlayWindow>();
            readonly TaskCompletionSource<PixelRect?> _tcs = new TaskCompletionSource<PixelRect?>();
            Native.POINT _start;
            public bool Dragging { get; private set; }
            public PixelRect Selection { get; private set; }
            public bool HasPrevious { get; }

            public Session(IList<MonitorInfo> monitors, (int w, int h)? fixedSize, PixelRect previous)
            {
                _monitors = monitors;
                _fixed = fixedSize;
                if (!previous.IsEmpty && monitors.Any(m => m.Bounds.IntersectsWith(previous)) && fixedSize == null)
                {
                    Selection = previous;
                    HasPrevious = true;
                }
            }

            public bool IsFixed => _fixed != null;
            public string FixedLabel => _fixed == null ? "" : $"{_fixed.Value.w} × {_fixed.Value.h}";

            public Task<PixelRect?> Run()
            {
                foreach (var m in _monitors)
                {
                    var w = new RegionOverlayWindow(this, m);
                    _windows.Add(w);
                    w.Show();
                }
                if (_fixed != null) Track();
                _windows.FirstOrDefault(w => w.Monitor == MonitorService.MonitorAtCursor(_monitors))?.Activate();
                Redraw();
                return _tcs.Task;
            }

            static Native.POINT Cursor()
            {
                Native.GetCursorPos(out var p);
                return p;
            }

            public void Down()
            {
                if (_fixed != null) { Finish(Selection); return; }
                _start = Cursor();
                Dragging = true;
                Selection = new PixelRect(_start.X, _start.Y, 0, 0);
                Redraw();
            }

            public void Move()
            {
                if (_fixed != null) { Track(); return; }
                if (!Dragging) { Redraw(); return; }
                var p = Cursor();
                Selection = PixelRect.FromLTRB(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y), Math.Max(_start.X, p.X) + 1, Math.Max(_start.Y, p.Y) + 1);
                Redraw();
            }

            public void Up()
            {
                if (!Dragging) return;
                Dragging = false;
                if (Selection.Width < 8 || Selection.Height < 8)
                {
                    // A click without dragging picks the whole monitor under the cursor.
                    var m = MonitorService.MonitorAtCursor(_monitors);
                    Finish(m?.Bounds);
                    return;
                }
                Finish(Selection);
            }

            /// <summary>Fixed-size mode: the frame follows the cursor and is kept inside the virtual desktop.</summary>
            void Track()
            {
                var p = Cursor();
                var (w, h) = _fixed.Value;
                var mon = MonitorService.MonitorAtCursor(_monitors);
                var area = mon?.Bounds ?? MonitorService.VirtualScreen(_monitors);
                int x = p.X - w / 2, y = p.Y - h / 2;
                // keep it on the monitor under the cursor when it fits, otherwise on the whole desktop
                var clamp = w <= area.Width && h <= area.Height ? area : MonitorService.VirtualScreen(_monitors);
                x = Math.Max(clamp.X, Math.Min(x, clamp.Right - w));
                y = Math.Max(clamp.Y, Math.Min(y, clamp.Bottom - h));
                Selection = new PixelRect(x, y, w, h);
                Redraw();
            }

            public void ConfirmPrevious()
            {
                if (!Selection.IsEmpty) Finish(Selection);
            }

            public void Cancel() => Finish(null);

            void Redraw()
            {
                foreach (var w in _windows) w.Render();
            }

            void Finish(PixelRect? result)
            {
                if (_tcs.Task.IsCompleted) return;
                foreach (var w in _windows) w.Close();
                _windows.Clear();
                _tcs.TrySetResult(result);
            }
        }
    }

    /// <summary>The dimmed overlay that covers one monitor during region selection.</summary>
    internal sealed class RegionOverlayWindow : Window
    {
        readonly RegionSelector.Session _session;
        public MonitorInfo Monitor { get; }
        readonly Path _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0x05, 0x07, 0x0A)) };
        readonly Rectangle _frame = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)), StrokeThickness = 2, IsHitTestVisible = false };
        readonly Border _label = new Border { Background = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1A)), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 4), IsHitTestVisible = false };
        readonly TextBlock _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        readonly Border _hint;
        readonly Canvas _canvas = new Canvas();

        public RegionOverlayWindow(RegionSelector.Session session, MonitorInfo monitor)
        {
            _session = session;
            Monitor = monitor;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // hit-testable everywhere
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Cursor = Cursors.Cross;
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            Left = monitor.Bounds.X / monitor.Scale; Top = monitor.Bounds.Y / monitor.Scale;
            Width = monitor.Bounds.Width / monitor.Scale; Height = monitor.Bounds.Height / monitor.Scale;

            _label.Child = _labelText;
            var hintText = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 15,
                TextAlignment = TextAlignment.Center,
                Text = session.IsFixed
                    ? $"วางกรอบ {session.FixedLabel} แล้วคลิกเพื่อยืนยัน · Esc ยกเลิก"
                    : "ลากเพื่อเลือกพื้นที่ (ลากข้ามจอได้) · คลิกเฉย ๆ = เลือกทั้งจอ" +
                      (session.HasPrevious ? " · Enter ใช้พื้นที่เดิม" : "") + " · Esc ยกเลิก"
            };
            _hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x11, 0x14, 0x1A)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 10, 18, 10),
                Child = hintText,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 40, 0, 0)
            };

            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_label);
            var root = new Grid();
            root.Children.Add(_canvas);
            root.Children.Add(_hint);
            Content = root;

            MouseLeftButtonDown += (s, e) => { CaptureMouse(); _session.Down(); };
            MouseMove += (s, e) => _session.Move();
            MouseLeftButtonUp += (s, e) => { ReleaseMouseCapture(); _session.Up(); };
            MouseRightButtonUp += (s, e) => _session.Cancel();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) _session.Cancel();
                else if (e.Key == Key.Enter) _session.ConfirmPrevious();
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.MakeToolWindow(hwnd);
            // Place by physical pixels so mixed-DPI layouts line up exactly.
            Native.PlaceWindow(hwnd, Monitor.Bounds.X, Monitor.Bounds.Y, Monitor.Bounds.Width, Monitor.Bounds.Height, true);
            // A DPI change during the first move can resize the window; place it again once it has settled.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                Native.PlaceWindow(hwnd, Monitor.Bounds.X, Monitor.Bounds.Y, Monitor.Bounds.Width, Monitor.Bounds.Height, true);
                Render();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        double Dpi => VisualTreeHelper.GetDpi(this).DpiScaleX;

        public void Render()
        {
            double scale = Dpi;
            double w = Monitor.Bounds.Width / scale, h = Monitor.Bounds.Height / scale;
            var full = new RectangleGeometry(new Rect(0, 0, w, h));
            var sel = _session.Selection;
            var local = sel.Intersect(Monitor.Bounds);

            if (local.IsEmpty)
            {
                _dim.Data = full;
                _frame.Visibility = Visibility.Collapsed;
                _label.Visibility = Visibility.Collapsed;
                return;
            }

            // Frame drawn for the full selection (clipped by the window), so edges on other monitors line up.
            var r = new Rect((sel.X - Monitor.Bounds.X) / scale, (sel.Y - Monitor.Bounds.Y) / scale, sel.Width / scale, sel.Height / scale);
            _dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r));
            _frame.Visibility = Visibility.Visible;
            Canvas.SetLeft(_frame, r.X - 1); Canvas.SetTop(_frame, r.Y - 1);
            _frame.Width = r.Width + 2; _frame.Height = r.Height + 2;

            // Show the size label only on the monitor that holds the selection's top-left corner.
            bool owner = sel.X >= Monitor.Bounds.X && sel.X < Monitor.Bounds.Right && sel.Y >= Monitor.Bounds.Y && sel.Y < Monitor.Bounds.Bottom;
            _label.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
            if (owner)
            {
                _labelText.Text = $"{sel.Width & ~1} × {sel.Height & ~1}";
                Canvas.SetLeft(_label, Math.Max(4, r.X));
                Canvas.SetTop(_label, r.Y > 34 ? r.Y - 30 : r.Y + 8);
            }
        }
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using LuminaRecorder.Services;
using ScreenRecorderLib;
using Path = System.Windows.Shapes.Path;

namespace LuminaRecorder.Views
{
    /// <summary>Borderless, topmost helper window that is positioned in physical pixels and hidden from recordings.</summary>
    internal abstract class PixelWindow : Window
    {
        protected IntPtr Hwnd { get; private set; }
        protected PixelRect Placement;
        readonly bool _clickThrough;

        protected PixelWindow(PixelRect placement, MonitorInfo monitor, bool clickThrough)
        {
            Placement = placement;
            _clickThrough = clickThrough;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
            double s = monitor?.Scale ?? 1;
            Left = placement.X / s; Top = placement.Y / s; Width = placement.Width / s; Height = placement.Height / s;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Hwnd = new WindowInteropHelper(this).Handle;
            if (_clickThrough) Native.MakeClickThrough(Hwnd); else Native.MakeToolWindow(Hwnd);
            Native.ExcludeFromCapture(Hwnd);
            Place();
            Dispatcher.BeginInvoke(new Action(Place), DispatcherPriority.Loaded);
        }

        protected void Place() =>
            Native.PlaceWindow(Hwnd, Placement.X, Placement.Y, Placement.Width, Placement.Height, true);
    }

    /// <summary>Thin red outline around the recorded region (one per monitor the region touches).</summary>
    internal sealed class RegionFrameWindow : PixelWindow
    {
        const int Pad = 4;

        public RegionFrameWindow(PixelRect region, MonitorInfo monitor)
            : base(FrameRect(region, monitor), monitor, true)
        {
            var area = FrameRect(region, monitor);
            double t = 2;
            var thickness = new Thickness(
                region.X >= monitor.Bounds.X ? t : 0,
                region.Y >= monitor.Bounds.Y ? t : 0,
                region.Right <= monitor.Bounds.Right ? t : 0,
                region.Bottom <= monitor.Bounds.Bottom ? t : 0);

            // inner offset so the line sits just outside the recorded pixels
            double s = monitor.Scale;
            var margin = new Thickness(
                Math.Max(0, (region.X - Pad - area.X) / s),
                Math.Max(0, (region.Y - Pad - area.Y) / s),
                Math.Max(0, (area.Right - (region.Right + Pad)) / s),
                Math.Max(0, (area.Bottom - (region.Bottom + Pad)) / s));

            Content = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)),
                BorderThickness = thickness,
                Margin = margin,
                IsHitTestVisible = false
            };
        }

        static PixelRect FrameRect(PixelRect region, MonitorInfo m)
        {
            var inflated = PixelRect.FromLTRB(region.X - Pad, region.Y - Pad, region.Right + Pad, region.Bottom + Pad);
            return inflated.Intersect(m.Bounds);
        }
    }

    /// <summary>Big 3‑2‑1 countdown shown before recording starts. Esc or the record hotkey cancels.</summary>
    internal sealed class CountdownWindow : PixelWindow
    {
        readonly int _seconds;
        readonly TextBlock _number;
        readonly TaskCompletionSource<bool> _tcs = new TaskCompletionSource<bool>();

        public CountdownWindow(int seconds, PixelRect target, MonitorInfo monitor)
            : base(CenterRect(target, monitor), monitor, false)
        {
            _seconds = seconds;
            _number = new TextBlock { FontSize = 72, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(_number);
            panel.Children.Add(new TextBlock { Text = "Esc เพื่อยกเลิก", FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xCF, 0xDA)), HorizontalAlignment = HorizontalAlignment.Center });
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x11, 0x14, 0x1A)),
                CornerRadius = new CornerRadius(28),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x38, 0x4B)),
                BorderThickness = new Thickness(2),
                Child = panel
            };
            ShowActivated = true;
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Cancel(); };
        }

        static PixelRect CenterRect(PixelRect target, MonitorInfo m)
        {
            int size = (int)Math.Round(170 * m.Scale);
            var area = target.Intersect(m.Bounds);
            if (area.IsEmpty) area = m.Bounds;
            return new PixelRect(area.X + area.Width / 2 - size / 2, area.Y + area.Height / 2 - size / 2, size, size);
        }

        public async Task<bool> RunAsync()
        {
            Show();
            Activate();
            for (int i = _seconds; i > 0 && !_tcs.Task.IsCompleted; i--)
            {
                _number.Text = i.ToString();
                var done = await Task.WhenAny(Task.Delay(1000), _tcs.Task);
                if (done == _tcs.Task) break;
            }
            Close();
            // let the window disappear from the screen before the first frame is captured
            if (!_tcs.Task.IsCompleted) await Task.Delay(120);
            _tcs.TrySetResult(true);
            return await _tcs.Task;
        }

        public void Cancel() => _tcs.TrySetResult(false);
    }

    /// <summary>Floating control pill shown while recording. It never appears in the video.</summary>
    internal sealed class RecordingBarWindow : PixelWindow
    {
        readonly RecorderService _rec;
        readonly AppSettings _settings;
        readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        readonly TextBlock _time = new TextBlock { FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
        readonly TextBlock _size = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MinWidth = 48 };
        readonly Ellipse _dot = new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)), VerticalAlignment = VerticalAlignment.Center };
        readonly Path _pauseIcon;
        readonly Button _pause, _mic;
        readonly Path _micSlash;
        bool _blink;

        public event Action StopRequested;

        const int W = 430, H = 54;

        public RecordingBarWindow(RecorderService rec, PixelRect target, MonitorInfo monitor, AppSettings settings)
            : base(BarRect(target, monitor), monitor, false)
        {
            _rec = rec;
            _settings = settings;
            Background = Brushes.Transparent;

            var res = Application.Current.Resources;
            _pauseIcon = new Path { Data = (Geometry)res["IconPause"], Fill = Brushes.White, Width = 12, Height = 12, Stretch = Stretch.Uniform };
            _pause = new Button { Style = (Style)res["PillButton"], Content = _pauseIcon, Margin = new Thickness(3, 0, 3, 0), ToolTip = $"พัก / อัดต่อ ({settings.HotkeyPause})" };
            System.Windows.Automation.AutomationProperties.SetName(_pause, "พักการอัด");
            _pause.Click += (s, e) => _rec.TogglePause();

            _micSlash = new Path { Data = Geometry.Parse("M4,4 L20,20"), Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E)), StrokeThickness = 2.2, Width = 16, Height = 16, Stretch = Stretch.Uniform, Visibility = Visibility.Collapsed };
            var micGrid = new Grid();
            micGrid.Children.Add(new Path { Data = (Geometry)res["IconMic"], Stroke = Brushes.White, StrokeThickness = 1.8, Width = 16, Height = 16, Stretch = Stretch.Uniform });
            micGrid.Children.Add(_micSlash);
            _mic = new Button { Style = (Style)res["PillButton"], Content = micGrid, Margin = new Thickness(3, 0, 3, 0), ToolTip = "ปิด/เปิดไมค์", Visibility = rec.HasMicrophone ? Visibility.Visible : Visibility.Collapsed };
            System.Windows.Automation.AutomationProperties.SetName(_mic, "ปิดหรือเปิดไมค์");
            _mic.Click += (s, e) => _micSlash.Visibility = _rec.ToggleMicMute() ? Visibility.Visible : Visibility.Collapsed;

            var stopContent = new StackPanel { Orientation = Orientation.Horizontal };
            stopContent.Children.Add(new Rectangle { Width = 10, Height = 10, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
            stopContent.Children.Add(new TextBlock { Text = $"หยุด {settings.HotkeyRecord}", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var stop = new Button { Style = (Style)res["RecordButton"], Height = 38, Padding = new Thickness(14, 0, 14, 0), Content = stopContent, Margin = new Thickness(4, 0, 0, 0) };
            System.Windows.Automation.AutomationProperties.SetName(stop, "หยุดอัด");
            stop.Click += (s, e) => StopRequested?.Invoke();

            var grip = new Path
            {
                Data = Geometry.Parse("M0,0 L0,0.1 M4,0 L4,0.1 M0,5 L0,5.1 M4,5 L4,5.1 M0,10 L0,10.1 M4,10 L4,10.1"),
                Stroke = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x72)),
                StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "ลากเพื่อย้าย (ย้ายไปจออื่นได้)"
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(grip);
            row.Children.Add(_dot);
            row.Children.Add(_time);
            row.Children.Add(_size);
            row.Children.Add(_pause);
            row.Children.Add(_mic);
            row.Children.Add(stop);

            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1A)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x34, 0x40)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(27),
                Padding = new Thickness(16, 0, 8, 0),
                Child = row,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            Tick();
            Closed += (s, e) => _timer.Stop();
        }

        static PixelRect BarRect(PixelRect target, MonitorInfo m)
        {
            int w = (int)Math.Round(W * m.Scale), h = (int)Math.Round(H * m.Scale);
            var area = target.Intersect(m.Bounds);
            if (area.IsEmpty) area = m.Bounds;
            int x = area.X + area.Width / 2 - w / 2;
            int margin = (int)(14 * m.Scale);
            // Above the region when there is room, otherwise just inside the top edge of the monitor.
            int y = area.Y - h - margin >= m.Bounds.Y ? area.Y - h - margin : m.Bounds.Y + margin;
            x = Math.Max(m.Bounds.X + margin, Math.Min(x, m.Bounds.Right - w - margin));
            return new PixelRect(x, y, w, h);
        }

        void Tick()
        {
            var t = _rec.Elapsed;
            _time.Text = $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            try
            {
                if (_rec.CurrentFile != null && File.Exists(_rec.CurrentFile))
                    _size.Text = MainWindow.FriendlySize(new FileInfo(_rec.CurrentFile).Length);
            }
            catch { }
            if (_rec.Status == RecorderStatus.Paused)
            {
                _blink = !_blink;
                _dot.Opacity = _blink ? 1 : 0.2;
            }
            else _dot.Opacity = 1;
        }

        public void OnStatus(RecorderStatus st)
        {
            var res = Application.Current.Resources;
            _pauseIcon.Data = (Geometry)res[st == RecorderStatus.Paused ? "IconPlay" : "IconPause"];
            System.Windows.Automation.AutomationProperties.SetName(_pause, st == RecorderStatus.Paused ? "อัดต่อ" : "พักการอัด");
            if (st == RecorderStatus.Finishing) _time.Text = "กำลังบันทึก…";
        }
    }
}

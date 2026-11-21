using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LuminaRecorder.Services;

namespace LuminaRecorder.Views
{
    /// <summary>Settings dialog: output folder, hotkeys, countdown and behaviour.</summary>
    internal sealed class SettingsWindow : Window
    {
        readonly AppSettings _s;
        readonly TextBox _folder = new TextBox();
        readonly ComboBox _countdown = new ComboBox();
        readonly CheckBox _minimize, _clipboard, _hardware;
        readonly TextBox _hkRecord, _hkPause, _hkShot;
        readonly TextBlock _error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x85)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

        public SettingsWindow(AppSettings settings)
        {
            _s = settings;
            Title = "ตั้งค่า · Lumina Recorder";
            Width = 520; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = (Brush)FindResource("Bg");
            Foreground = (Brush)FindResource("Text");
            FontFamily = (FontFamily)FindResource("UiFont");
            ShowInTaskbar = false;

            var root = new StackPanel { Margin = new Thickness(24) };

            root.Children.Add(Section("โฟลเดอร์เก็บไฟล์"));
            var folderRow = new Grid();
            folderRow.ColumnDefinitions.Add(new ColumnDefinition());
            folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _folder.Text = settings.EffectiveOutputFolder;
            folderRow.Children.Add(_folder);
            var browse = new Button { Content = "เลือก…", Style = (Style)FindResource("SecondaryButton"), Height = 36, Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (s, e) => Browse();
            Grid.SetColumn(browse, 1);
            folderRow.Children.Add(browse);
            root.Children.Add(folderRow);

            root.Children.Add(Section("ปุ่มลัด (ใช้ได้ทุกที่ แม้โปรแกรมถูกย่อ) · คลิกช่องแล้วกดปุ่มที่ต้องการ"));
            _hkRecord = HotkeyBox(settings.HotkeyRecord);
            _hkPause = HotkeyBox(settings.HotkeyPause);
            _hkShot = HotkeyBox(settings.HotkeyScreenshot);
            root.Children.Add(Row("เริ่ม / หยุดอัด", _hkRecord));
            root.Children.Add(Row("พัก / อัดต่อ", _hkPause));
            root.Children.Add(Row("จับภาพหน้าจอ", _hkShot));

            root.Children.Add(Section("การอัด"));
            foreach (var (v, t) in new[] { (0, "ไม่นับถอยหลัง"), (3, "นับถอยหลัง 3 วินาที"), (5, "นับถอยหลัง 5 วินาที") })
                _countdown.Items.Add(new ComboBoxItem { Content = t, Tag = v });
            _countdown.SelectedItem = _countdown.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == settings.CountdownSeconds) ?? _countdown.Items[1];
            root.Children.Add(Row("ก่อนเริ่มอัด", _countdown));

            _minimize = Switch("ย่อหน้าต่างโปรแกรมระหว่างอัด", settings.MinimizeWhileRecording);
            _clipboard = Switch("คัดลอกภาพหน้าจอไว้ในคลิปบอร์ดด้วย", settings.CopyScreenshotToClipboard);
            _hardware = Switch("ใช้การ์ดจอช่วยเข้ารหัสวิดีโอ (แนะนำ)", settings.HardwareEncoding);
            root.Children.Add(_minimize);
            root.Children.Add(_clipboard);
            root.Children.Add(_hardware);

            root.Children.Add(_error);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
            var cancel = new Button { Content = "ยกเลิก", Style = (Style)FindResource("SecondaryButton"), IsCancel = true, MinWidth = 96 };
            var save = new Button { Content = "บันทึก", Style = (Style)FindResource("SecondaryButton"), IsDefault = true, MinWidth = 96, Margin = new Thickness(10, 0, 0, 0), Background = (Brush)FindResource("Accent"), BorderThickness = new Thickness(0), Foreground = Brushes.White };
            save.Click += (s, e) => Save();
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            root.Children.Add(buttons);

            Content = root;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        }

        TextBlock Section(string text) => new TextBlock { Text = text, Style = (Style)FindResource("SectionLabel"), Margin = new Thickness(2, 18, 0, 8), TextWrapping = TextWrapping.Wrap };

        Grid Row(string label, FrameworkElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        CheckBox Switch(string text, bool value) =>
            new CheckBox { Content = text, IsChecked = value, Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 4, 0, 6) };

        TextBox HotkeyBox(string value)
        {
            var box = new TextBox { Text = value, IsReadOnly = true, Cursor = Cursors.Hand };
            box.PreviewKeyDown += (s, e) =>
            {
                e.Handled = true;
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Tab) { e.Handled = false; return; }
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftShift || key == Key.RightShift ||
                    key == Key.LeftAlt || key == Key.RightAlt || key == Key.LWin || key == Key.RWin) return;
                var mods = Keyboard.Modifiers;
                var text = (mods.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (mods.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") +
                           (mods.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + key;
                box.Text = text;
            };
            return box;
        }

        void Browse()
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = _folder.Text, Description = "เลือกโฟลเดอร์เก็บวิดีโอและภาพหน้าจอ" })
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) _folder.Text = dlg.SelectedPath;
        }

        void Save()
        {
            var keys = new[] { _hkRecord.Text, _hkPause.Text, _hkShot.Text };
            if (keys.Any(k => !HotkeyService.TryParse(k, out _, out _)))
            {
                _error.Text = "ปุ่มลัดบางปุ่มไม่ถูกต้อง";
                return;
            }
            if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Length)
            {
                _error.Text = "ปุ่มลัดต้องไม่ซ้ำกัน";
                return;
            }
            try { System.IO.Directory.CreateDirectory(_folder.Text); }
            catch
            {
                _error.Text = "ไม่สามารถใช้โฟลเดอร์นี้ได้";
                return;
            }

            _s.OutputFolder = _folder.Text;
            _s.HotkeyRecord = _hkRecord.Text;
            _s.HotkeyPause = _hkPause.Text;
            _s.HotkeyScreenshot = _hkShot.Text;
            _s.CountdownSeconds = (int)((ComboBoxItem)_countdown.SelectedItem).Tag;
            _s.MinimizeWhileRecording = _minimize.IsChecked == true;
            _s.CopyScreenshotToClipboard = _clipboard.IsChecked == true;
            _s.HardwareEncoding = _hardware.IsChecked == true;
            DialogResult = true;
        }
    }
}

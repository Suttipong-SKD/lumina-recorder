using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace LuminaRecorder
{
    public partial class App : Application
    {
        static Mutex _single;

        protected override void OnStartup(StartupEventArgs e)
        {
            if (!AcquireSingleInstance())
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, a) => Log(a.ExceptionObject as Exception);
            base.OnStartup(e);
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log(e.Exception);
            e.Handled = true;
            if (IsMissingRuntime(e.Exception))
            {
                var r = MessageBox.Show(
                    "เครื่องนี้ยังไม่มี Microsoft Visual C++ Runtime (x64) ที่โปรแกรมต้องใช้ในการอัดวิดีโอ\n\n" +
                    "กด OK เพื่อเปิดหน้าดาวน์โหลด ติดตั้งแล้วเปิด Lumina Recorder ใหม่อีกครั้ง",
                    "Lumina Recorder", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (r == MessageBoxResult.OK)
                    try { System.Diagnostics.Process.Start("https://aka.ms/vs/17/release/vc_redist.x64.exe"); } catch { }
                Shutdown();
                return;
            }
            MessageBox.Show("เกิดข้อผิดพลาด: " + e.Exception.Message, "Lumina Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
            // If the main window never came up, don't stay alive invisibly in the background.
            if (MainWindow == null || !MainWindow.IsLoaded) Shutdown();
        }

        /// <summary>
        /// One instance at a time. If another copy is already running with a window, bring it to the front.
        /// If the other copy has no window (it failed to start and got stuck), end it and carry on.
        /// </summary>
        static bool AcquireSingleInstance()
        {
            _single = new Mutex(true, "LuminaRecorder.SingleInstance", out bool first);
            if (first) return true;

            var me = Process.GetCurrentProcess();
            var others = Process.GetProcessesByName(me.ProcessName).Where(p => p.Id != me.Id).ToList();
            var withWindow = others.FirstOrDefault(p => { try { p.Refresh(); return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });
            if (withWindow != null)
            {
                Services.Native.ShowWindow(withWindow.MainWindowHandle, Services.Native.SW_RESTORE);
                Services.Native.SetForegroundWindow(withWindow.MainWindowHandle);
                return false;
            }

            foreach (var p in others)
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            try
            {
                if (_single.WaitOne(3000)) return true;
            }
            catch (AbandonedMutexException) { return true; }

            MessageBox.Show("Lumina Recorder เปิดอยู่แล้ว ถ้าไม่เห็นหน้าต่าง ให้ปิด LuminaRecorder ใน Task Manager แล้วเปิดใหม่",
                "Lumina Recorder", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        static bool IsMissingRuntime(Exception ex)
        {
            for (var x = ex; x != null; x = x.InnerException)
                if ((x is FileNotFoundException || x is FileLoadException || x is BadImageFormatException) &&
                    x.Message.IndexOf("ScreenRecorderLib", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        internal static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuminaRecorder");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
            }
            catch { }
        }
    }
}

using System;
using System.IO;
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
            _single = new Mutex(true, "LuminaRecorder.SingleInstance", out bool first);
            if (!first)
            {
                MessageBox.Show("Lumina Recorder เปิดอยู่แล้ว", "Lumina Recorder", MessageBoxButton.OK, MessageBoxImage.Information);
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

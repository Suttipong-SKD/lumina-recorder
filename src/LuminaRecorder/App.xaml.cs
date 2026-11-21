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
            MessageBox.Show("เกิดข้อผิดพลาด: " + e.Exception.Message, "Lumina Recorder", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
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

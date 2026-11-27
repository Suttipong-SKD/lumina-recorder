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
            AppDomain.CurrentDomain.UnhandledException += (s, a) => { Log(a.ExceptionObject as Exception); Services.Boot.Log("UNHANDLED " + a.ExceptionObject); };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, a) => { Log(a.Exception); a.SetObserved(); };

            Services.Boot.Begin(e.Args);          // reads what the last start left behind, starts the watchdog
            ConfigureRendering(e.Args);
            LogSystemInfo();
            try { base.OnStartup(e); }
            catch (Exception ex)
            {
                Log(ex); Services.Boot.Log("OnStartup threw " + ex);
                MessageBox.Show("เปิดโปรแกรมไม่สำเร็จ: " + ex.Message + "\n\nรายละเอียดอยู่ที่ " + DataDir, "Lumina Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(2);
            }
        }

        // ---- graphics-driver safety ------------------------------------------------------------------------------
        // A new GPU driver can crash WPF's hardware rendering at startup, before any managed error can be logged.
        // We drop a marker file while starting; if it is still there next time, the last start crashed hard, so
        // we switch WPF to software rendering (and keep it that way until --hardware-render is passed).

        internal static bool SafeRendering { get; private set; }
        static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuminaRecorder");
        static string StartingFlag => Path.Combine(DataDir, "starting.flag");
        static string SoftwareFlag => Path.Combine(DataDir, "software-render.flag");

        static void ConfigureRendering(string[] args)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                bool forceSoft = args.Any(a => a.Equals("--software-render", StringComparison.OrdinalIgnoreCase));
                bool forceHard = args.Any(a => a.Equals("--hardware-render", StringComparison.OrdinalIgnoreCase));
                if (forceHard) File.Delete(SoftwareFlag);
                if (!forceHard && (forceSoft || Services.Boot.HadCrash)) File.WriteAllText(SoftwareFlag, DateTime.Now.ToString("s"));
                if (File.Exists(SoftwareFlag))
                {
                    System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                    SafeRendering = true;
                }
            }
            catch { }
        }

        /// <summary>Called once the main window has been drawn: the start did not crash.</summary>
        internal static void MarkStartedOk()
        {
            Services.Boot.Finished();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            MarkStartedOk();
            base.OnExit(e);
        }

        /// <summary>Writes OS and graphics-card/driver details to error.log so driver problems can be diagnosed.</summary>
        static void LogSystemInfo()
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"Lumina Recorder {typeof(App).Assembly.GetName().Version} · Windows {Environment.OSVersion.Version} · render={(SafeRendering ? "software" : "hardware")}");
                    using (var q = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion, DriverDate FROM Win32_VideoController"))
                        foreach (System.Management.ManagementObject o in q.Get())
                            sb.AppendLine($"  GPU: {o["Name"]} · driver {o["DriverVersion"]} · {o["DriverDate"]}");
                    File.AppendAllText(Path.Combine(DataDir, "system.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {sb}");
                }
                catch { }
            });
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

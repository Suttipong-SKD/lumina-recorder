using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace LuminaRecorder.Services
{
    /// <summary>
    /// Startup safety net. Every risky step (native/COM/driver calls) is announced in boot.log and in starting.flag
    /// BEFORE it runs. If the app crashes or hangs inside a step, the next start sees which step it was and skips it,
    /// so the window always comes up. A watchdog also restarts the app automatically if the window never appears.
    /// </summary>
    internal static class Boot
    {
        public const string Init = "init";           // window creation (never skipped)
        public const string DarkTitle = "darktitle";
        public const string Hotkeys = "hotkeys";
        public const string Devices = "devices";
        public const string Windows = "windows";
        public const string Meters = "meters";

        static readonly object _lock = new object();
        static readonly HashSet<string> _skip = new HashSet<string>();
        static volatile string _current = Init;
        static volatile bool _rendered;
        static Stopwatch _clock = Stopwatch.StartNew();

        static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuminaRecorder");
        static string FlagPath => Path.Combine(Dir, "starting.flag");
        static string SkipPath => Path.Combine(Dir, "skip-steps.txt");
        static string LogPath => Path.Combine(Dir, "boot.log");

        public static IReadOnlyCollection<string> Skipped { get { lock (_lock) return _skip.ToList(); } }
        public static bool HadCrash { get; private set; }
        public static bool RetriedAutomatically { get; private set; }

        /// <summary>Call first thing at startup. Reads what the previous start left behind.</summary>
        public static void Begin(string[] args)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                RetriedAutomatically = args.Any(a => a.Equals("--retry", StringComparison.OrdinalIgnoreCase));
                if (args.Any(a => a.Equals("--reset-steps", StringComparison.OrdinalIgnoreCase))) { Delete(SkipPath); }

                // load steps that were skipped recently (entries expire after 7 days so a fixed driver gets another chance)
                if (File.Exists(SkipPath))
                    foreach (var line in File.ReadAllLines(SkipPath))
                    {
                        var p = line.Split('|');
                        if (p.Length == 2 && DateTime.TryParse(p[1], out var when) && (DateTime.Now - when).TotalDays < 7)
                            _skip.Add(p[0]);
                    }

                // the previous start never finished: whatever step was written in the flag is the culprit
                if (File.Exists(FlagPath))
                {
                    HadCrash = true;
                    var last = (File.ReadAllText(FlagPath) ?? "").Trim();
                    Log($"previous start did NOT finish; it was in step '{last}'");
                    if (!string.IsNullOrEmpty(last) && last != Init) AddSkip(last);
                }
                Log($"--- start · {Environment.OSVersion.Version} · args: {string.Join(" ", args)} · skipping: [{string.Join(",", _skip)}]");
                Mark(Init);
                StartWatchdog();
            }
            catch { }
        }

        /// <summary>True when this step may run (it did not crash/hang the last time).</summary>
        public static bool Allowed(string step)
        {
            lock (_lock) return !_skip.Contains(step);
        }

        /// <summary>Record that we are about to enter a risky step.</summary>
        public static void Mark(string step)
        {
            _current = step;
            Log("enter " + step);
            try { File.WriteAllText(FlagPath, step); } catch { }
        }

        public static void Ok(string step) => Log("ok    " + step);

        /// <summary>Put a step on the skip list from now on (crash, hang, or exception).</summary>
        public static void Fail(string step, string why)
        {
            Log($"FAIL  {step}: {why}");
            AddSkip(step);
        }

        /// <summary>Run a step guarded by try/catch and the skip list.</summary>
        public static bool Run(string step, Action action)
        {
            if (!Allowed(step)) { Log("skip  " + step); return false; }
            Mark(step);
            try { action(); Ok(step); return true; }
            catch (Exception ex) { Fail(step, ex.GetType().Name + " " + ex.Message); App.Log(ex); return false; }
        }

        /// <summary>
        /// Run a native call on a worker thread, give up after the timeout (and skip that step from now on).
        /// A hang in a graphics-driver or COM call can then never freeze the window.
        /// </summary>
        public static async Task<T> RunAsync<T>(string step, Func<T> work, int timeoutMs = 6000) where T : class
        {
            if (!Allowed(step)) { Log("skip  " + step); return null; }
            Mark(step);
            var task = Task.Run(work);
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
            if (done != task) { Fail(step, "timeout " + timeoutMs + " ms"); return null; }
            if (task.IsFaulted)
            {
                var ex = task.Exception?.GetBaseException();
                Fail(step, ex?.GetType().Name + " " + ex?.Message);
                if (ex != null) App.Log(ex);
                return null;
            }
            Ok(step);
            return task.Result;
        }

        /// <summary>Everything started fine: clear the crash marker.</summary>
        public static void Finished()
        {
            _rendered = true;
            Delete(FlagPath);
            Log($"startup finished in {_clock.ElapsedMilliseconds} ms");
        }

        public static void WindowShown()
        {
            _rendered = true;
            Log($"window rendered after {_clock.ElapsedMilliseconds} ms");
        }

        // ---- watchdog ---------------------------------------------------------------------------------------------

        static void StartWatchdog()
        {
            var t = new Thread(() =>
            {
                // 1) if the window is not drawn after 12 s, the start is stuck
                for (int i = 0; i < 120 && !_rendered; i++) Thread.Sleep(100);
                if (_rendered) return;

                var step = _current;
                Log($"WATCHDOG: window not shown after 12 s, stuck in '{step}'");
                if (step != Init) AddSkip(step);

                if (!RetriedAutomatically)
                {
                    // restart once; the stuck step is now on the skip list so the new copy avoids it
                    try
                    {
                        Delete(FlagPath);
                        var exe = Process.GetCurrentProcess().MainModule.FileName;
                        Process.Start(new ProcessStartInfo(exe, "--retry --software-render") { UseShellExecute = false });
                        Log("WATCHDOG: restarted automatically");
                    }
                    catch (Exception ex) { Log("WATCHDOG: restart failed " + ex.Message); }
                    Environment.Exit(3);
                }
                else
                {
                    Native.MessageBoxW(IntPtr.Zero,
                        "Lumina Recorder เปิดหน้าต่างไม่ขึ้น (ค้างที่ขั้นตอน: " + step + ")\n\n" +
                        "ไฟล์บันทึกอยู่ที่ " + LogPath + "\nส่งไฟล์ boot.log และ error.log ให้ผู้พัฒนาเพื่อแก้ไขต่อได้ครับ",
                        "Lumina Recorder", 0x10 /*MB_ICONERROR*/);
                    Delete(FlagPath);
                    Environment.Exit(4);
                }
            })
            { IsBackground = true, Name = "boot-watchdog" };
            t.Start();
        }

        // ---- helpers ----------------------------------------------------------------------------------------------

        static void AddSkip(string step)
        {
            try
            {
                lock (_lock)
                {
                    if (step == Init || string.IsNullOrEmpty(step)) return;
                    _skip.Add(step);
                    var lines = File.Exists(SkipPath) ? File.ReadAllLines(SkipPath).Where(l => !l.StartsWith(step + "|")).ToList() : new List<string>();
                    lines.Add(step + "|" + DateTime.Now.ToString("s"));
                    File.WriteAllLines(SkipPath, lines);
                }
            }
            catch { }
        }

        static void Delete(string path) { try { File.Delete(path); } catch { } }

        public static void Log(string line)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(Dir);
                    // keep the file small
                    var fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 200_000) File.Delete(LogPath);
                    File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\r\n");
                }
            }
            catch { }
        }
    }
}

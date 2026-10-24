using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using Path = System.Windows.Shapes.Path;

[assembly: System.Reflection.AssemblyTitle("CleanerRAM")]
[assembly: System.Reflection.AssemblyProduct("CleanerRAM")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace CleanerRAM
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public class MEMORYSTATUSEX
        {
            public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            public uint dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct TOKEN_PRIV { public int Count; public long Luid; public int Attr; }

        [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("ntdll.dll")] public static extern int NtSetSystemInformation(int cls, IntPtr info, int len);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetSystemFileCacheSize(IntPtr min, IntPtr max, int flags);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr h, uint acc, out IntPtr tok);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValue(string sys, string name, out long luid);
        [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr tok, bool disable, ref TOKEN_PRIV np, int len, IntPtr prev, IntPtr rl);

        public static bool Enable(string name)
        {
            IntPtr tok;
            if (!OpenProcessToken(GetCurrentProcess(), 0x28, out tok)) return false;
            try
            {
                TOKEN_PRIV tp = new TOKEN_PRIV();
                tp.Count = 1; tp.Attr = 2;
                if (!LookupPrivilegeValue(null, name, out tp.Luid)) return false;
                if (!AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                return Marshal.GetLastWin32Error() == 0;
            }
            finally { CloseHandle(tok); }
        }

        // SystemMemoryListInformation = 80. 3 = flush modified list, 4 = purge standby list
        public static bool MemCommand(int cmd)
        {
            IntPtr p = Marshal.AllocHGlobal(4);
            try { Marshal.WriteInt32(p, cmd); return NtSetSystemInformation(80, p, 4) == 0; }
            finally { Marshal.FreeHGlobal(p); }
        }
    }

    class Settings
    {
        public bool Auto = true;
        public int Interval = 15;
        public int Threshold = 70;
        public bool Working = true, Standby = true, Modified = true, FileCache = false;
        public string Theme = "light";
        public string Accent = "#FFFF6A00";
        public string Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
        public bool Autostart = false, Tray = true, Notify = true;

        static string FilePath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CleanerRAM", "settings.ini"); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                var sb = new System.Text.StringBuilder();
                foreach (var f in typeof(Settings).GetFields())
                    sb.AppendLine(f.Name + "=" + Convert.ToString(f.GetValue(this), CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch { }
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i < 1) continue;
                        var f = typeof(Settings).GetField(line.Substring(0, i));
                        if (f == null) continue;
                        try { f.SetValue(s, Convert.ChangeType(line.Substring(i + 1), f.FieldType, CultureInfo.InvariantCulture)); } catch { }
                    }
            }
            catch { }
            if (s.Interval < 1) s.Interval = 1;
            return s;
        }
    }

    static class Cleaner
    {
        public static bool IsAdmin()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        public static void Snapshot(out ulong total, out ulong avail, out uint load)
        {
            var m = new Native.MEMORYSTATUSEX();
            Native.GlobalMemoryStatusEx(m);
            total = m.ullTotalPhys; avail = m.ullAvailPhys; load = m.dwMemoryLoad;
        }

        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "idle", "system", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon", "services", "lsass",
            "svchost", "dwm", "explorer", "fontdrvhost", "audiodg", "sihost", "taskhostw", "ctfmon", "shellexperiencehost",
            "startmenuexperiencehost", "searchhost", "searchindexer", "textinputhost", "runtimebroker", "applicationframehost",
            "securityhealthservice", "msmpeng", "nvcontainer", "nvdisplay.container", "cleanerram"
        };

        // gentle = автоочистка: не трогаем систему, оболочку и активное окно, режем только крупные фоновые процессы
        public static long Clean(bool working, bool standby, bool modified, bool fileCache, bool gentle)
        {
            var prevPrio = Thread.CurrentThread.Priority;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            try
            {
                ulong t, a1, a2; uint l;
                Snapshot(out t, out a1, out l);
                bool admin = IsAdmin();
                if (admin)
                {
                    Native.Enable("SeProfileSingleProcessPrivilege");
                    Native.Enable("SeIncreaseQuotaPrivilege");
                    Native.Enable("SeDebugPrivilege");
                }
                if (working)
                {
                    uint fg = 0;
                    Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fg);
                    long minWs = gentle ? 150L * 1048576 : 40L * 1048576;
                    foreach (Process p in Process.GetProcesses())
                    {
                        try
                        {
                            if (p.Id <= 4 || (uint)p.Id == fg || Skip.Contains(p.ProcessName)) continue;
                            if (p.WorkingSet64 < minWs) continue;
                            IntPtr h = Native.OpenProcess(0x0400 | 0x0100, false, p.Id);
                            if (h != IntPtr.Zero) { Native.EmptyWorkingSet(h); Native.CloseHandle(h); }
                            Thread.Sleep(gentle ? 40 : 15);
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                // В автоматическом режиме кэш сбрасываем только когда памяти реально мало, иначе диск будет молотить зря
                bool lowMem = t > 0 && (double)a1 / t < 0.12;
                if (admin && fileCache && !gentle) Native.SetSystemFileCacheSize(new IntPtr(-1), new IntPtr(-1), 0);
                if (admin && modified && !gentle) Native.MemCommand(3);
                if (admin && standby && (!gentle || lowMem)) Native.MemCommand(4);
                Thread.Sleep(400);
                Snapshot(out t, out a2, out l);
                return (long)a2 - (long)a1;
            }
            finally { Thread.CurrentThread.Priority = prevPrio; }
        }
    }

    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static void Set(bool on, bool admin)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on && !admin) k.SetValue("CleanerRAM", "\"" + UI.Exe + "\" --tray");
                    else k.DeleteValue("CleanerRAM", false);
                }
            }
            catch { }
            if (!admin) return;
            string exe = UI.Exe.Replace("'", "''");
            string cmd = on
                ? "$a=New-ScheduledTaskAction -Execute '" + exe + "' -Argument '--tray';" +
                  "$t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME;" +
                  "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0;" +
                  "Register-ScheduledTask -TaskName CleanerRAM -Action $a -Trigger $t -Settings $s -RunLevel Highest -Force"
                : "Unregister-ScheduledTask -TaskName CleanerRAM -Confirm:$false -ErrorAction SilentlyContinue";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + cmd + "\"");
                    psi.CreateNoWindow = true; psi.UseShellExecute = false;
                    using (var p = Process.Start(psi)) p.WaitForExit(20000);
                }
                catch { }
            });
        }
    }

    static class Program
    {
        public static void Tr(string m) { if (Environment.GetEnvironmentVariable("CR_TRACE") != null) File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cr_trace.txt"), m + "\r\n"); }

        [STAThread]
        static int Main(string[] args)
        {
            Tr("main");
            int shot = Array.IndexOf(args, "--shot");
            if (shot < 0)
            {
                bool created;
                UI.Mtx = new Mutex(true, "CleanerRAM_SingleInstance", out created);
                if (!created) return 0;
            }
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += (o, e) =>
            {
                try { File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CleanerRAM.log"), DateTime.Now + " " + e.Exception + "\r\n"); } catch { }
                e.Handled = true;
            };
            UI ui;
            try
            {
                if (shot >= 0)
                {
                    ui = new UI(true, false);
                    Program.Tr("ctor ok"); ui.Shots(args[shot + 1]);
                }
                else
                {
                    ui = new UI(Array.IndexOf(args, "--tray") >= 0, true);
                }
            }
            catch (Exception ex)
            {
                Tr(ex.ToString()); MessageBox.Show(ex.ToString(), "CleanerRAM");
                return 1;
            }
            app.Run();
            GC.KeepAlive(UI.Mtx);
            return 0;
        }
    }
}

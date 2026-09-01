using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

namespace VolMixerTray
{
    static class Program
    {
        // Modern DPI Awareness (Per-Monitor V2) with fallback
        [DllImport("user32.dll", SetLastError = true)]
        static extern int SetProcessDpiAwarenessContext(int dpiFlag);
        const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static void EnableDPI()
        {
            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            EnableDPI();

            try
            {
                int timeoutMs = 30000;
                int pollMs = 250;
                int distancePx = 300;
                int pad = 8;
                bool useMouseMonitor = true;

                foreach (string a in args)
                {
                    if (a.StartsWith("--timeoutMs=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(12), out timeoutMs);
                    else if (a.StartsWith("--distancePx=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(13), out distancePx);
                    else if (a.StartsWith("--pad=", StringComparison.OrdinalIgnoreCase)) int.TryParse(a.Substring(6), out pad);
                    else if (a.StartsWith("--monitor=", StringComparison.OrdinalIgnoreCase)) useMouseMonitor = !a.EndsWith("primary", StringComparison.OrdinalIgnoreCase);
                }

                bool first;
                using (var m = new Mutex(true, @"VolMixerTray_Mutex", out first))
                {
                    if (!first) return;

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayContext(timeoutMs, pollMs, distancePx, pad, useMouseMonitor));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("VolMixerTray failed to start:\r\n\r\n" + ex, "VolMixerTray Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    sealed class TrayContext : ApplicationContext
    {
        const string LightIconRes = "VolMixerTray.Icons.Light.ico";
        const string DarkIconRes = "VolMixerTray.Icons.Dark.ico";
        const string AppId = "VolumeMixerTray";

        enum IconThemeMode { Auto = 0, Light = 1, Dark = 2 }
        enum AppLanguage { English = 0, Spanish = 1 }
        enum TrayOpenMode { LegacyMixer = 0, SoundOutput = 1 }

        const string RegKeyPath = @"Software\VolMixerTray";
        const string RegLangValue = "Language";
        const string RegOpenModeValue = "TrayOpenMode";

        readonly NotifyIcon tray;
        readonly ContextMenuStrip menu;
        readonly Timer timer;             // watch distance
        readonly Timer windowFindTimer;   // non-blocking window search
        readonly int totalWatchMs, pollMs, distancePx, pad;
        readonly bool useMouseMonitor;

        Point anchor;
        Point startMouse;
        DateTime startedUtc;
        DateTime graceUntilUtc;
        Rectangle activeBounds;
        Rectangle safeZoneBRQ;

        Process sndVol;
        IntPtr sndVolHandle = IntPtr.Zero;
        int findTicks = 0;

        Icon exeFallbackIcon, lightIcon, darkIcon;
        IconThemeMode iconMode = IconThemeMode.Auto;
        AppLanguage currentLanguage;
        TrayOpenMode trayOpenMode;

        ToolStripMenuItem miOpenModeRoot, miOpenLegacy, miOpenSoundOutput;
        ToolStripMenuItem miThemeRoot, miThemeAuto, miThemeLight, miThemeDark;
        ToolStripMenuItem miLanguageRoot, miLangEn, miLangEs, miStartup, miExit;

        // ===== Win32 helpers =====
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);

        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        static readonly IntPtr HWND_TOP = IntPtr.Zero;
        const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        const uint WM_CLOSE = 0x0010;

        // Keyboard helpers for Win+Ctrl+V (Windows Sound Output flyout)
        [DllImport("user32.dll", SetLastError = true)]
        static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        const uint KEYEVENTF_KEYUP = 0x0002;
        const byte VK_LWIN = 0x5B;
        const byte VK_LCONTROL = 0xA2;
        const byte VK_V = 0x56;

        // Theme Event Watcher
        sealed class SystemEventWatcher : NativeWindow, IDisposable
        {
            private const int WM_SETTINGCHANGE = 0x001A;
            private readonly Action _onThemeChange;
            private readonly Action _onTaskbarCreated;
            private readonly uint _wmTaskbarCreated;

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            private static extern uint RegisterWindowMessage(string lpString);

            public SystemEventWatcher(Action onThemeChange, Action onTaskbarCreated)
            {
                CreateHandle(new CreateParams());
                _onThemeChange = onThemeChange;
                _onTaskbarCreated = onTaskbarCreated;
                _wmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_SETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    string area = Marshal.PtrToStringUni(m.LParam);
                    if (area == "ImmersiveColorSet" && _onThemeChange != null) _onThemeChange();
                }
                else if (m.Msg == _wmTaskbarCreated && _wmTaskbarCreated != 0)
                {
                    if (_onTaskbarCreated != null) _onTaskbarCreated();
                }
                base.WndProc(ref m);
            }

            public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
        }

        SystemEventWatcher themeWatcher;

        static IntPtr FindTopLevelWindowByPid(int pid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                uint procId;
                GetWindowThreadProcessId(h, out procId);
                if (procId == (uint)pid && IsWindowVisible(h))
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static void MoveWindowBottomRight(IntPtr hWnd, Rectangle screenBounds, Point anchorBR)
        {
            RECT r;
            if (!GetWindowRect(hWnd, out r)) return;
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;

            int x = Math.Max(screenBounds.Left, Math.Min(anchorBR.X - w, screenBounds.Right - w));
            int y = Math.Max(screenBounds.Top, Math.Min(anchorBR.Y - h, screenBounds.Bottom - h));

            SetWindowPos(hWnd, HWND_TOP, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }

        static string GetSndVolPath()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            bool wow64 = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess;
            return System.IO.Path.Combine(windir, wow64 ? @"Sysnative\SndVol.exe" : @"System32\SndVol.exe");
        }

        public TrayContext(int totalWatchMs, int pollMs, int distancePx, int pad, bool useMouseMonitor)
        {
            this.totalWatchMs = totalWatchMs;
            this.pollMs = pollMs;
            this.distancePx = distancePx;
            this.pad = pad;
            this.useMouseMonitor = useMouseMonitor;

            try { exeFallbackIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { exeFallbackIcon = SystemIcons.Application; }

            lightIcon = LoadEmbeddedIcon(LightIconRes);
            darkIcon = LoadEmbeddedIcon(DarkIconRes);

            trayOpenMode = DetectInitialOpenMode();

            menu = new ContextMenuStrip();

            // Choose what a left-click on the tray icon opens
            miOpenModeRoot = new ToolStripMenuItem("Open from tray");
            miOpenLegacy = new ToolStripMenuItem("Legacy Volume Mixer", null, delegate { SetTrayOpenMode(TrayOpenMode.LegacyMixer); });
            miOpenSoundOutput = new ToolStripMenuItem("Sound Output", null, delegate { SetTrayOpenMode(TrayOpenMode.SoundOutput); });
            miOpenModeRoot.DropDownItems.AddRange(new[] { miOpenLegacy, miOpenSoundOutput });

            miThemeRoot = new ToolStripMenuItem("Tray Icon Theme");
            miThemeAuto = new ToolStripMenuItem("Follow Windows theme (Auto)", null, delegate { SetIconMode(IconThemeMode.Auto); });
            miThemeLight = new ToolStripMenuItem("Use Dark Icon", null, delegate { SetIconMode(IconThemeMode.Light); });
            miThemeDark = new ToolStripMenuItem("Use Light Icon", null, delegate { SetIconMode(IconThemeMode.Dark); });
            miThemeRoot.DropDownItems.AddRange(new[] { miThemeAuto, miThemeLight, miThemeDark });

            miLanguageRoot = new ToolStripMenuItem("Language");
            miLangEn = new ToolStripMenuItem("English", null, delegate { SetLanguage(AppLanguage.English); });
            miLangEs = new ToolStripMenuItem("Spanish", null, delegate { SetLanguage(AppLanguage.Spanish); });
            miLanguageRoot.DropDownItems.AddRange(new[] { miLangEn, miLangEs });

            miStartup = new ToolStripMenuItem("Run at Startup");
            miStartup.Click += delegate { ToggleRunAtStartup(); };
            menu.Opening += delegate { miStartup.Checked = IsRunAtStartupEnabled(); };

            miExit = new ToolStripMenuItem("Exit", null, ExitClick);

            // Add all items to the menu
            menu.Items.AddRange(new ToolStripItem[] {
                miOpenModeRoot,
                new ToolStripSeparator(),
                miThemeRoot,
                miLanguageRoot,
                new ToolStripSeparator(),
                miStartup,
                new ToolStripSeparator(),
                miExit
            });

            tray = new NotifyIcon { Icon = exeFallbackIcon, Visible = true, ContextMenuStrip = menu };
            tray.MouseClick += TrayClick;

            timer = new Timer { Interval = this.pollMs };
            timer.Tick += TimerTick;

            windowFindTimer = new Timer { Interval = 50 };
            windowFindTimer.Tick += WindowFindTimerTick;

            themeWatcher = new SystemEventWatcher(
                () => { if (iconMode == IconThemeMode.Auto) ApplyTrayIcon(); },
                () => { try { if (tray != null) { tray.Visible = false; tray.Visible = true; ApplyTrayIcon(); } } catch { } }
            );

            SetIconMode(IconThemeMode.Auto);
            currentLanguage = DetectInitialLanguage();
            ApplyLanguage();
            UpdateOpenModeChecks();
        }

        bool IsRunAtStartupEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (k != null) return k.GetValue(AppId) != null;
                }
            }
            catch { }
            return false;
        }

        void ToggleRunAtStartup()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null)
                    {
                        if (IsRunAtStartupEnabled()) k.DeleteValue(AppId, false);
                        else k.SetValue(AppId, Application.ExecutablePath);
                    }
                }
            }
            catch { }
        }

        static AppLanguage DetectInitialLanguage()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKeyPath))
                {
                    if (k != null)
                    {
                        string v = k.GetValue(RegLangValue) as string;
                        if (!string.IsNullOrEmpty(v)) return string.Equals(v, "es", StringComparison.OrdinalIgnoreCase) ? AppLanguage.Spanish : AppLanguage.English;
                    }
                }
            }
            catch { }
            try { if (string.Equals(CultureInfo.InstalledUICulture.TwoLetterISOLanguageName, "es", StringComparison.OrdinalIgnoreCase)) return AppLanguage.Spanish; } catch { }
            return AppLanguage.English;
        }

        static TrayOpenMode DetectInitialOpenMode()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RegKeyPath))
                {
                    if (k != null)
                    {
                        object v = k.GetValue(RegOpenModeValue);
                        if (v is int && (int)v == (int)TrayOpenMode.SoundOutput) return TrayOpenMode.SoundOutput;
                    }
                }
            }
            catch { }
            return TrayOpenMode.LegacyMixer;
        }

        void SetTrayOpenMode(TrayOpenMode mode)
        {
            trayOpenMode = mode;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKeyPath))
                {
                    if (k != null) k.SetValue(RegOpenModeValue, (int)mode, RegistryValueKind.DWord);
                }
            }
            catch { }
            UpdateOpenModeChecks();
        }

        void UpdateOpenModeChecks()
        {
            if (miOpenLegacy != null) miOpenLegacy.Checked = trayOpenMode == TrayOpenMode.LegacyMixer;
            if (miOpenSoundOutput != null) miOpenSoundOutput.Checked = trayOpenMode == TrayOpenMode.SoundOutput;
        }

        void SetLanguage(AppLanguage lang)
        {
            currentLanguage = lang;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RegKeyPath))
                {
                    if (k != null) k.SetValue(RegLangValue, lang == AppLanguage.Spanish ? "es" : "en", RegistryValueKind.String);
                }
            }
            catch { }
            ApplyLanguage();
            UpdateOpenModeChecks();
        }

        void ApplyLanguage()
        {
            bool es = currentLanguage == AppLanguage.Spanish;

            if (miOpenModeRoot != null) miOpenModeRoot.Text = es ? "Abrir desde la bandeja" : "Open from tray";
            if (miOpenLegacy != null) miOpenLegacy.Text = es ? "Mezclador de volumen heredado" : "Legacy Volume Mixer";
            if (miOpenSoundOutput != null) miOpenSoundOutput.Text = es ? "Salida de sonido" : "Sound Output";

            if (miThemeRoot != null) miThemeRoot.Text = es ? "Tema del icono" : "Tray Icon Theme";
            if (miThemeAuto != null) miThemeAuto.Text = es ? "Auto (Seguir sistema)" : "Follow Windows theme (Auto)";
            if (miThemeLight != null) miThemeLight.Text = es ? "Usar icono oscuro" : "Use Dark Icon";
            if (miThemeDark != null) miThemeDark.Text = es ? "Usar icono claro" : "Use Light Icon";
            if (miLanguageRoot != null) miLanguageRoot.Text = es ? "Idioma" : "Language";
            if (miLangEn != null) miLangEn.Text = es ? "Inglés" : "English";
            if (miLangEs != null) miLangEs.Text = es ? "Español" : "Spanish";
            if (miStartup != null) miStartup.Text = es ? "Ejecutar al iniciar Windows" : "Run at Startup";
            if (miExit != null) miExit.Text = es ? "Salir (Cerrar App)" : "Exit (Close App)";

            if (miLangEn != null) miLangEn.Checked = (currentLanguage == AppLanguage.English);
            if (miLangEs != null) miLangEs.Checked = (currentLanguage == AppLanguage.Spanish);

            if (tray != null) tray.Text = es ? "Mezclador de volumen" : "Volume Mixer";
        }

        static Icon LoadEmbeddedIcon(string resourceName)
        {
            try { using (System.IO.Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)) { if (s != null) return new Icon(s); } } catch { }
            return null;
        }

        void SetIconMode(IconThemeMode mode)
        {
            iconMode = mode;
            if (miThemeAuto != null) miThemeAuto.Checked = (mode == IconThemeMode.Auto);
            if (miThemeLight != null) miThemeLight.Checked = (mode == IconThemeMode.Light);
            if (miThemeDark != null) miThemeDark.Checked = (mode == IconThemeMode.Dark);
            ApplyTrayIcon();
        }

        void ApplyTrayIcon()
        {
            Icon target = exeFallbackIcon;
            if (iconMode == IconThemeMode.Light && lightIcon != null) target = lightIcon;
            else if (iconMode == IconThemeMode.Dark && darkIcon != null) target = darkIcon;
            else if (iconMode == IconThemeMode.Auto)
            {
                int theme = ReadAppsUseLightTheme();
                target = theme == 1 ? (lightIcon != null ? lightIcon : target) : (darkIcon != null ? darkIcon : target);
            }
            try { tray.Icon = target; } catch { }
        }

        static int ReadAppsUseLightTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) return (int)v;
                    }
                }
            }
            catch { }
            return 0;
        }

        void TrayClick(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            if (trayOpenMode == TrayOpenMode.SoundOutput)
            {
                OpenSoundOutput();
                return;
            }

            ToggleLegacyMixer();
        }

        void ExitClick(object s, EventArgs e) { try { tray.Visible = false; } catch { } ExitThread(); }

        static void OpenSoundOutput()
        {
            try
            {
                // Simulate Win+Ctrl+V using keybd_event so the same Windows flyout opens.
                keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
                keybd_event(VK_LCONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LCONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }

        void ToggleLegacyMixer()
        {
            try
            {
                CloseSndVolGracefully();

                if (sndVol != null && !sndVol.HasExited)
                {
                    try { sndVol.Kill(); } catch { }
                    sndVol = null;
                    return;
                }

                POINT pnt;
                GetCursorPos(out pnt);
                Point physMouse = new Point(pnt.X, pnt.Y);
                Screen scr = useMouseMonitor ? Screen.FromPoint(physMouse) : Screen.PrimaryScreen;
                activeBounds = scr.Bounds;
                safeZoneBRQ = new Rectangle(activeBounds.Left + activeBounds.Width / 2, activeBounds.Top + activeBounds.Height / 2, activeBounds.Width / 2, activeBounds.Height / 2);

                anchor = ComputeAnchor(scr, pad);
                int packed = ((anchor.Y & 0xFFFF) << 16) | (anchor.X & 0xFFFF);

                if (sndVol != null)
                {
                    try { sndVol.Dispose(); } catch { }
                    sndVol = null;
                }

                sndVol = Process.Start(new ProcessStartInfo
                {
                    FileName = GetSndVolPath(),
                    Arguments = "-t " + packed,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });

                startMouse = physMouse;
                graceUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
                startedUtc = DateTime.UtcNow;

                findTicks = 0;
                windowFindTimer.Start();
                timer.Start();
            }
            catch (Exception ex)
            {
                tray.ShowBalloonTip(2500, currentLanguage == AppLanguage.Spanish ? "Mezclador de volumen" : "Volume Mixer", ex.Message, ToolTipIcon.Error);
            }
        }

        void WindowFindTimerTick(object sender, EventArgs e)
        {
            if (sndVol == null || sndVol.HasExited) { windowFindTimer.Stop(); return; }

            findTicks++;
            IntPtr h = FindTopLevelWindowByPid(sndVol.Id);
            if (h != IntPtr.Zero)
            {
                sndVolHandle = h;
                MoveWindowBottomRight(h, activeBounds, anchor);
                windowFindTimer.Stop();
            }
            else if (findTicks > 60) // Stop after ~3 seconds
            {
                windowFindTimer.Stop();
            }
        }

        static Point ComputeAnchor(Screen scr, int pad)
        {
            Rectangle wa = scr.WorkingArea;
            Rectangle b = scr.Bounds;
            if (wa.Bottom < b.Bottom || wa.Right < b.Right) return new Point(wa.Right - pad, wa.Bottom - pad);
            if (wa.Top > b.Top) return new Point(wa.Right - pad, wa.Top + pad);
            if (wa.Left > b.Left) return new Point(wa.Left + pad, wa.Bottom - pad);
            return new Point(wa.Right - pad, wa.Bottom - pad);
        }

        void TimerTick(object sender, EventArgs e)
        {
            try
            {
                if (sndVol == null || sndVol.HasExited || (DateTime.UtcNow - startedUtc).TotalMilliseconds > totalWatchMs)
                {
                    timer.Stop();
                    return;
                }

                POINT pnt;
                if (!GetCursorPos(out pnt)) return;
                Point pos = new Point(pnt.X, pnt.Y);

                if (safeZoneBRQ.Contains(pos)) return;

                if (DateTime.UtcNow >= graceUntilUtc)
                {
                    if (Math.Abs(pos.X - startMouse.X) > distancePx || Math.Abs(pos.Y - startMouse.Y) > distancePx)
                    {
                        CloseSndVolGracefully();
                        timer.Stop();
                    }
                }
            }
            catch { }
        }

        void CloseSndVolGracefully()
        {
            // Try graceful close via PostMessage first to save user settings
            if (sndVolHandle != IntPtr.Zero)
            {
                PostMessage(sndVolHandle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                sndVolHandle = IntPtr.Zero;
            }

            // Backup killer for any orphaned process
            if (sndVol != null && !sndVol.HasExited)
            {
                try { sndVol.CloseMainWindow(); } catch { }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (themeWatcher != null) themeWatcher.Dispose();
                if (timer != null) timer.Dispose();
                if (windowFindTimer != null) windowFindTimer.Dispose();
                if (menu != null) menu.Dispose();
                if (tray != null) { try { tray.Visible = false; } catch { } tray.Dispose(); }
                if (sndVol != null) sndVol.Dispose();
                if (lightIcon != null) lightIcon.Dispose();
                if (darkIcon != null) darkIcon.Dispose();
                if (exeFallbackIcon != null) exeFallbackIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
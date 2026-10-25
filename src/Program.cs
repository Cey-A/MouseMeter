using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MouseMeter
{
    /// <summary>HKCU Run registration, no admin rights needed.</summary>
    public static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "MouseMeter";

        public static string Command()
        {
            return CommandFor(AppDomain.CurrentDomain.BaseDirectory,
                System.IO.File.Exists(System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), "MouseMeter.exe")));
        }

        /// <summary>
        /// Pure form of <see cref="Command"/> so the decision is testable without touching
        /// the registry or the file system.
        /// </summary>
        public static string CommandFor(string baseDir, bool exePresent)
        {
            string root = (baseDir ?? "").TrimEnd('\\');

            // Built as MouseMeter.exe: point the Run key at ourselves. Going through
            // wscript + .vbs only makes sense for the script-hosted build, and even then
            // it is the slower path (~200 ms of PowerShell startup + an AV-flagged combo).
            //
            // Joined by hand rather than Path.Combine: Combine uses the *host* separator,
            // so a Run-key value built off Windows would come out as "C:\dir/file.exe".
            if (exePresent)
                return "\"" + root + "\\MouseMeter.exe\"";

            return "wscript.exe \"" + root + "\\run-hidden.vbs\"";
        }

        /// <summary>True only when the Run value exists AND still launches this build.</summary>
        public static bool IsEnabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    {
                        if (k == null) return false;
                        string v = k.GetValue(ValueName) as string;
                        if (v == null) return false;
                        // An entry left over from the .vbs build would still "exist" but start
                        // the old script, so treat a stale command as not-enabled and rewrite it.
                        return string.Equals(v, Command(), StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch { return false; }
            }
        }

        public static void Apply(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(ValueName, Command(), RegistryValueKind.String);
                    else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }
    }

    public static class Program
    {
        /// <summary>Single-instance mutex. dev.cmd polls this to know when the old copy is gone.</summary>
        public const string MutexName = "MouseMeter.SingleInstance.v2";

        static Mutex _instance;

        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        /// <summary>
        /// Ask an already-running copy to quit and wait for it to let go of the mutex.
        /// Best effort: if the old build predates the quit listener nothing answers, and
        /// the caller falls back to a hard kill.
        /// </summary>
        /// <summary>
        /// Ask an already-running copy to quit and wait for it to let go of the mutex.
        /// Best effort: if the old build predates the quit listener nothing answers, and
        /// the caller falls back to a hard kill.
        /// </summary>
        static void StopPreviousInstance()
        {
            try
            {
                EventWaitHandle ev;
                if (!EventWaitHandle.TryOpenExisting(TrayContext.QuitEventName, out ev))
                    return;

                using (ev)
                {
                    ev.Set();
                    WaitForMutexRelease(60, 50);
                }
            }
            catch (Exception ex) { Log.Exception("stop previous", ex); }
        }

        /// <summary>
        /// Block until nobody holds <see cref="MutexName"/> (or the budget runs out).
        /// Polling rather than one fixed sleep: a healthy instance exits in ~60 ms, and
        /// sleeping a fixed 1.5 s per rebuild makes a fast edit-compile loop feel sluggish.
        /// </summary>
        public static bool WaitForMutexRelease(int attempts, int sleepMs)
        {
            return WaitForMutexRelease(MutexName, attempts, sleepMs);
        }

        public static bool WaitForMutexRelease(string name, int attempts, int sleepMs)
        {
            for (int i = 0; i < attempts; i++)
            {
                if (IsMutexFree(name)) return true;
                Thread.Sleep(sleepMs);
            }
            return IsMutexFree(name);
        }

        /// <summary>
        /// True when nobody owns the named mutex. On a platform without named kernel
        /// objects (anything but Windows) the mutex can never be held by another process,
        /// so "free" is the honest answer — and the one that lets the dev loop proceed
        /// instead of waiting out its budget on every single restart.
        /// </summary>
        public static bool IsMutexFree(string name)
        {
            try
            {
                using (var probe = new Mutex(false, name))
                {
                    bool got;
                    try { got = probe.WaitOne(0); }
                    catch (AbandonedMutexException) { got = true; }   // owner died: treat as free
                    if (got) probe.ReleaseMutex();
                    return got;
                }
            }
            catch (PlatformNotSupportedException) { return true; }
            catch (NotSupportedException) { return true; }
            catch { return false; }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool restart = args != null && Array.IndexOf(args, "--restart") >= 0;

            // "--restart" is what dev.cmd uses: the previous build is still holding the
            // single-instance mutex, so ask it to quit cleanly (which flushes settings)
            // and wait for it instead of killing the process. A hard kill would lose the
            // widget position and opacity the user just dragged into place.
            if (restart) StopPreviousInstance();

            bool created;
            _instance = new Mutex(true, MutexName, out created);
            if (!created)
            {
                Log.Info("second instance, exiting");
                return;
            }

            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Log.Exception("ui thread", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log.Error("domain: " + e.ExceptionObject);
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool verbose = args != null && Array.IndexOf(args, "--verbose") >= 0;
            Log.Verbose = verbose;
            Log.Info("start v3 args=" + (args == null ? "" : string.Join(" ", args)));

            Settings cfg;
            try { cfg = Settings.Load(); }
            catch { cfg = new Settings(); }

            // IsEnabled() also returns false for an entry pointing at an old build (e.g. the
            // .vbs launcher before this became MouseMeter.exe), so this single call both
            // registers a missing entry and repairs a stale one.
            if (cfg.StartWithWindows && !AutoStart.IsEnabled) AutoStart.Apply(true);

            try
            {
                var ctx = new TrayContext(cfg);

                if (args != null && Array.IndexOf(args, "--settings") >= 0)
                    ctx.OpenSettings();

                Application.Run(ctx);
            }
            catch (Exception ex)
            {
                Log.Exception("fatal", ex);
                throw;
            }

            GC.KeepAlive(_instance);
        }
    }

    /// <summary>
    /// Hidden window used for two things: marshalling work back onto the UI thread, and receiving
    /// WM_DEVICECHANGE so a freshly plugged dongle shows up within ~1 s instead of after a full
    /// poll interval.
    ///
    /// It must be a real Form-derived window: a bare Control does not get WM_DEVICECHANGE
    /// broadcasts reliably (they go to top-level windows).
    /// </summary>
    public class MessageWindow : Form
    {
        const int WM_DEVICECHANGE = 0x0219;
        const int DBT_DEVNODES_CHANGED = 0x0007;
        const int DBT_DEVICEARRIVAL = 0x8000;
        const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

        public Action DeviceChanged;

        public MessageWindow()
        {
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Minimized;
            Opacity = 0;
            Size = new Size(1, 1);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ShowInTaskbar = false;
        }

        protected override void SetVisibleCore(bool value)
        {
            // never actually show the window, but still create the handle
            base.SetVisibleCore(false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE)
            {
                int evt = m.WParam.ToInt32();
                if (evt == DBT_DEVNODES_CHANGED || evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
                {
                    var h = DeviceChanged;
                    if (h != null)
                    {
                        try { h(); } catch { }
                    }
                }
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Owns the tray icon, the widget and the polling loop.</summary>
    public class TrayContext : ApplicationContext
    {
        /// <summary>
        /// Named kernel event used by the dev loop: a freshly built copy signals it, this
        /// instance flushes settings and exits so the new one can take the mutex. A kernel
        /// event rather than a window message because the widget and the settings window are
        /// both optional — there is no HWND that is guaranteed to exist.
        /// </summary>
        public const string QuitEventName = @"Local\MouseMeter.QuitRequest";

        readonly Settings _cfg;
        readonly DeviceManager _dm;
        readonly NotifyIcon _ni;
        readonly MessageWindow _marshal = new MessageWindow();
        System.Threading.Timer _timer;
        HudForm _hud;
        SettingsForm _form;
        Palette _p;
        Icon _icon;
        volatile bool _polling;
        volatile bool _rescanQueued;
        volatile bool _quitting;
        int _intervalMs = 60000;
        readonly HashSet<string> _lowNotified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TrayContext(Settings cfg)
        {
            _cfg = cfg;
            _marshal.DeviceChanged = OnDeviceChange;
            _p = Theme.For(cfg.Theme);
            _dm = new DeviceManager(cfg);

            IntPtr handle = _marshal.Handle;   // forces UI thread affinity for BeginInvoke

            _ni = new NotifyIcon();
            _ni.Visible = true;
            _ni.Text = "MouseMeter";
            _ni.MouseDoubleClick += delegate { ShowSettings(); };
            _ni.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleHud();
            };
            _ni.ContextMenuStrip = BuildMenu();

            // first read synchronously, so the widget never flashes "no device"
            try { _dm.Refresh(); } catch (Exception ex) { Log.Exception("first refresh", ex); }

            UpdateTray(false);
            if (_cfg.HudEnabled) ShowHud();
            StartTimer(_dm.NextIntervalSec(_cfg.IntervalSec, _cfg.AdaptivePolling));
            ListenForQuitRequest();
        }

        EventWaitHandle _quitEvent;

        void ListenForQuitRequest()
        {
            try
            {
                _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
                var t = new Thread(delegate()
                {
                    try
                    {
                        // One request = one exit. Looping would fight a second instance that
                        // creates the same event in the meantime.
                        if (_quitEvent.WaitOne(TimeSpan.FromMinutes(30)))
                            _marshal.BeginInvoke((Action)delegate { Quit(); });
                    }
                    catch { }
                });
                t.IsBackground = true;
                t.Name = "quit-listener";
                t.Start();
            }
            catch (Exception ex) { Log.Exception("quit listener", ex); }
        }

        void Post(Action action)
        {
            try
            {
                if (_marshal.IsHandleCreated) _marshal.BeginInvoke(action);
                else action();
            }
            catch (Exception ex) { Log.Exception("post", ex); }
        }

        // ---------- tray ----------

        ContextMenuStrip BuildMenu()
        {
            var m = MenuBuilder.Create(_p);
            var caption = MenuBuilder.Caption("未检测到设备");
            caption.Name = "mmCaption";

            var hud = MenuBuilder.Item("显示桌面挂件");
            hud.Name = "mmHud";
            hud.Checked = _cfg.HudEnabled;
            hud.Click += delegate
            {
                _cfg.HudEnabled = !_cfg.HudEnabled;
                _cfg.Save();
                if (_cfg.HudEnabled) ShowHud(); else HideHud();
            };

            var refresh = MenuBuilder.Item("立即刷新");
            refresh.Click += delegate { Poll(); };

            var settings = MenuBuilder.Item("设置…");
            settings.Click += delegate { ShowSettings(); };

            var startup = MenuBuilder.Item("开机自动启动");
            startup.Name = "mmStartup";
            startup.Checked = _cfg.StartWithWindows;
            startup.Click += delegate
            {
                _cfg.StartWithWindows = !_cfg.StartWithWindows;
                AutoStart.Apply(_cfg.StartWithWindows);
                _cfg.Save();
                if (_form != null) _form.SyncFromConfig();
            };

            var exit = MenuBuilder.Item("退出 MouseMeter");
            exit.Click += delegate { Quit(); };

            m.Items.Add(caption);
            m.Items.Add(MenuBuilder.Sep());
            m.Items.Add(hud);
            m.Items.Add(refresh);
            m.Items.Add(settings);
            m.Items.Add(MenuBuilder.Sep());
            m.Items.Add(startup);
            m.Items.Add(MenuBuilder.Sep());
            m.Items.Add(exit);

            m.Opening += delegate
            {
                var cap = m.Items["mmCaption"];
                if (cap != null) cap.Text = CaptionText();

                var h = m.Items["mmHud"] as ToolStripMenuItem;
                if (h != null) h.Checked = _cfg.HudEnabled;
                var s = m.Items["mmStartup"] as ToolStripMenuItem;
                if (s != null) s.Checked = _cfg.StartWithWindows;
            };

            return m;
        }

        string CaptionText()
        {
            var list = _dm.Snapshot();
            if (list.Count == 0) return "未检测到设备";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < 3; i++)
            {
                if (i > 0) sb.Append(" · ");
                sb.Append(list[i].DeviceName).Append(' ').Append(list[i].Level).Append('%');
            }
            if (list.Count > 3) sb.Append(" …");
            return sb.ToString();
        }

        void UpdateTray(bool fresh)
        {
            var list = _dm.Snapshot();
            Reading? primary = DeviceManager.Primary(list);

            IconFactory.State state;
            int level;
            string kind;
            string tip;

            if (primary == null)
            {
                state = IconFactory.State.Offline;
                level = 0;
                kind = DeviceKind.Mouse;
                tip = "MouseMeter · " + (_dm.LastError.Length > 0 ? _dm.LastError : "正在查找设备");
            }
            else
            {
                var r = primary.Value;
                level = r.Level;
                kind = r.Kind;
                state = IconFactory.StateOf(r, _cfg.LowThreshold);
                string tipName = DeviceKind.Label(r.Kind) + " " + r.DeviceName;
                tip = tipName + " · " + level + "%" + (r.Charging ? " · 充电中" : "");
                if (list.Count > 1)
                {
                    var extra = new System.Text.StringBuilder();
                    for (int i = 1; i < list.Count && i < 3; i++)
                        extra.Append(" · ").Append(DeviceKind.Label(list[i].Kind)).Append(' ').Append(list[i].Level).Append('%');
                    tip += extra.ToString();
                }
            }

            if (tip.Length > 63) tip = tip.Substring(0, 63);
            try { _ni.Text = tip; } catch { }

            bool darkTaskbar = !Theme.SystemUsesLight();
            int size = SystemInformation.SmallIconSize.Width;
            if (size < 16) size = 16;

            try
            {
                using (var bmp = IconFactory.Render(size, level, state, darkTaskbar, kind))
                {
                    var icon = IconFactory.ToIcon(bmp);
                    var old = _icon;
                    _ni.Icon = icon;
                    _icon = icon;
                    if (old != null) old.Dispose();
                }
            }
            catch (Exception ex) { Log.Exception("tray icon", ex); }
        }

        // ---------- widget ----------

        string ResolveHudTheme()
        {
            string t = _cfg.HudTheme;
            if (t == "system") return Theme.SystemUsesLight() ? "light" : "dark";
            return t == "light" ? "light" : "dark";
        }

        void ShowHud()
        {
            if (_hud == null || _hud.IsDisposed)
            {
                _hud = new HudForm(_cfg);
                _hud.OpenSettingsRequested += delegate { ShowSettings(); };
                _hud.RefreshRequested += delegate { Poll(); };
                _hud.HideRequested += delegate
                {
                    _cfg.HudEnabled = false;
                    _cfg.Save();
                    HideHud();
                };
                _hud.ExitRequested += delegate { Quit(); };
            }
            _hud.ApplyTheme(ResolveHudTheme());
            _hud.ApplyScale();
            _hud.UpdateReadings(HudRows(), _dm.Snapshot().Count > 0, _dm.IsStale, _cfg.LowThreshold);
            _hud.Show();
            _hud.BringToFront();
        }

        /// <summary>Rows shown on the widget: mouse / keyboard / headset first, gadgets after.</summary>
        List<Reading> HudRows()
        {
            var list = _dm.Snapshot();
            if (_cfg.ShowPeripheralBatteries)
            {
                var extra = _dm.SnapshotWithPeripherals(true);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Count; i++) seen.Add(list[i].Id);
                for (int i = 0; i < extra.Count; i++)
                    if (!seen.Contains(extra[i].Id)) list.Add(extra[i]);
            }
            return list;
        }

        void ToggleHud()
        {
            _cfg.HudEnabled = !_cfg.HudEnabled;
            _cfg.Save();
            if (_cfg.HudEnabled) ShowHud(); else HideHud();
            var menu = _ni.ContextMenuStrip;
            if (menu != null)
            {
                var h = menu.Items["mmHud"] as ToolStripMenuItem;
                if (h != null) h.Checked = _cfg.HudEnabled;
            }
        }

        void HideHud()
        {
            if (_hud != null) _hud.Hide();
        }

        // ---------- settings window ----------

        public void OpenSettings()
        {
            ShowSettings();
        }

        void ShowSettings()
        {
            if (_form == null || _form.IsDisposed)
            {
                _form = new SettingsForm(_cfg, _dm);
                _form.Changed += delegate { ApplyConfig(); };
            }
            _form.SyncFromConfig();
            _form.SyncReading();
            _form.Show();
            _form.WindowState = FormWindowState.Normal;
            _form.BringToFront();
            _form.Activate();
        }

        void ApplyConfig()
        {
            _p = Theme.For(_cfg.Theme);

            if (_cfg.HudEnabled)
            {
                if (_hud == null || !_hud.Visible || _hud.IsDisposed) ShowHud();
                _hud.ApplyTheme(ResolveHudTheme());
                _hud.ApplyScale();
                _hud.UpdateReadings(HudRows(), _dm.Snapshot().Count > 0, _dm.IsStale, _cfg.LowThreshold);
            }
            else HideHud();

            StartTimer(_dm.NextIntervalSec(_cfg.IntervalSec, _cfg.AdaptivePolling));
            UpdateTray(false);
        }

        // ---------- polling ----------

        void StartTimer(int seconds)
        {
            int ms = Math.Max(5, seconds) * 1000;
            _intervalMs = ms;
            if (_timer != null) _timer.Dispose();
            _timer = new System.Threading.Timer(delegate { Poll(); }, null, ms, ms);
        }

        /// <summary>Re-arm with a different period without creating a second timer instance.</summary>
        void RescheduleIfNeeded()
        {
            int next = _dm.NextIntervalSec(_cfg.IntervalSec, _cfg.AdaptivePolling);
            if (Math.Abs(next * 1000 - _intervalMs) < 500) return;
            Post(delegate { StartTimer(next); });
        }

        void Poll()
        {
            if (_polling) return;
            _polling = true;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try { _dm.Refresh(); }
                    catch (Exception ex) { Log.Exception("poll", ex); }
                    finally { _polling = false; }
                    Post(OnPolled);
                });
            }
            catch (Exception ex)
            {
                _polling = false;
                Log.Exception("queue poll", ex);
            }
        }

        void OnPolled()
        {
            UpdateTray(true);

            if (_hud != null && _hud.Visible)
                _hud.UpdateReadings(HudRows(), _dm.Snapshot().Count > 0, _dm.IsStale, _cfg.LowThreshold);

            if (_form != null && _form.Visible) _form.SyncReading();

            NotifyLowBatteries();
            RescheduleIfNeeded();
        }

        void NotifyLowBatteries()
        {
            if (!_cfg.NotifyLow) return;
            var list = _dm.Snapshot();
            bool anyAlarm = false;
            for (int i = 0; i < list.Count; i++)
            {
                var r = list[i];
                if (r.Charging || r.Level <= 0 || r.Level > _cfg.LowThreshold)
                {
                    _lowNotified.Remove(r.Id);
                    continue;
                }
                anyAlarm = true;
                if (_lowNotified.Contains(r.Id)) continue;
                _lowNotified.Add(r.Id);
                try
                {
                    _ni.ShowBalloonTip(6000, DeviceKind.Label(r.Kind) + "电量偏低",
                        r.DeviceName + " 剩余 " + r.Level + "%，该充电了", ToolTipIcon.Warning);
                }
                catch { }
            }
            GC.KeepAlive(anyAlarm);
        }

        // ---------- device change ----------

        /// <summary>Called by the hidden marshal control when a USB device is plugged or removed.</summary>
        public void OnDeviceChange()
        {
            if (_rescanQueued) return;
            _rescanQueued = true;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        Thread.Sleep(1200);   // let the driver stack settle
                        _dm.Refresh();
                    }
                    catch (Exception ex) { Log.Exception("devchange refresh", ex); }
                    finally { _rescanQueued = false; }
                    Post(OnPolled);
                });
            }
            catch { _rescanQueued = false; }
        }

        // ---------- lifecycle ----------

        void Quit()
        {
            // Idempotent: the quit-listener thread, the tray menu and Dispose() can all reach
            // here within a few ms of each other during a dev-loop restart. Disposing the
            // NotifyIcon twice throws, and that exception used to escape into the unhandled
            // handler as a bogus "fatal".
            if (_quitting) return;
            _quitting = true;

            try { _cfg.Save(); } catch (Exception ex) { Log.Exception("save on exit", ex); }
            if (_timer != null) { try { _timer.Dispose(); } catch { } _timer = null; }
            if (_hud != null) { try { _hud.Close(); _hud.Dispose(); } catch { } _hud = null; }
            if (_form != null) { try { _form.Dispose(); } catch { } _form = null; }
            try { _ni.Visible = false; _ni.Dispose(); } catch { }
            if (_icon != null) { try { _icon.Dispose(); } catch { } _icon = null; }
            try { if (_marshal.IsHandleCreated) _marshal.Dispose(); } catch { }
            try { if (_quitEvent != null) { _quitEvent.Close(); _quitEvent = null; } } catch { }
            Log.Info("exit");
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Quit();
            base.Dispose(disposing);
        }
    }
}

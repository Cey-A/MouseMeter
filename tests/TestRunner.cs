using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace MouseMeter
{
    /// <summary>
    /// Dependency free test runner (no xUnit: the app compiles with C# 5 on .NET Framework,
    /// and the same source is compiled here as a plain console app).
    ///
    ///   powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
    /// </summary>
    public static class TestRunner
    {
        static int _pass;
        static int _fail;

        public static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            // Helper mode for the cross-process mutex test: hold a named lock until told to
            // release. Must come first, before any test output.
            if (args != null && args.Length >= 3 && args[0] == "--hold-mutex")
                return HoldMutex(args[1], args[2]);

            ProtocolTests.Run();
            HeuristicTests.Run();
            MergeTests.Run();
            MaterialTests.Run();
            LaunchTests.Run();

            Console.WriteLine();
            Console.WriteLine("passed: " + _pass + "   failed: " + _fail);
            return _fail == 0 ? 0 : 1;
        }

        /// <summary>Child-process helper: take a named mutex and hold it until signalled.</summary>
        static int HoldMutex(string mutexName, string releaseEventName)
        {
            try
            {
                var m = new Mutex(true, mutexName);   // acquire on this thread
                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, releaseEventName);
                Console.WriteLine("holding");
                Console.Out.Flush();
                ev.WaitOne(TimeSpan.FromSeconds(30));
                try { m.ReleaseMutex(); } catch { }
                GC.KeepAlive(m);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
        }

        public static void Check(string name, bool condition)
        {
            if (condition) { _pass++; Console.WriteLine("  ok   " + name); }
            else { _fail++; Console.WriteLine("  FAIL " + name); }
        }

        public static void Equal<T>(string name, T expected, T actual)
        {
            bool ok = EqualityComparer<T>.Default.Equals(expected, actual);
            Check(name + " (expected " + expected + ", got " + actual + ")", ok);
        }

        public static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title);
        }
    }

    /// <summary>The reply bytes below are the real capture documented in docs/PROTOCOL.md §2.3.</summary>
    public static class ProtocolTests
    {
        // raw: 12 F9 FE FF 9D B8 FF FF FF FD F6 DB FF ...
        // payload: 01 00 62 47 00 00 00 02 09 24 00  -> type 1, pid 0x4762, transport 09, level 0x24 = 36
        static byte[] RealReply()
        {
            var b = new byte[65];
            for (int i = 0; i < b.Length; i++) b[i] = 0xFF;
            b[0] = 0x11;
            b[1] = 0xF9;
            b[2] = 0xFE;   // type   0x01
            b[3] = 0xFF;   // reserved
            b[4] = 0x9D;   // pid lo 0x62
            b[5] = 0xB8;   // pid hi 0x47
            b[6] = 0xFF;
            b[7] = 0xFF;
            b[8] = 0xFF;
            b[9] = 0xFD;   // 0x02
            b[10] = 0xF6;  // transport 0x09
            b[11] = 0xDB;  // level 0x24 = 36
            b[12] = 0xFF;  // not charging
            return b;
        }

        public static void Run()
        {
            TestRunner.Section("protocol");

            var req = Protocol.BuildRequest(0x06);
            TestRunner.Equal("request length", 65, req.Length);
            TestRunner.Equal("request report id", (byte)0x12, req[0]);
            TestRunner.Equal("request opcode inverted", (byte)0xF9, req[1]);
            TestRunner.Equal("request padding", (byte)0xFF, req[64]);

            Reading r;
            string why;
            bool ok = Protocol.TryParse(RealReply(), out r, out why);
            TestRunner.Check("real capture parses", ok);
            TestRunner.Equal("real capture level", 36, r.Level);
            TestRunner.Equal("real capture charging", false, r.Charging);
            TestRunner.Equal("real capture transport", "2.4G", r.Transport);
            TestRunner.Check("pid echoed", Protocol.PidMatches(RealReply(), 0x4762));
            TestRunner.Equal("response type", 1, Protocol.ResponseType(RealReply()));

            // charging flag
            var chg = RealReply();
            chg[12] = 0xFE;   // 0x01 -> charging
            TestRunner.Check("charging reply parses", Protocol.TryParse(chg, out r, out why));
            TestRunner.Equal("charging flag", true, r.Charging);

            // full battery
            var full = RealReply();
            full[11] = 0x9C;  // 0x63 = 99
            Protocol.TryParse(full, out r, out why);
            TestRunner.Equal("99% decode", 99, r.Level);

            // out of range level must be rejected, not shown
            var bad = RealReply();
            bad[11] = 0x00;   // 0xFF = 255
            TestRunner.Check("level 255 rejected", !Protocol.TryParse(bad, out r, out why));

            // wrong head
            var nohead = RealReply();
            nohead[1] = 0x00;
            TestRunner.Check("wrong head rejected", !Protocol.TryParse(nohead, out r, out why));

            // short buffer
            var shortBuf = new byte[10];
            TestRunner.Check("short buffer rejected", !Protocol.TryParse(shortBuf, out r, out why));

            TestRunner.Check("null rejected", !Protocol.TryParse(null, out r, out why));
            TestRunner.Equal("invalid reading is level -1", -1, Reading.Invalid().Level);

            // model field: ASCII, inverted, terminated by 0x00
            var model = new byte[65];
            for (int i = 0; i < model.Length; i++) model[i] = 0xFF;
            string m = "K87 Pro";
            for (int i = 0; i < m.Length; i++) model[13 + i] = (byte)(m[i] ^ 0xFF);
            model[13 + m.Length] = 0xFF;   // 0x00 terminator
            TestRunner.Equal("model parsed", "K87 Pro", Protocol.ParseModel(model));
            TestRunner.Equal("model empty when garbage", "", Protocol.ParseModel(RealReply()));

            // retry budget must stay well under one poll interval
            TestRunner.Check("worst case retry < 600ms", Retry.WorstCaseMs < 600);
        }
    }

    public static class HeuristicTests
    {
        sealed class Fake : HidCollectionLike
        {
            public int UsagePage { get; set; }
            public int Usage { get; set; }
            public int FeatureLength { get; set; }
            public string ProductName { get; set; }
            public string Manufacturer { get; set; }
        }

        static Fake Col(int page, int usage, int feat, string name)
        {
            var f = new Fake();
            f.UsagePage = page; f.Usage = usage; f.FeatureLength = feat;
            f.ProductName = name ?? ""; f.Manufacturer = "";
            return f;
        }

        public static void Run()
        {
            TestRunner.Section("device heuristics");

            // ---- names ----
            TestRunner.Equal("name: headset", DeviceKind.Headset, DeviceHeuristics.FromName("WH-1000XM5 Headphone"));
            TestRunner.Equal("name: earbud", DeviceKind.Headset, DeviceHeuristics.FromName("EDIFIER Earbuds"));
            TestRunner.Equal("name: chinese headset", DeviceKind.Headset, DeviceHeuristics.FromName("华为 FreeBuds 耳机"));
            TestRunner.Equal("name: keyboard", DeviceKind.Keyboard, DeviceHeuristics.FromName("Keychron K3 Keyboard"));
            TestRunner.Equal("name: chinese keyboard", DeviceKind.Keyboard, DeviceHeuristics.FromName("迈从键盘 K87"));
            TestRunner.Equal("name: mouse", DeviceKind.Mouse, DeviceHeuristics.FromName("A5 ultra mouse"));
            TestRunner.Equal("name: receiver is not a class", null, DeviceHeuristics.FromName("2.4G Receiver"));
            TestRunner.Equal("name: empty", null, DeviceHeuristics.FromName(""));

            // order: "Gaming Headset Keyboard" must be a headset (headset wins)
            TestRunner.Equal("name: headset beats keyboard", DeviceKind.Headset,
                DeviceHeuristics.FromName("Gaming Headset Keyboard"));

            // ---- usages ----
            TestRunner.Equal("usage: mouse", DeviceKind.Mouse, DeviceHeuristics.FromUsage(0x01, 0x02));
            TestRunner.Equal("usage: keyboard", DeviceKind.Keyboard, DeviceHeuristics.FromUsage(0x01, 0x06));
            TestRunner.Equal("usage: keypad", DeviceKind.Keyboard, DeviceHeuristics.FromUsage(0x01, 0x07));
            TestRunner.Equal("usage: joystick ignored", null, DeviceHeuristics.FromUsage(0x01, 0x04));
            TestRunner.Equal("usage: vendor page", null, DeviceHeuristics.FromUsage(0xFF01, 0x01));

            // ---- probe candidates ----
            TestRunner.Check("vendor 65B is probeable", DeviceHeuristics.IsProbeCandidate(Col(0xFF01, 0x01, 65, "")));
            TestRunner.Check("vendor 64B is not", !DeviceHeuristics.IsProbeCandidate(Col(0xFF01, 0x01, 64, "")));
            TestRunner.Check("mouse collection not probed", !DeviceHeuristics.IsProbeCandidate(Col(0x01, 0x02, 65, "")));
            TestRunner.Check("keyboard collection not probed", !DeviceHeuristics.IsProbeCandidate(Col(0x01, 0x06, 65, "")));
            TestRunner.Check("consumer collection not probed", !DeviceHeuristics.IsProbeCandidate(Col(0x0C, 0x01, 65, "")));

            // ---- classify: vendor collection with no usage of its own ----
            var vendor = Col(0xFF01, 0x01, 65, "2.4G Receiver");
            var kbd = Col(0x01, 0x06, 8, "");
            var cons = Col(0x0C, 0x01, 2, "");
            var siblings = new List<HidCollectionLike>();
            siblings.Add(vendor); siblings.Add(kbd); siblings.Add(cons);
            TestRunner.Equal("classify: vendor on keyboard dongle", DeviceKind.Keyboard,
                DeviceHeuristics.Classify(vendor, siblings));

            var mouse = Col(0x01, 0x02, 5, "");
            var sib2 = new List<HidCollectionLike>();
            sib2.Add(vendor); sib2.Add(mouse);
            TestRunner.Equal("classify: vendor on mouse dongle", DeviceKind.Mouse,
                DeviceHeuristics.Classify(vendor, sib2));

            var sib3 = new List<HidCollectionLike>();
            sib3.Add(vendor);
            TestRunner.Equal("classify: unknown dongle", null, DeviceHeuristics.Classify(vendor, sib3));

            // ---- probe target selection prefers the canonical usage ----
            var other = Col(0xFFA0, 0x01, 65, "");
            var list = new List<HidCollectionLike>();
            list.Add(other); list.Add(vendor);
            TestRunner.Check("probe target prefers FF01/0001", ReferenceEquals(vendor, DeviceHeuristics.PickProbeTarget(list)));

            var list2 = new List<HidCollectionLike>();
            list2.Add(mouse); list2.Add(kbd);
            TestRunner.Check("probe target none for plain hid", DeviceHeuristics.PickProbeTarget(list2) == null);

            // ---- hw id parsing ----
            ushort vid, pid;
            TestRunner.Check("hwid parse", DeviceHeuristics.TryParseHwId("0BDA:4762", out vid, out pid));
            TestRunner.Equal("hwid vid", (ushort)0x0BDA, vid);
            TestRunner.Equal("hwid pid", (ushort)0x4762, pid);
            TestRunner.Check("hwid rejects garbage", !DeviceHeuristics.TryParseHwId("nope", out vid, out pid));

            // ---- audio endpoint extraction (real format from HKCU MMDevices) ----
            string p = @"\\?\SWD#MMDEVICE#{0.0.0.00000000}.{6b2c8f6e-2d7a-4c1f-9c0e-0a3b5d8e9f11}|"
                     + @"{2f0a9b1c-3d4e-5f60-7182-93a4b5c6d7e8}\6b2c8f6e-2d7a-4c1f-9c0e-0a3b5d8e9f11."
                     + @"{a1b2c3d4-e5f6-4718-9a2b-3c4d5e6f7081}";
            string ep = DeviceHeuristics.AudioEndpointFromPath(p);
            TestRunner.Equal("audio endpoint guid", "{a1b2c3d4-e5f6-4718-9a2b-3c4d5e6f7081}", ep != null ? "{" + ep + "}" : null);
            TestRunner.Check("audio endpoint none for hid path",
                DeviceHeuristics.AudioEndpointFromPath(@"\\?\hid#vid_0bda&pid_4762&mi_02&col01#7&1a2b#0000") == null);

            // ---- transport label ----
            TestRunner.Equal("transport ble", "BLE", DeviceHeuristics.TransportOf(@"BLE#0_0001"));
            TestRunner.Equal("transport 2.4G", "2.4G", DeviceHeuristics.TransportOf(@"HID\{7e2f3b1a-11c2-4e9a-a2b3-3c4d5e6f7081}"));
        }
    }

    /// <summary>
    /// Launch-path rules for the packaged build. These are the ones that silently break
    /// auto-start: a Run key that still points at the old .vbs launcher looks "enabled"
    /// but starts the previous generation of the app.
    /// </summary>
    public static class LaunchTests
    {
        public static void Run()
        {
            TestRunner.Section("launch / packaging");

            // exe present -> registry must point at the exe, quoted, no wscript
            string exeCmd = AutoStart.CommandFor(@"C:\Tools\MouseMeter\", true);
            TestRunner.Equal("autostart uses exe", "\"C:\\Tools\\MouseMeter\\MouseMeter.exe\"", exeCmd);
            TestRunner.Check("autostart exe cmd has no wscript", exeCmd.IndexOf("wscript", StringComparison.OrdinalIgnoreCase) < 0);

            // exe absent -> fall back to the script host
            string vbsCmd = AutoStart.CommandFor(@"C:\Tools\MouseMeter", false);
            TestRunner.Equal("autostart falls back to vbs", "wscript.exe \"C:\\Tools\\MouseMeter\\run-hidden.vbs\"", vbsCmd);

            // trailing separator must not produce a doubled backslash
            TestRunner.Check("no double backslash", exeCmd.IndexOf("\\\\") < 0);

            // empty/null base dir must not throw (StartupObject runs before env is ready)
            bool threw = false;
            try { AutoStart.CommandFor(null, false); }
            catch { threw = true; }
            TestRunner.Check("autostart tolerates null base dir", !threw);

            RestartTests();
        }

        /// <summary>
        /// The dev loop depends on one property: after the old copy exits, the mutex is
        /// free. If IsMutexFree() lied, dev.cmd would either restart too early (two
        /// instances fighting) or always wait the full 3 s.
        ///
        /// Only the cross-process paths are asserted: a *named* mutex taken inside this
        /// process is not visible to a second handle on Linux (Mono/.NET name support is
        /// shimmed), while on Windows it is. Asserting the in-process case would therefore
        /// pass on Windows and fail here, which is worse than not asserting it.
        /// </summary>
        static void RestartTests()
        {
            string name = "MouseMeter.Test.Mutex." + Guid.NewGuid().ToString("N");

            TestRunner.Check("fresh mutex name reads as free", Program.IsMutexFree(name));

            // waiting with nothing to wait for must return at once, not sleep the budget
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool freed = Program.WaitForMutexRelease(name, 2, 10);
            sw.Stop();
            TestRunner.Check("wait returns true when already free", freed);
            TestRunner.Check("free wait does not sleep the full budget", sw.ElapsedMilliseconds < 500);

            // attempts=0 must still report the current state instead of returning false blindly
            TestRunner.Check("zero attempts still reports state", Program.WaitForMutexRelease(name, 0, 10));

            // a mutex abandoned by a dead thread must not block a restart forever
            var t = new Thread(delegate()
            {
                var m = new Mutex(true, name);
                GC.KeepAlive(m);   // never released, thread exits holding it
            });
            t.Start();
            t.Join();

            TestRunner.Check("abandoned mutex treated as free", Program.IsMutexFree(name));

            CrossProcessMutexTest(name);
        }

        /// <summary>
        /// The one case that actually matters: another *process* holds the lock. That is why
        /// a named mutex is used at all, and it is the only shape a real restart can take.
        /// </summary>
        static void CrossProcessMutexTest(string baseName)
        {
            // Named kernel objects are a Windows feature. On Linux/.NET Core the runtime
            // throws PlatformNotSupportedException, which is exactly why this test can only
            // give a real answer on the target OS.
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Console.WriteLine("  skip cross-process mutex (named primitives are Windows-only)");
                return;
            }

            string name = baseName + ".xproc";
            string holder = name + ".hold";

            // The child is this same executable re-entered with a special argv. It has to be
            // a long-lived process, so it blocks on a second event we can signal at the end.
            // Launch the child through the dotnet host: GetEntryAssembly().Location is the
            // .dll, not an executable, so Process.Start on it fails on every OS.
            string dll = System.Reflection.Assembly.GetEntryAssembly().Location;
            var psi = new System.Diagnostics.ProcessStartInfo();
            if (System.IO.Path.GetExtension(dll).ToLowerInvariant() == ".exe" && System.IO.File.Exists(dll))
            {
                psi.FileName = dll;                        // self-contained apphost
                psi.Arguments = "--hold-mutex \"" + name + "\" \"" + holder + "\"";
            }
            else
            {
                psi.FileName = "dotnet";
                psi.Arguments = "\"" + dll + "\" --hold-mutex \"" + name + "\" \"" + holder + "\"";
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;

            System.Diagnostics.Process child = null;
            try
            {
                child = System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                TestRunner.Check("cross-process: child launched (" + ex.GetType().Name + ")", false);
                return;
            }

            try
            {
                // Handshake instead of a fixed sleep: the child prints "holding" only after
                // the mutex is actually held, so the assertions below are not racy.
                string line = child.StandardOutput.ReadLine();
                TestRunner.Check("cross-process: child acquired the lock",
                    line != null && line.Trim() == "holding");

                TestRunner.Check("cross-process mutex reads as taken", !Program.IsMutexFree(name));

                // And the wait must give up rather than hang forever.
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                bool released = Program.WaitForMutexRelease(name, 3, 20);
                sw2.Stop();
                TestRunner.Check("cross-process wait times out", !released);
                TestRunner.Check("cross-process wait is bounded", sw2.ElapsedMilliseconds < 1500);

                if (Program.IsMutexFree(name))
                {
                    // Nothing to clean up via the event; the child never started holding.
                    TestRunner.Check("cross-process: child did hold the lock", false);
                }
            }
            finally
            {
                try
                {
                    EventWaitHandle ev;
                    if (EventWaitHandle.TryOpenExisting(holder, out ev))
                    {
                        using (ev) ev.Set();
                    }
                }
                catch { }

                if (child != null)
                {
                    if (!child.WaitForExit(2000))
                    {
                        try { child.Kill(); } catch { }
                    }
                    child.Dispose();
                }

                // mutex must come back
                TestRunner.Check("cross-process mutex freed after child exit",
                    Program.WaitForMutexRelease(name, 40, 25));
            }
        }
    }

    public static class MergeTests
    {
        static Reading Rd(string id, string kind, int level, bool charging, string name)
        {
            var r = new Reading();
            r.Id = id; r.Kind = kind; r.DeviceName = name; r.Level = level;
            r.Charging = charging; r.Transport = "2.4G"; r.HardwareId = "0BDA:4762"; r.At = DateTime.Now;
            return r;
        }

        public static void Run()
        {
            TestRunner.Section("merge + ordering");

            // ordering is internal; verify the public contract instead: primary prefers the mouse
            var list = new List<Reading>();
            list.Add(Rd("b", DeviceKind.Headset, 80, false, "耳机"));
            list.Add(Rd("c", DeviceKind.Keyboard, 55, false, "键盘"));
            list.Add(Rd("a", DeviceKind.Mouse, 29, false, "A5 ultra"));
            var primary = DeviceManager.Primary(list);
            TestRunner.Equal("primary is the mouse", "A5 ultra", primary.HasValue ? primary.Value.DeviceName : null);

            var noMouse = new List<Reading>();
            noMouse.Add(Rd("b", DeviceKind.Headset, 80, false, "耳机"));
            noMouse.Add(Rd("c", DeviceKind.Keyboard, 55, false, "键盘"));
            var p2 = DeviceManager.Primary(noMouse);
            TestRunner.Equal("primary falls back to first", "耳机", p2.HasValue ? p2.Value.DeviceName : null);
            TestRunner.Check("primary null on empty", !DeviceManager.Primary(new List<Reading>()).HasValue);

            TestRunner.Equal("label keyboard", "键盘", DeviceKind.Label(DeviceKind.Keyboard));
            TestRunner.Equal("label headset", "耳机", DeviceKind.Label(DeviceKind.Headset));
            TestRunner.Equal("label mouse", "鼠标", DeviceKind.Label(DeviceKind.Mouse));

            TestRunner.Check("fresh reading not stale", !Freshness.IsStale(Rd("x", DeviceKind.Mouse, 50, false, "m")));
            var old = Rd("x", DeviceKind.Mouse, 50, false, "m");
            old.At = DateTime.Now.AddMinutes(-5);
            TestRunner.Check("5 min old is stale", Freshness.IsStale(old));
        }
    }

    /// <summary>
    /// Material (Acrylic) helpers. Pure functions over Material so this runs in a console process
    /// without initialising WinForms — the visual result still needs a screenshot, but the
    /// fallback chain and the readability of the tint would otherwise regress silently.
    /// </summary>
    public static class MaterialTests
    {
        public static void Run()
        {
            TestRunner.Section("material selection");

            TestRunner.Equal("100% means solid card", Material.Solid, Material.Pick(100, true, true, true));
            TestRunner.Equal("win11 22H2+ -> backdrop", Material.Backdrop, Material.Pick(96, true, true, true));
            TestRunner.Equal("win10 1803+ -> accent policy", Material.Accent, Material.Pick(96, true, true, false));
            TestRunner.Equal("transparency off -> solid", Material.Solid, Material.Pick(96, false, true, true));
            TestRunner.Equal("win10 1709 or older -> solid", Material.Solid, Material.Pick(96, true, false, false));
            TestRunner.Equal("55% still blurs", Material.Backdrop, Material.Pick(55, true, true, true));

            TestRunner.Section("tint alpha mapping");
            int lo = Material.TintAlpha(55, false);
            int mid = Material.TintAlpha(77, false);
            int hi = Material.TintAlpha(99, false);
            TestRunner.Check("alpha rises with the slider (" + lo + " < " + mid + " < " + hi + ")", lo < mid && mid < hi);
            TestRunner.Check("softest tint still hides the wallpaper edge (" + lo + " >= 120)", lo >= 120);
            TestRunner.Check("strongest tint is not opaque (" + hi + " < 255)", hi < 255);
            TestRunner.Equal("dark card gets extra body", 22, Material.TintAlpha(77, true) - mid);
            TestRunner.Equal("clamped below 55", lo, Material.TintAlpha(0, false));
            TestRunner.Equal("clamped above 99", hi, Material.TintAlpha(100, false));

            TestRunner.Section("tint packing");
            // 0xAABBGGRR. Getting R and B the wrong way round is invisible in code review but
            // turns a neutral grey card blue on screen.
            int word = Material.TintWord(200, 32, 32, 34);
            TestRunner.Equal("alpha in the top byte", 200, (word >> 24) & 0xFF);
            TestRunner.Equal("red below alpha", 32, (word >> 16) & 0xFF);
            TestRunner.Equal("green in the middle", 32, (word >> 8) & 0xFF);
            TestRunner.Equal("blue in the low byte", 34, word & 0xFF);
            // Asymmetric channel values so a swap cannot hide behind equal bytes.
            int w2 = Material.TintWord(180, 10, 20, 30);
            TestRunner.Check("asymmetric channels keep their order",
                ((w2 >> 16) & 0xFF) == 10 && ((w2 >> 8) & 0xFF) == 20 && (w2 & 0xFF) == 30);
        }
    }
}

using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AsioRateMonitor
{
    internal static class SelfTests
    {
        private static int passed;
        private static void Check(bool value, string name)
        {
            if (!value) throw new Exception("FAIL: " + name);
            passed++;
            Console.WriteLine("PASS: " + name);
        }
        private sealed class FakeProvider : ISampleRateProvider, IChangeNotifications
        {
            public string Name { get { return "Fake"; } }
            public string DriverPath { get { return ""; } }
            public int Reads, Releases;
            public bool Disposed;
            public bool NotificationsEnabled;
            public volatile bool Supported = true;
            public RateReading Next = RateReading.Success(44100);
            public readonly AutoResetEvent Change = new AutoResetEvent(false);
            public WaitHandle[] ChangeHandles { get { return new WaitHandle[] { Change }; } }
            public bool CanWaitForChanges { get { return NotificationsEnabled && Supported; } }
            public string NotificationDetail { get { return "fake notifications"; } }
            public void EnableNotifications(bool enabled) { NotificationsEnabled = enabled; }
            public void ConsumeChange(int index) { }
            public RateReading Read()
            {
                Interlocked.Increment(ref Reads);
                return Next;
            }
            public void Release() { Interlocked.Increment(ref Releases); }
            public void Dispose() { Disposed = true; Change.Dispose(); }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1)
                {
                    var release = System.Reflection.Assembly.ReflectionOnlyLoadFrom(Path.GetFullPath(args[0]));
                    Check(release.GetType("AsioRateMonitor.SelfTests", false) == null &&
                        release.GetType("AsioRateMonitor.Diagnostics", false) == null,
                        "release executable excludes tests and diagnostics");
                }
                Check(RateReading.Success(44100).Label == "44.1 kHz", "44.1 kHz preserves decimal");
                Check(RateReading.Success(48000).Label == "48 kHz", "48 kHz formatting");
                CultureInfo original = Thread.CurrentThread.CurrentCulture;
                try {
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                    Check(RateReading.Success(44100).Kilohertz == "44.1", "culture-independent icon");
                } finally { Thread.CurrentThread.CurrentCulture = original; }
                Check(RateReading.Success(0).State == ReadingState.Error, "zero rate is unknown");
                var unavailable = new RateReading(ReadingState.NoDevice, 48000, "disconnected");
                Check(unavailable.Hertz == 0 && unavailable.Label == "-- kHz", "disconnect cannot display old rate");
                Check(MonitorPolicy.Delay(unavailable, 1000) == 5000, "failure retries back off");
                Check(MonitorPolicy.Delay(RateReading.Success(48000), 1000) == 1000, "normal polling interval");
                Check(MonitorPolicy.WaitMilliseconds(MonitorMode.Events, true, RateReading.Success(48000), 1000) == Timeout.Infinite, "subscribed device has no periodic rate query");
                Check(MonitorPolicy.WaitMilliseconds(MonitorMode.Events, true, unavailable, 1000) == Timeout.Infinite, "PnP notification waits for reconnect");
                Check(MonitorPolicy.WaitMilliseconds(MonitorMode.Events, false, RateReading.Success(48000), 1000) == 1000, "unsupported notifications fall back to polling");
                Check(MonitorPolicy.WaitMilliseconds(MonitorMode.Polling, true, RateReading.Success(48000), 2000) == 2000, "manual polling overrides notification support");
                Rectangle bar = new Rectangle(0, 1392, 2560, 48);
                Rectangle weather = new Rectangle(1982, 1392, 152, 48);
                Rectangle tray = new Rectangle(2146, 1392, 414, 48);
                Rectangle slot = TaskbarGeometry.Beside(bar, weather, new Rectangle[] { weather, tray }, 112, 8);
                Check(slot == new Rectangle(1862, 1392, 112, 48), "weather text goes left when right side occupied");
                Check(TaskbarGeometry.Beside(bar, weather, new Rectangle[] { weather }, 112, 8) == slot,
                    "temporarily missing tray buttons do not move text to the right");
                Check(TaskbarGeometry.Beside(bar, weather, new Rectangle[] { slot }, 112, 8).IsEmpty,
                    "occupied left slot falls back instead of swapping sides");
                Check(TaskbarGeometry.Beside(bar, weather, new Rectangle[] { bar }, 112, 8).IsEmpty, "full taskbar never covers existing buttons");
                string settingsPath = Path.Combine(Path.GetTempPath(), "AsioRateMonitor-test-" + Guid.NewGuid().ToString("N") + ".ini");
                try
                {
                    Check(Preferences.Load(settingsPath).TaskbarText, "taskbar text enabled by default");
                    Preferences preferences = new Preferences { TaskbarText = false, Mode = MonitorMode.Polling, Interval = 2000 };
                    preferences.Save(settingsPath);
                    Preferences restored = Preferences.Load(settingsPath);
                    Check(!restored.TaskbarText && restored.Mode == MonitorMode.Polling && restored.Interval == 2000,
                        "text disabled preference survives restart without changing monitor settings");
                    restored.TaskbarText = true; restored.Save(settingsPath);
                    Check(Preferences.Load(settingsPath).TaskbarText, "text enabled preference survives restart");
                }
                finally { if (File.Exists(settingsPath)) File.Delete(settingsPath); }
                using (var menu = new System.Windows.Forms.ContextMenuStrip())
                using (var readout = new TaskbarReadout(menu))
                {
                    using (Bitmap bitmap = LayeredReadoutRenderer.Draw(112, 48, "44.1 kHz", readout.Font, Color.White))
                    {
                        bool wholeAreaClickable = true, hasText = false;
                        for (int y = 0; y < bitmap.Height; y++)
                            for (int x = 0; x < bitmap.Width; x++)
                            {
                                byte alpha = bitmap.GetPixel(x, y).A;
                                wholeAreaClickable &= alpha > 0;
                                hasText |= alpha == 255;
                            }
                        Check(bitmap.GetPixel(0, 0).A == 1 && wholeAreaClickable && hasText,
                            "transparent background retains mouse hit area and opaque text");
                        LayeredReadoutRenderer.Present(readout.Handle, bitmap);
                        Check((TaskbarNative.GetWindowLongPtr(readout.Handle, -20).ToInt64() & 0x80000) != 0,
                            "per-pixel composited surface submitted successfully");
                    }
                }
                using (Icon icon = RateIcon.Create(RateReading.Success(44100), 32))
                    Check(icon.Width == 32 && icon.Height == 32, "tray icon creation");
                FakeProvider fake = new FakeProvider();
                using (AutoResetEvent update = new AutoResetEvent(false))
                {
                    RateReading observed = null;
                    RateMonitor monitor = new RateMonitor(fake);
                    monitor.Updated += delegate(RateReading reading) { observed = reading; update.Set(); };
                    monitor.Start();
                    Check(update.WaitOne(3000) && observed.Hertz == 44100, "worker publishes live rate");
                    Check(!update.WaitOne(1250) && fake.Reads == 1, "idle event mode does not poll");
                    fake.Next = RateReading.Success(48000);
                    fake.Change.Set();
                    Check(update.WaitOne(3000) && observed.Hertz == 48000 && monitor.EventCount == 1, "notification triggers rate read");
                    monitor.Mode = MonitorMode.Polling;
                    Check(update.WaitOne(3000) && !fake.NotificationsEnabled, "switch to polling unsubscribes sample-rate events");
                    Check(update.WaitOne(2500), "polling continues without events");
                    monitor.Mode = MonitorMode.Events;
                    Check(update.WaitOne(3000) && fake.NotificationsEnabled, "switch back subscribes events");
                    Check(!update.WaitOne(1250), "switch back stops periodic queries");
                    fake.Supported = false;
                    monitor.Refresh();
                    Check(update.WaitOne(3000) && monitor.ModeText.Contains("轮询保底"), "unavailable subscription reports fallback");
                    Check(update.WaitOne(2500), "automatic fallback actually polls");
                    fake.Supported = true;
                    fake.Next = unavailable;
                    monitor.Refresh();
                    Check(update.WaitOne(3000) && observed.State == ReadingState.NoDevice && observed.Hertz == 0, "worker publishes disconnect without stale value");
                    monitor.Suspend(true);
                    Check(update.WaitOne(3000) && observed.State == ReadingState.Suspended && fake.Releases > 0, "suspend releases control handle");
                    monitor.Suspend(false);
                    Check(update.WaitOne(3000) && observed.State == ReadingState.NoDevice, "resume queries again");
                    monitor.Dispose();
                    Check(fake.Disposed, "exit disposes provider");
                }
                Console.WriteLine("All " + passed + " tests passed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
    }
}

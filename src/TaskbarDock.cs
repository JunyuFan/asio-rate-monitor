using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AsioRateMonitor
{
    internal static class TaskbarNative
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint command);
        internal delegate void WinEventProc(IntPtr hook, uint kind, IntPtr window, int objectId, int childId, uint thread, uint time);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
        [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint first, uint last, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] public static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref Point p);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("kernel32.dll")] public static extern void SetLastError(uint error);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);
        public static void MatchTaskbarDpi()
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero) SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(taskbar));
        }
        public static Rectangle Bounds(IntPtr h)
        {
            Rect r;
            return GetWindowRect(h, out r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
        }
    }

    internal sealed class TaskbarPlacement
    {
        public IntPtr Taskbar;
        public Rectangle Bounds;
        public uint Dpi = 96;
        public string Detail;
        public bool Valid { get { return Taskbar != IntPtr.Zero && Bounds.Width > 0; } }
    }

    internal static class TaskbarGeometry
    {
        public static Rectangle Beside(Rectangle bar, Rectangle anchor, IList<Rectangle> occupied, int width, int gap)
        {
            // Keep a fixed side, even when shell menus temporarily hide buttons.
            // If the left slot is occupied, use the tray fallback instead of jumping.
            Rectangle candidate = new Rectangle(anchor.Left - gap - width, bar.Top, width, bar.Height);
            if (!bar.Contains(candidate)) return Rectangle.Empty;
            foreach (Rectangle button in occupied)
                if (candidate.IntersectsWith(button)) return Rectangle.Empty;
            return candidate;
        }
    }

    // Accessibility is queried only initially or after taskbar layout events.
    // This observer never reads the audio driver and has no periodic timer.
    internal sealed class TaskbarLayoutObserver : IDisposable
    {
        private readonly AutoResetEvent requested = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly Action<TaskbarPlacement> updated;
        private readonly int ownProcess = Process.GetCurrentProcess().Id;
        private volatile bool stopping;
        private volatile bool enabled = true;
        private AutomationElement subscribedRoot;
        private IntPtr subscribedWindow;
        private Rectangle lastWeather, lastBar;
        private readonly AutomationPropertyChangedEventHandler propertyHandler;
        private readonly StructureChangedEventHandler structureHandler;
        public TaskbarLayoutObserver(Action<TaskbarPlacement> updated)
        {
            this.updated = updated;
            propertyHandler = delegate(object sender, AutomationPropertyChangedEventArgs args) { Changed(sender); };
            structureHandler = delegate(object sender, StructureChangedEventArgs args) { Changed(sender); };
            worker = new Thread(Run) { IsBackground = true, Name = "Taskbar layout events" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            Request();
        }
        public void Request() { if (!stopping) requested.Set(); }
        public void SetEnabled(bool value) { enabled = value; Request(); }
        private void Changed(object sender)
        {
            if (stopping) return;
            try
            {
                AutomationElement element = sender as AutomationElement;
                if (element != null && element.Current.ProcessId == ownProcess) return;
            }
            catch (ElementNotAvailableException) { }
            Request();
        }
        private void Unsubscribe()
        {
            if (subscribedRoot == null) return;
            try { Automation.RemoveAutomationPropertyChangedEventHandler(subscribedRoot, propertyHandler); } catch (Exception) { }
            try { Automation.RemoveStructureChangedEventHandler(subscribedRoot, structureHandler); } catch (Exception) { }
            subscribedRoot = null; subscribedWindow = IntPtr.Zero;
        }
        private void Run()
        {
            try
            {
                while (!stopping)
                {
                    requested.WaitOne();
                    if (stopping) break;
                    if (!enabled) { Unsubscribe(); continue; }
                    // Coalesce one burst of shell animation/layout events.
                    Thread.Sleep(150);
                    requested.WaitOne(0);
                    TaskbarPlacement placement;
                    try { placement = Inspect(); }
                    catch (Exception ex) { placement = new TaskbarPlacement { Detail = "任务栏定位失败：" + ex.GetType().Name }; }
                    if (!stopping) updated(placement);
                }
            }
            finally { Unsubscribe(); requested.Dispose(); }
        }
        private TaskbarPlacement Inspect()
        {
            IntPtr taskbar = TaskbarNative.FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) { Unsubscribe(); return new TaskbarPlacement { Detail = "等待任务栏启动" }; }
            Rectangle bar = TaskbarNative.Bounds(taskbar);
            uint dpi = TaskbarNative.GetDpiForWindow(taskbar);
            if (dpi == 0) dpi = 96;
            if (bar.Width < bar.Height) return new TaskbarPlacement { Detail = "当前仅支持横向任务栏，已使用备用托盘图标" };
            AutomationElement root = AutomationElement.FromHandle(taskbar);
            if (subscribedWindow != taskbar)
            {
                Unsubscribe();
                lastWeather = Rectangle.Empty;
                subscribedRoot = root; subscribedWindow = taskbar;
                Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree, propertyHandler,
                    AutomationElement.BoundingRectangleProperty, AutomationElement.IsOffscreenProperty);
                Automation.AddStructureChangedEventHandler(root, TreeScope.Subtree, structureHandler);
            }
            AutomationElementCollection buttons = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            Rectangle weather = Rectangle.Empty;
            List<Rectangle> occupied = new List<Rectangle>();
            foreach (AutomationElement button in buttons)
            {
                AutomationElement.AutomationElementInformation current = button.Current;
                if (current.ProcessId == ownProcess || current.IsOffscreen) continue;
                System.Windows.Rect r = current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) continue;
                Rectangle bounds = Rectangle.FromLTRB((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
                occupied.Add(bounds);
                if (current.AutomationId == "WidgetsButton") weather = bounds;
            }
            // Menus/animations can temporarily omit the widget from the UIA tree.
            // Retain its last position while the taskbar geometry is unchanged.
            if (!weather.IsEmpty) lastWeather = weather;
            else if (lastBar == bar) weather = lastWeather;
            else lastWeather = Rectangle.Empty;
            lastBar = bar;
            IntPtr tray = TaskbarNative.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            Rectangle trayBounds = TaskbarNative.Bounds(tray);
            if (!trayBounds.IsEmpty) occupied.Add(trayBounds);
            Rectangle anchor = weather;
            if (anchor.IsEmpty)
            {
                anchor = trayBounds;
                if (anchor.IsEmpty) return new TaskbarPlacement { Detail = "未找到天气或通知区域，已使用备用托盘图标" };
            }
            int width = (int)Math.Ceiling(112 * dpi / 96.0);
            Rectangle slot = TaskbarGeometry.Beside(bar, anchor, occupied, width, (int)Math.Ceiling(8 * dpi / 96.0));
            if (slot.IsEmpty) return new TaskbarPlacement { Detail = "天气旁空间不足，已使用备用托盘图标" };
            return new TaskbarPlacement {
                Taskbar = taskbar, Bounds = slot, Dpi = dpi,
                Detail = weather.IsEmpty ? "任务栏通知区域旁" : "任务栏天气左侧"
            };
        }
        public void Dispose() { stopping = true; requested.Set(); worker.Join(1000); }
    }

    internal sealed class TaskbarReadout : Form
    {
        private string label = "-- kHz";
        private Color color = Color.LightGray;
        private readonly ToolTip tooltip = new ToolTip();
        private Rectangle previousBounds;
        private IntPtr attachedTaskbar;
        private bool available;
        private IntPtr reorderHook;
        private readonly TaskbarNative.WinEventProc reorderCallback;
        private string paintedKey;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000; return p; }
        }
        public TaskbarReadout(ContextMenuStrip menu)
        {
            Text = "ASIO Rate Monitor 任务栏采样率";
            AccessibleName = "ASIO Rate Monitor -- kHz";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 32, 32);
            Font = CreateReadoutFont(15);
            ContextMenuStrip = menu;
            // Shell child composition can cover ordinary GDI children even when
            // IsWindowVisible and mouse hit testing report them as present.
            // Use a per-pixel composited surface, with no color-key click-through.
            reorderCallback = delegate(IntPtr hook, uint kind, IntPtr window, int obj, int child, uint thread, uint time) {
                if (!IsDisposed && Visible && window == attachedTaskbar)
                    EnsureAboveSiblings();
            };
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            paintedKey = null;
        }
        private void EnsureAboveSiblings()
        {
            if (attachedTaskbar != IntPtr.Zero && TaskbarNative.GetWindow(attachedTaskbar, 5) != Handle)
                TaskbarNative.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002);
        }
        internal static Font CreateReadoutFont(float size)
        {
            Font font = new Font("Segoe UI Semibold", size, FontStyle.Regular, GraphicsUnit.Pixel);
            if (font.Name == "Segoe UI Semibold") return font;
            font.Dispose();
            return new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        }
        private void UpdatePalette()
        {
            bool light = false;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    light = key != null && Object.Equals(key.GetValue("SystemUsesLightTheme"), 1);
            }
            catch (System.Security.SecurityException) { }
            color = available ? (light ? Color.FromArgb(30, 30, 30) : Color.White)
                : (light ? Color.DimGray : Color.FromArgb(175, 180, 185));
        }
        public void UpdateReading(RateReading reading, string mode)
        {
            string next = reading.State == ReadingState.Available ? reading.Label : "-- kHz";
            available = reading.State == ReadingState.Available;
            UpdatePalette();
            if (label != next)
            {
                label = next;
                AccessibleName = "ASIO Rate Monitor " + label;
            }
            tooltip.SetToolTip(this, "ASIO Rate Monitor · " + (reading.State == ReadingState.Available ? reading.Hertz + " Hz" : reading.Detail) + "\n" + mode + "\n右键打开设置");
            if (Visible) PaintLayer();
        }
        public bool Attach(TaskbarPlacement placement)
        {
            if (!placement.Valid || !TaskbarNative.IsWindow(placement.Taskbar)) { Hide(); return false; }
            UpdatePalette();
            if (attachedTaskbar != placement.Taskbar || TaskbarNative.GetParent(Handle) != placement.Taskbar)
            {
                long style = TaskbarNative.GetWindowLongPtr(Handle, -16).ToInt64();
                TaskbarNative.SetWindowLongPtr(Handle, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L | 0x04000000L));
                TaskbarNative.SetLastError(0);
                IntPtr old = TaskbarNative.SetParent(Handle, placement.Taskbar);
                if (old == IntPtr.Zero && Marshal.GetLastWin32Error() != 0) { Hide(); return false; }
                attachedTaskbar = placement.Taskbar;
                if (reorderHook != IntPtr.Zero) TaskbarNative.UnhookWinEvent(reorderHook);
                uint shellProcess;
                TaskbarNative.GetWindowThreadProcessId(attachedTaskbar, out shellProcess);
                // Out-of-context event delivery; no injected code and no timer.
                reorderHook = TaskbarNative.SetWinEventHook(0x8004, 0x8004, IntPtr.Zero, reorderCallback, shellProcess, 0, 2);
                previousBounds = Rectangle.Empty;
            }
            if (previousBounds != placement.Bounds || !Visible)
            {
                float size = (float)(15 * placement.Dpi / 96.0);
                if (Math.Abs(Font.Size - size) > 0.1f)
                {
                    Font old = Font; Font = CreateReadoutFont(size); old.Dispose();
                }
                TaskbarNative.Point origin = new TaskbarNative.Point { X = placement.Bounds.X, Y = placement.Bounds.Y };
                TaskbarNative.ScreenToClient(placement.Taskbar, ref origin);
                if (!Visible) Show();
                TaskbarNative.SetWindowPos(Handle, IntPtr.Zero, origin.X, origin.Y,
                    placement.Bounds.Width, placement.Bounds.Height, 0x0010 | 0x0040);
                previousBounds = placement.Bounds;
            }
            EnsureAboveSiblings();
            return PaintLayer();
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084) { m.Result = new IntPtr(1); return; } // HTCLIENT
            if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            if (m.Msg == 0x007B) // WM_CONTEXTMENU: never forward to Explorer.
            {
                if (ContextMenuStrip != null && !ContextMenuStrip.Visible)
                {
                    Point point = m.LParam.ToInt64() == -1 ? new Point(Width / 2, Height / 2)
                        : PointToClient(new Point(unchecked((short)(m.LParam.ToInt64() & 0xffff)),
                            unchecked((short)((m.LParam.ToInt64() >> 16) & 0xffff))));
                    ContextMenuStrip.Show(this, point);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // The compositor receives premultiplied pixels in PaintLayer.
        }
        private bool PaintLayer()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0) return false;
            string key = label + ":" + Width + ":" + Height + ":" + Font.Size + ":" + color.ToArgb();
            if (paintedKey == key) return true;
            try
            {
                using (Bitmap bitmap = LayeredReadoutRenderer.Draw(Width, Height, label, Font, color))
                    LayeredReadoutRenderer.Present(Handle, bitmap);
            }
            catch (System.ComponentModel.Win32Exception) { Hide(); paintedKey = null; return false; }
            paintedKey = key;
            return true;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (reorderHook != IntPtr.Zero) { TaskbarNative.UnhookWinEvent(reorderHook); reorderHook = IntPtr.Zero; }
                tooltip.Dispose(); Font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ShellMessageWindow : Form
    {
        private readonly uint taskbarCreated = TaskbarNative.RegisterWindowMessage("TaskbarCreated");
        public event Action<bool> LayoutChanged;
        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == taskbarCreated || m.Msg == 0x007E || m.Msg == 0x001A)
                if (LayoutChanged != null) LayoutChanged((uint)m.Msg == taskbarCreated);
            base.WndProc(ref m);
        }
    }
}

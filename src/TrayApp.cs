using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AsioRateMonitor
{
    internal sealed class TrayApp : ApplicationContext
    {
        private readonly ShellMessageWindow dispatcher = new ShellMessageWindow();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly Preferences preferences = Preferences.Load();
        private readonly NotifyIcon tray;
        private readonly ToolStripMenuItem statusItem, detailItem, modeItem, timeItem, countItem, layoutItem;
        private readonly ToolStripMenuItem eventChoice, pollingChoice;
        private readonly RateMonitor monitor;
        private readonly TaskbarLayoutObserver layout;
        private readonly RegisteredWaitHandle exitWait;
        private TaskbarReadout readout;
        private Icon currentIcon;
        private RateReading latest = new RateReading(ReadingState.Stale, 0, "正在连接 Dreamus");
        private string renderedKey;
        private bool exiting;
        public TrayApp(WaitHandle exitSignal)
        {
            IntPtr handle = dispatcher.Handle;
            menu.Items.Add(new ToolStripMenuItem("ASIO Rate Monitor") { Enabled = false });
            statusItem = new ToolStripMenuItem { Enabled = false };
            detailItem = new ToolStripMenuItem { Enabled = false };
            modeItem = new ToolStripMenuItem { Enabled = false };
            timeItem = new ToolStripMenuItem { Enabled = false };
            countItem = new ToolStripMenuItem { Enabled = false };
            layoutItem = new ToolStripMenuItem("正在定位天气旁空位") { Enabled = false };
            menu.Items.AddRange(new ToolStripItem[] { statusItem, detailItem, modeItem, timeItem, countItem, layoutItem, new ToolStripSeparator() });
            ToolStripMenuItem modes = new ToolStripMenuItem("更新方式");
            eventChoice = new ToolStripMenuItem("事件通知（推荐）") { Checked = preferences.Mode == MonitorMode.Events };
            pollingChoice = new ToolStripMenuItem("定时轮询（兼容模式）") { Checked = preferences.Mode == MonitorMode.Polling };
            eventChoice.Click += delegate { ChangeMode(MonitorMode.Events); };
            pollingChoice.Click += delegate { ChangeMode(MonitorMode.Polling); };
            modes.DropDownItems.Add(eventChoice); modes.DropDownItems.Add(pollingChoice);
            menu.Items.Add(modes);
            ToolStripMenuItem intervalMenu = new ToolStripMenuItem("轮询间隔（含保底）");
            foreach (int value in new[] { 1000, 2000, 5000 })
            {
                int delay = value;
                ToolStripMenuItem choice = new ToolStripMenuItem(value / 1000 + " 秒") { Checked = preferences.Interval == value };
                choice.Click += delegate {
                    preferences.Interval = delay; monitor.Interval = delay;
                    foreach (ToolStripMenuItem item in intervalMenu.DropDownItems) item.Checked = item == choice;
                    preferences.Save();
                };
                intervalMenu.DropDownItems.Add(choice);
            }
            menu.Items.Add(intervalMenu);
            ToolStripMenuItem showText = new ToolStripMenuItem("显示任务栏采样率文字") { Checked = preferences.TaskbarText };
            showText.Click += delegate {
                preferences.TaskbarText = !preferences.TaskbarText;
                showText.Checked = preferences.TaskbarText;
                preferences.Save();
                layout.SetEnabled(preferences.TaskbarText);
                if (!preferences.TaskbarText)
                {
                    readout.Hide();
                    layoutItem.Text = "任务栏文字已关闭";
                }
            };
            menu.Items.Add(showText);
            menu.Items.Add("查看运行状态", null, delegate { ShowStatus(); });
            menu.Items.Add("立即读取一次", null, delegate { monitor.Refresh(); });
            menu.Items.Add("重新定位任务栏文字", null, delegate { layout.Request(); });
            menu.Items.Add("关于", null, delegate {
                MessageBox.Show("ASIO Rate Monitor " + typeof(TrayApp).Assembly.GetName().Version.ToString(3)
                    + "\n\n天气旁文字直接显示驱动报告的采样率，托盘图标常驻。\n事件模式在采样率或设备状态变化时读取；\n可随时切换轮询，通知不可用时也会自动保底。\n\n不创建音频流，不修改采样率或音频参数。\n退出时只释放自己的状态连接与显示窗口。",
                    "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitThread(); });
            menu.Opening += delegate {
                timeItem.Text = "最近状态读取：" + latest.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
                countItem.Text = "本次运行：查询 " + monitor.QueryCount + " 次 · 驱动通知 " + monitor.EventCount + " 次";
                modeItem.Text = monitor.ModeText;
            };
            readout = new TaskbarReadout(menu);
            tray = new NotifyIcon { ContextMenuStrip = menu, Text = "ASIO Rate Monitor · 正在连接" };
            tray.DoubleClick += delegate { ShowStatus(); };
            monitor = new RateMonitor(ProviderFactory.CreateDefault());
            monitor.Interval = preferences.Interval;
            monitor.Mode = preferences.Mode;
            monitor.Updated += OnUpdated;
            Render(latest);
            tray.Visible = true;
            layout = new TaskbarLayoutObserver(OnPlacement);
            layout.SetEnabled(preferences.TaskbarText);
            if (!preferences.TaskbarText) layoutItem.Text = "任务栏文字已关闭";
            menu.Closed += delegate { if (!exiting) layout.Request(); };
            dispatcher.LayoutChanged += delegate(bool recreated) {
                if (exiting) return;
                if (recreated)
                {
                    readout.Dispose(); readout = new TaskbarReadout(menu);
                    readout.UpdateReading(latest, monitor.ModeText);
                }
                layout.Request();
            };
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            exitWait = ThreadPool.RegisterWaitForSingleObject(exitSignal,
                delegate { Dispatch(delegate { ExitThread(); }); }, null, Timeout.Infinite, true);
            monitor.Start();
        }
        private void ChangeMode(MonitorMode value)
        {
            preferences.Mode = value;
            eventChoice.Checked = value == MonitorMode.Events;
            pollingChoice.Checked = value == MonitorMode.Polling;
            preferences.Save();
            monitor.Mode = value;
        }
        private void Dispatch(Action action)
        {
            if (exiting) return;
            try { dispatcher.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }
        private void OnUpdated(RateReading reading)
        {
            Dispatch(delegate { if (!exiting) { latest = reading; Render(reading); } });
        }
        private void OnPlacement(TaskbarPlacement placement)
        {
            Dispatch(delegate {
                if (exiting || menu.Visible) return;
                // A placement queued before the user disabled text must not show it again.
                if (!preferences.TaskbarText) { readout.Hide(); return; }
                try
                {
                    bool docked = readout.Attach(placement);
                    layoutItem.Text = docked ? placement.Detail : placement.Valid ? "任务栏嵌入未成功，已使用备用图标" : placement.Detail;
                }
                catch (Exception)
                {
                    readout.Hide();
                    layoutItem.Text = "任务栏窗口不可用，已使用备用图标";
                }
            });
        }
        private void ShowStatus()
        {
            MessageBox.Show("ASIO Rate Monitor 正在运行\n\n当前：" + latest.Label
                + "\n" + latest.Detail + "\n更新方式：" + monitor.ModeText
                + "\n文字位置：" + layoutItem.Text
                + "\n\n右键托盘图标可切换更新方式、重新定位文字或退出。",
                "ASIO Rate Monitor 运行状态", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Suspend && e.Mode != PowerModes.Resume) return;
            Dispatch(delegate {
                if (exiting) return;
                bool suspended = e.Mode == PowerModes.Suspend;
                latest = new RateReading(ReadingState.Suspended, 0, suspended ? "系统休眠，查询已暂停" : "已唤醒，正在重新读取");
                Render(latest);
                monitor.Suspend(suspended);
                if (!suspended) layout.Request();
            });
        }
        private void Render(RateReading reading)
        {
            string key = reading.State + ":" + reading.Hertz + ":" + reading.Detail + ":" + monitor.ModeText;
            if (key == renderedKey) return;
            renderedKey = key;
            Icon next = RateIcon.Create(reading, Math.Max(32, SystemInformation.SmallIconSize.Width * 2));
            tray.Icon = next;
            if (currentIcon != null) currentIcon.Dispose();
            currentIcon = next;
            string text = reading.State == ReadingState.Available ? "ASIO Rate Monitor · " + reading.Label + " (" + reading.Hertz + " Hz)" : "ASIO Rate Monitor · " + reading.Detail;
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            statusItem.Text = reading.State == ReadingState.Available ? "当前：" + reading.Label + " · " + reading.Hertz + " Hz" : "当前：未知";
            detailItem.Text = reading.Detail; modeItem.Text = monitor.ModeText;
            readout.UpdateReading(reading, monitor.ModeText);
        }
        protected override void ExitThreadCore()
        {
            if (exiting) return;
            exiting = true;
            exitWait.Unregister(null);
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            layout.Dispose();
            monitor.Updated -= OnUpdated;
            monitor.Dispose();
            tray.Visible = false; tray.Dispose();
            currentIcon.Dispose();
            readout.Dispose(); menu.Dispose(); dispatcher.Dispose();
            base.ExitThreadCore();
        }
    }
}

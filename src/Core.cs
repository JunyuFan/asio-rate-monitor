using System;
using System.Globalization;
using System.Threading;

namespace AsioRateMonitor
{
    public enum ReadingState { Available, NoDriver, NoDevice, MultipleDevices, Error, Suspended, Stale }

    public sealed class RateReading
    {
        public readonly ReadingState State;
        public readonly uint Hertz;
        public readonly string Detail;
        public readonly DateTime TimestampUtc;
        public RateReading(ReadingState state, uint hertz, string detail)
        {
            State = state;
            // An error must never retain a formerly valid reading.
            Hertz = state == ReadingState.Available ? hertz : 0;
            Detail = detail ?? "";
            TimestampUtc = DateTime.UtcNow;
        }
        public static RateReading Success(uint hertz)
        {
            return hertz == 0 ? new RateReading(ReadingState.Error, 0, "驱动未提供有效采样率")
                : new RateReading(ReadingState.Available, hertz, "驱动实时状态");
        }
        public string Kilohertz { get { return Hertz == 0 ? "--" : (Hertz / 1000.0).ToString("0.###", CultureInfo.InvariantCulture); } }
        public string Label { get { return State == ReadingState.Available ? Kilohertz + " kHz" : "-- kHz"; } }
    }

    // A provider may use only a vendor's verified status interface. Implementations
    // own their control handles; they must not create audio streams or change settings.
    // Calls are serialized on one background thread, including Release and Dispose.
    public interface ISampleRateProvider : IDisposable
    {
        string Name { get; }
        string DriverPath { get; }
        RateReading Read();
        void Release();
    }

    public interface IChangeNotifications
    {
        WaitHandle[] ChangeHandles { get; }
        bool CanWaitForChanges { get; }
        string NotificationDetail { get; }
        void EnableNotifications(bool enabled);
        void ConsumeChange(int index);
    }

    public enum MonitorMode { Events, Polling }

    public static class ProviderFactory
    {
        public static ISampleRateProvider CreateDefault() { return new DreamusProvider(); }
    }

    public static class MonitorPolicy
    {
        public static int Delay(RateReading reading, int interval)
        {
            return reading.State == ReadingState.Available ? interval : Math.Max(interval, 5000);
        }
        public static int WaitMilliseconds(MonitorMode mode, bool canWaitForChanges, RateReading reading, int interval)
        {
            if (mode == MonitorMode.Events && canWaitForChanges &&
                (reading.State == ReadingState.Available || reading.State == ReadingState.NoDevice ||
                 reading.State == ReadingState.MultipleDevices))
                return Timeout.Infinite;
            return Delay(reading, interval);
        }
    }

    public sealed class RateMonitor : IDisposable
    {
        private readonly ISampleRateProvider provider;
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Thread worker;
        private readonly object publishGate = new object();
        private readonly System.Threading.Timer timeout;
        private volatile bool stopping, suspended;
        private volatile int interval = 1000;
        private volatile MonitorMode mode = MonitorMode.Events;
        private volatile string modeText = "正在连接";
        private bool queryActive;
        private long queryCount, eventCount;
        public event Action<RateReading> Updated;
        public MonitorMode Mode { get { return mode; } set { mode = value; if (worker.IsAlive) wake.Set(); } }
        public string ModeText { get { return modeText; } }
        public long QueryCount { get { return Interlocked.Read(ref queryCount); } }
        public long EventCount { get { return Interlocked.Read(ref eventCount); } }
        public int Interval
        {
            get { return interval; }
            set
            {
                if (value < 1000 || value > 5000) throw new ArgumentOutOfRangeException("value");
                interval = value;
                if (worker.IsAlive) wake.Set();
            }
        }
        public RateMonitor(ISampleRateProvider provider)
        {
            this.provider = provider;
            worker = new Thread(Run) { IsBackground = true, Name = "Sample rate status reader" };
            timeout = new System.Threading.Timer(delegate {
                lock (publishGate)
                    if (queryActive) Publish(new RateReading(ReadingState.Stale, 0, "驱动查询超时，等待响应"));
            }, null, Timeout.Infinite, Timeout.Infinite);
        }
        public void Start() { worker.Start(); }
        public void Refresh() { wake.Set(); }
        public void Suspend(bool value) { suspended = value; wake.Set(); }
        private void Publish(RateReading reading)
        {
            var callback = Updated;
            if (!stopping && callback != null) callback(reading);
        }
        private void Run()
        {
            IChangeNotifications notifications = provider as IChangeNotifications;
            int signaled = 0;
            try
            {
                while (!stopping)
                {
                    if (suspended)
                    {
                        provider.Release();
                        modeText = "休眠期间暂停";
                        Publish(new RateReading(ReadingState.Suspended, 0, "系统休眠，查询已暂停"));
                        wake.WaitOne();
                        signaled = 0;
                        continue;
                    }
                    RateReading reading;
                    lock (publishGate) queryActive = true;
                    timeout.Change(10000, Timeout.Infinite);
                    try
                    {
                        if (notifications != null)
                        {
                            notifications.EnableNotifications(mode == MonitorMode.Events);
                            if (signaled > 0)
                            {
                                notifications.ConsumeChange(signaled - 1);
                                Interlocked.Increment(ref eventCount);
                            }
                        }
                        Interlocked.Increment(ref queryCount);
                        reading = provider.Read();
                    }
                    catch (Exception ex)
                    {
                        provider.Release();
                        reading = new RateReading(ReadingState.Error, 0, "读取失败：" + ex.GetType().Name);
                    }
                    timeout.Change(Timeout.Infinite, Timeout.Infinite);
                    bool canWait = notifications != null && notifications.CanWaitForChanges;
                    int wait = MonitorPolicy.WaitMilliseconds(mode, canWait, reading, interval);
                    modeText = mode == MonitorMode.Polling ? "轮询 · " + interval / 1000 + " 秒"
                        : wait == Timeout.Infinite ? "事件通知 · 无定时查询"
                        : "轮询保底 · " + (notifications == null ? "驱动无通知接口" : notifications.NotificationDetail);
                    lock (publishGate)
                    {
                        queryActive = false;
                        if (!suspended) Publish(reading);
                    }
                    if (suspended) { signaled = 0; continue; }
                    WaitHandle[] changes = notifications == null ? new WaitHandle[0] : notifications.ChangeHandles;
                    WaitHandle[] handles = new WaitHandle[changes.Length + 1];
                    handles[0] = wake;
                    Array.Copy(changes, 0, handles, 1, changes.Length);
                    signaled = WaitHandle.WaitAny(handles, wait);
                    if (signaled == WaitHandle.WaitTimeout) signaled = 0;
                }
            }
            finally
            {
                provider.Dispose();
                timeout.Dispose();
                wake.Dispose();
            }
        }
        public void Dispose()
        {
            stopping = true;
            wake.Set();
            // Never abort a native driver call. Keep the UI responsive; OS process
            // cleanup handles an exceptionally stuck native call on application exit.
            worker.Join(1500);
        }
    }
}

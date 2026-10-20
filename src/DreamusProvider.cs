using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace AsioRateMonitor
{
    public sealed class DreamusProvider : ISampleRateProvider, IChangeNotifications
    {
        private const string ApiFile = "DreamusUsbAudioapi_x64.dll";
        private IntPtr library, device;
        private Enumerate enumerate;
        private GetCount getCount;
        private Open open;
        private GetRate getRate;
        private Close close;
        private RegisterDevice registerDevice;
        private ReadNotification readNotification;
        private RegisterPnp registerPnp;
        private UnregisterPnp unregisterPnp;
        private readonly AutoResetEvent changed = new AutoResetEvent(false);
        private readonly AutoResetEvent arrived = new AutoResetEvent(false);
        private readonly AutoResetEvent removed = new AutoResetEvent(false);
        private readonly byte[] notificationBuffer = new byte[4096];
        private bool wantNotifications = true, pnpRegistered, deviceRegistered;
        private string notificationDetail = "等待驱动连接";
        public bool CanWaitForChanges { get { return pnpRegistered && (device == IntPtr.Zero || deviceRegistered); } }
        public string NotificationDetail { get { return notificationDetail; } }
        public WaitHandle[] ChangeHandles { get { return new WaitHandle[] { changed, arrived, removed }; } }
        public string Name { get { return "Dreamus USB Audio Device"; } }
        public string DriverPath { get; private set; }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Enumerate();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetCount();
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Open(uint index, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint GetRate(IntPtr handle, out uint hertz);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Close(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint RegisterDevice(IntPtr handle, uint filter, IntPtr signal, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint ReadNotification(IntPtr handle, out uint kind, byte[] data, uint size, out uint bytes);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint RegisterPnp(IntPtr arrival, IntPtr removal, IntPtr window, uint msg, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void UnregisterPnp();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);

        private static string FindDriver()
        {
            // Read the 64-bit system installation, never search the working directory
            // or download/ship a replacement driver DLL.
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey asio = machine.OpenSubKey(@"SOFTWARE\ASIO\Dreamus USB Audio Device"))
            {
                string clsid = asio == null ? null : asio.GetValue("CLSID") as string;
                Guid parsed;
                if (Guid.TryParse(clsid, out parsed))
                {
                    using (RegistryKey server = machine.OpenSubKey(@"SOFTWARE\Classes\CLSID\" + parsed.ToString("B") + @"\InprocServer32"))
                    {
                        string asioPath = server == null ? null : server.GetValue(null) as string;
                        if (!String.IsNullOrEmpty(asioPath))
                        {
                            string directory = Path.GetDirectoryName(Environment.ExpandEnvironmentVariables(asioPath.Trim('"')));
                            string candidate = Path.Combine(directory, ApiFile);
                            if (Path.IsPathRooted(candidate) && File.Exists(candidate)) return Path.GetFullPath(candidate);
                        }
                    }
                }
            }
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Dreamus\USB Audio Device Driver\x64\" + ApiFile);
            return File.Exists(fallback) ? fallback : null;
        }
        private T Bind<T>(string name) where T : class
        {
            IntPtr entry = GetProcAddress(library, name);
            if (entry == IntPtr.Zero) throw new EntryPointNotFoundException(name);
            return Marshal.GetDelegateForFunctionPointer(entry, typeof(T)) as T;
        }
        private RateReading Load()
        {
            if (library != IntPtr.Zero) return null;
            DriverPath = FindDriver();
            if (DriverPath == null) return new RateReading(ReadingState.NoDriver, 0, "未找到已安装的 Dreamus 64 位驱动");
            // Search dependencies only beside the trusted installed DLL and in the
            // system/default directories. No current-directory DLL search.
            library = LoadLibraryEx(DriverPath, IntPtr.Zero, 0x00000100 | 0x00001000);
            if (library == IntPtr.Zero) return new RateReading(ReadingState.Error, 0, "驱动接口加载失败：" + Marshal.GetLastWin32Error());
            try
            {
                enumerate = Bind<Enumerate>("TUSBAUDIO_EnumerateDevices");
                getCount = Bind<GetCount>("TUSBAUDIO_GetDeviceCount");
                open = Bind<Open>("TUSBAUDIO_OpenDeviceByIndex");
                getRate = Bind<GetRate>("TUSBAUDIO_GetCurrentSampleRate");
                close = Bind<Close>("TUSBAUDIO_CloseDevice");
                try
                {
                    registerDevice = Bind<RegisterDevice>("TUSBAUDIO_RegisterDeviceNotification");
                    readNotification = Bind<ReadNotification>("TUSBAUDIO_ReadDeviceNotification");
                    registerPnp = Bind<RegisterPnp>("TUSBAUDIO_RegisterPnpNotification");
                    unregisterPnp = Bind<UnregisterPnp>("TUSBAUDIO_UnregisterPnpNotification");
                    uint status = registerPnp(arrived.SafeWaitHandle.DangerousGetHandle(),
                        removed.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, 0, 0);
                    pnpRegistered = status == 0;
                    notificationDetail = pnpRegistered ? "等待采样率通知" : "设备通知订阅失败：0x" + status.ToString("X8");
                }
                catch (EntryPointNotFoundException) { notificationDetail = "驱动缺少通知接口"; }
                return null;
            }
            catch
            {
                FreeLibrary(library); library = IntPtr.Zero;
                return new RateReading(ReadingState.Error, 0, "该 Dreamus 驱动缺少所需查询接口");
            }
        }
        public RateReading Read()
        {
            RateReading failure = Load();
            if (failure != null) return failure;
            uint status;
            if (device == IntPtr.Zero)
            {
                status = enumerate();
                if (status != 0) return Error("枚举设备", status);
                uint count = getCount();
                if (count == 0) return new RateReading(ReadingState.NoDevice, 0, "Dreamus 设备未连接");
                if (count != 1) return new RateReading(ReadingState.MultipleDevices, 0, "检测到多个 Dreamus 设备，暂不自动选择");
                IntPtr handle;
                status = open(0, out handle);
                if (status != 0) return Error("连接状态接口", status);
                device = handle;
                if (device == IntPtr.Zero) return new RateReading(ReadingState.Error, 0, "驱动返回无效设备连接");
            }
            ConfigureDeviceNotifications();
            uint hertz;
            status = getRate(device, out hertz);
            if (status != 0)
            {
                Release();
                return Error("读取采样率", status);
            }
            return RateReading.Success(hertz);
        }
        private static RateReading Error(string operation, uint status)
        {
            return new RateReading(ReadingState.Error, 0, operation + "失败（0x" + status.ToString("X8") + "）");
        }
        public void Release()
        {
            if (device == IntPtr.Zero) return;
            IntPtr old = device;
            device = IntPtr.Zero;
            if (deviceRegistered) registerDevice(old, 0, IntPtr.Zero, 0);
            deviceRegistered = false;
            close(old);
            changed.Reset();
        }
        public void EnableNotifications(bool enabled)
        {
            wantNotifications = enabled;
            ConfigureDeviceNotifications();
        }
        private void ConfigureDeviceNotifications()
        {
            if (device == IntPtr.Zero || registerDevice == null || readNotification == null) return;
            if (!wantNotifications)
            {
                if (deviceRegistered) registerDevice(device, 0, IntPtr.Zero, 0);
                deviceRegistered = false;
                changed.Reset();
                return;
            }
            if (deviceRegistered) return;
            // Sample-rate category 0x1 and the queue-empty status below were
            // verified against the installed Dreamus 5.50.0 driver.
            uint status = registerDevice(device, 1, changed.SafeWaitHandle.DangerousGetHandle(), 0);
            deviceRegistered = status == 0;
            notificationDetail = deviceRegistered ? "采样率通知已订阅" : "采样率通知订阅失败：0x" + status.ToString("X8");
        }
        public void ConsumeChange(int index)
        {
            if (index == 1 || index == 2)
            {
                Release();
                return;
            }
            if (!deviceRegistered || device == IntPtr.Zero) return;
            // Drain the queue so the driver's event can return to a nonsignaled
            // state. A bounded loop also protects against a malfunctioning driver.
            for (int i = 0; i < 256; i++)
            {
                uint kind, bytes;
                uint status = readNotification(device, out kind, notificationBuffer, (uint)notificationBuffer.Length, out bytes);
                if (status == 0xEE000022) return;
                if (status != 0) { Release(); throw new InvalidOperationException("Notification status 0x" + status.ToString("X8")); }
            }
            Release();
            throw new InvalidOperationException("Excessive notification queue");
        }
        public void Dispose()
        {
            Release();
            if (pnpRegistered) { unregisterPnp(); pnpRegistered = false; }
            if (library != IntPtr.Zero) { FreeLibrary(library); library = IntPtr.Zero; }
            changed.Dispose(); arrived.Dispose(); removed.Dispose();
        }
    }
}

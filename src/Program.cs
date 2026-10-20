using System;
using System.Threading;
using System.Windows.Forms;

namespace AsioRateMonitor
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--exit")
            {
                try { using (EventWaitHandle quit = EventWaitHandle.OpenExisting(@"Local\AsioRateMonitor.Exit.v1")) quit.Set(); return 0; }
                catch (WaitHandleCannotBeOpenedException) { return 1; }
            }
            bool created;
            using (Mutex mutex = new Mutex(true, @"Local\AsioRateMonitor.v1", out created))
            {
                if (!created)
                {
                    MessageBox.Show("采样率监视器已经运行。请查看右下角托盘的采样率数字图标，也可能在“显示隐藏的图标”中。\n\n双击图标查看运行状态，右键设置或退出。", "ASIO Rate Monitor");
                    return 0;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    TaskbarNative.MatchTaskbarDpi();
                    using (EventWaitHandle exit = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AsioRateMonitor.Exit.v1"))
                        Application.Run(new TrayApp(exit));
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("程序未能启动：" + ex.Message, "ASIO Rate Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }

        }
    }
}

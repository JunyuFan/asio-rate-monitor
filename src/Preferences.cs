using System;
using System.IO;

namespace AsioRateMonitor
{
    internal sealed class Preferences
    {
        public int Interval = 1000;
        public MonitorMode Mode = MonitorMode.Events;
        public bool TaskbarText = true;
        private static string FileName { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); } }
        public static Preferences Load()
        {
            return Load(FileName);
        }
        internal static Preferences Load(string path)
        {
            Preferences p = new Preferences();
            try
            {
                if (!File.Exists(path)) return p;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] pair = line.Split('=');
                    if (pair.Length != 2) continue;
                    int value;
                    if (pair[0] == "interval" && Int32.TryParse(pair[1], out value) && (value == 1000 || value == 2000 || value == 5000)) p.Interval = value;
                    if (pair[0] == "mode") p.Mode = pair[1] == "polling" ? MonitorMode.Polling : MonitorMode.Events;
                    if (pair[0] == "taskbarText") p.TaskbarText = pair[1] != "false";
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return p;
        }
        public void Save()
        {
            Save(FileName);
        }
        internal void Save(string path)
        {
            try { File.WriteAllLines(path, new[] {
                "interval=" + Interval, "mode=" + (Mode == MonitorMode.Events ? "events" : "polling"),
                "taskbarText=" + (TaskbarText ? "true" : "false") }); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

using System;
using System.Text;

namespace phanLoaiCaChua
{
    public static class AppLogger
    {
        private static readonly StringBuilder _sb = new StringBuilder();
        private static readonly object _lock = new object();

        public static event Action<string> LogAdded;

        public static void Log(string message)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (_lock)
            {
                _sb.AppendLine(line);
            }
            LogAdded?.Invoke(line);
        }

        public static string GetAll()
        {
            lock (_lock)
            {
                return _sb.ToString();
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _sb.Clear();
            }
        }
    }
}

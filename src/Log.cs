using System;
using System.IO;
using System.Text;

namespace MouseMeter
{
    /// <summary>
    /// Rotating diagnostic log. Silent unless --verbose was passed or something fails.
    /// Deliberately does NOT reuse the v1 file name (log.txt) so stale output can never
    /// be mistaken for the current process writing.
    /// </summary>
    public static class Log
    {
        const long MaxBytes = 512 * 1024;
        const int KeepFiles = 3;
        static readonly object _gate = new object();
        static volatile bool _verbose;

        public static bool Verbose { get { return _verbose; } set { _verbose = value; } }

        public static string Dir
        {
            get
            {
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseMeter");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string FilePath { get { return Path.Combine(Dir, "error.log"); } }

        public static void Info(string msg) { if (_verbose) Write("INFO ", msg); }
        public static void Warn(string msg) { Write("WARN ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        public static void Exception(string where, Exception ex)
        {
            Write("ERROR", where + ": " + (ex == null ? "(null)" : ex.ToString()));
        }

        static void Write(string level, string msg)
        {
            try
            {
                lock (_gate)
                {
                    string path = FilePath;
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxBytes) Rotate(path);
                    }
                    catch { }

                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + msg + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }

        static void Rotate(string path)
        {
            try
            {
                for (int i = KeepFiles - 1; i >= 1; i--)
                {
                    string from = i == 1 ? path : path + "." + (i - 1);
                    string to = path + "." + i;
                    if (!File.Exists(from)) continue;
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(from, to);
                }
            }
            catch { }
        }

        public static string Tail(int lines)
        {
            try
            {
                if (!File.Exists(FilePath)) return "";
                var all = File.ReadAllLines(FilePath);
                int start = Math.Max(0, all.Length - Math.Max(1, lines));
                var sb = new StringBuilder();
                for (int i = start; i < all.Length; i++) sb.AppendLine(all[i]);
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }
    }
}

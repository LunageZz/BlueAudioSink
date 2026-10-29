using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SinkLite.Platform
{
    /// <summary>
    /// 极简滚动 JSONL 日志：保留 7 天，单文件上限 8 MB。
    /// 只记录状态、耗时与错误码，不记录音频内容。
    /// </summary>
    internal sealed class Log : IDisposable
    {
        private const long MaxBytes = 8L * 1024 * 1024;
        private const int RetainDays = 7;

        private readonly object sync = new object();
        private readonly string directory;
        private StreamWriter writer;
        private DateTime openedDay;
        private long written;

        public Log(string directory)
        {
            this.directory = directory;
            try
            {
                Directory.CreateDirectory(directory);
                Cleanup();
            }
            catch
            {
            }
        }

        public static string DefaultDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BlueAudioSink",
                    "logs");
            }
        }

        public void Info(string eventName, params (string Key, object Value)[] fields)
        {
            Write("info", eventName, fields);
        }

        public void Warn(string eventName, params (string Key, object Value)[] fields)
        {
            Write("warn", eventName, fields);
        }

        public void Error(string eventName, params (string Key, object Value)[] fields)
        {
            Write("error", eventName, fields);
        }

        private void Write(string level, string eventName, (string Key, object Value)[] fields)
        {
            lock (sync)
            {
                try
                {
                    EnsureWriter();
                    StringBuilder sb = new StringBuilder(256);
                    sb.Append("{\"ts\":\"");
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
                    sb.Append("\",\"level\":\"").Append(level);
                    sb.Append("\",\"event\":\"").Append(Escape(eventName)).Append('"');
                    if (fields != null)
                    {
                        foreach (var f in fields)
                        {
                            sb.Append(",\"").Append(Escape(f.Key)).Append("\":");
                            AppendValue(sb, f.Value);
                        }
                    }
                    sb.Append('}');
                    string line = sb.ToString();
                    writer.WriteLine(line);
                    writer.Flush();
                    written += line.Length + 2;
                }
                catch
                {
                }
            }
        }

        private static void AppendValue(StringBuilder sb, object value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            if (value is bool b)
            {
                sb.Append(b ? "true" : "false");
                return;
            }

            if (value is int i)
            {
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is uint ui)
            {
                sb.Append(ui.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is long l)
            {
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (value is double d)
            {
                sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }

            if (value is float f)
            {
                sb.Append(f.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }

            sb.Append('"').Append(Escape(value.ToString())).Append('"');
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            StringBuilder sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        private void EnsureWriter()
        {
            DateTime today = DateTime.Now.Date;
            bool needNew = writer == null || openedDay != today || written > MaxBytes;
            if (!needNew)
            {
                return;
            }

            if (writer != null)
            {
                writer.Dispose();
                writer = null;
            }

            string path = Path.Combine(directory, "sink-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".jsonl");
            if (written > MaxBytes)
            {
                string rolled = Path.Combine(
                    directory,
                    "sink-" + today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
                    DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".jsonl");
                try
                {
                    if (File.Exists(path))
                    {
                        File.Move(path, rolled);
                    }
                }
                catch
                {
                }
            }

            writer = new StreamWriter(
                new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            openedDay = today;
            written = 0;
        }

        private void Cleanup()
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-RetainDays);
                foreach (string f in Directory.GetFiles(directory, "sink-*.jsonl"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(f) < cutoff)
                        {
                            File.Delete(f);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                try
                {
                    if (writer != null)
                    {
                        writer.Flush();
                        writer.Dispose();
                    }
                }
                catch
                {
                }
                writer = null;
            }
        }
    }
}

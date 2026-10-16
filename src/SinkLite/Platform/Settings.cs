using System;
using System.IO;
using System.Text;

namespace SinkLite.Platform
{
    /// <summary>
    /// 极简配置：ini 风格键值，存放于 %LocalAppData%\BlueAudioSink\settings.ini。
    /// 只保存设备 ID 与开关，不保存任何隐私内容。
    /// </summary>
    internal sealed class Settings
    {
        public string DeviceId { get; set; } = string.Empty;

        public bool AutoConnect { get; set; } = true;

        public int SampleSeconds { get; set; } = 2;

        /// <summary>首选输出端点 ID。非空时程序会把默认输出锁定在它上面。</summary>
        public string PreferredRenderId { get; set; } = string.Empty;

        public static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BlueAudioSink",
                    "settings.ini");
            }
        }

        public static Settings Load()
        {
            Settings settings = new Settings();
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    return settings;
                }

                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                    {
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string value = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "deviceid":
                            settings.DeviceId = value;
                            break;
                        case "autoconnect":
                            settings.AutoConnect = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                            break;
                        case "preferredrenderid":
                            settings.PreferredRenderId = value;
                            break;
                        case "sampleseconds":
                            int seconds;
                            if (int.TryParse(value, out seconds) && seconds >= 1 && seconds <= 30)
                            {
                                settings.SampleSeconds = seconds;
                            }
                            break;
                    }
                }
            }
            catch
            {
            }

            return settings;
        }

        public void Save()
        {
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# BlueAudioSink 设置");
                sb.AppendLine("deviceid=" + (DeviceId ?? string.Empty));
                sb.AppendLine("autoconnect=" + (AutoConnect ? "1" : "0"));
                sb.AppendLine("sampleseconds=" + SampleSeconds);
                sb.AppendLine("preferredrenderid=" + (PreferredRenderId ?? string.Empty));
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }
}

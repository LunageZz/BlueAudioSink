using System;
using Microsoft.Win32;

namespace SinkLite.Platform
{
    /// <summary>开机自启：写 HKCU Run，无需管理员权限。</summary>
    internal static class AutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BlueAudioSink";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    object value = key.GetValue(ValueName);
                    return value != null && !string.IsNullOrEmpty(value.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    if (enabled)
                    {
                        string exe = Environment.ProcessPath;
                        if (string.IsNullOrEmpty(exe))
                        {
                            return false;
                        }
                        // 现在启动即显示主窗口，不再有托盘模式，因此不带参数。
                        key.SetValue(ValueName, "\"" + exe + "\"");
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}

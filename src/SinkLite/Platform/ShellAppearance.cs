using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SinkLite.Platform
{
    internal static class ShellAppearance
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

        public static void Initialize(Log log)
        {
            try
            {
                int result = SetCurrentProcessExplicitAppUserModelID("BlueAudioSink.Desktop");
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                string executable = Environment.ProcessPath;
                using (Stream icon = typeof(ShellAppearance).Assembly.GetManifestResourceStream("app.ico"))
                {
                    if (icon == null || string.IsNullOrEmpty(executable)) return;
                    string revision = Convert.ToHexString(SHA256.HashData(icon));
                    string location = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(executable.ToUpperInvariant()))).Substring(0, 16);
                    string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlueAudioSink");
                    string marker = Path.Combine(folder, "shell-icon-" + location + ".txt");
                    if (File.Exists(marker) && File.ReadAllText(marker) == revision) return;
                    IntPtr path = Marshal.StringToCoTaskMemUni(executable);
                    try { SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero); } // UPDATEITEM, PATHW
                    finally { Marshal.FreeCoTaskMem(path); }
                    SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // Invalidate cached icon associations.
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(marker, revision);
                    log.Info("Ui.ShellIconRefresh", ("revision", revision.Substring(0, 12)));
                }
            }
            catch (Exception ex) { log.Warn("Ui.ShellAppearanceFailed", ("error", ex.Message)); }
        }
    }
}

using System.Reflection;

namespace SinkLite.Platform
{
    // Keep About and startup logs aligned with the build's version metadata.
    internal static class AppInfo
    {
        public const string OriginalRepository = "https://github.com/liubaiqing/BlueAudioSink";
        public const string ForkRepository = "https://github.com/LunageZz/BlueAudioSink";
        public static string Version => typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "未知";
    }
}

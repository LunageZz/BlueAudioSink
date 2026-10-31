using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace SinkLite.Platform
{
    internal static class CableInstaller
    {
        public const string DownloadUrl="https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
        public const string ExpectedSha256="B950E39F01AF1D04EA623C8F6D8EB9B6EA5C477C637295FABF20631C85116BFB";
        public static async Task<string> DownloadAndStart()
        {
            try {
                string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BlueAudioSink","drivers","VB-CABLE-Pack45");
                Directory.CreateDirectory(directory);
                byte[] data;
                using(var client=new HttpClient { Timeout=TimeSpan.FromSeconds(90) }) {
                    using(var response=await client.GetAsync(DownloadUrl,HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false)) {
                        response.EnsureSuccessStatusCode();
                        using(var source=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using(var buffer=new MemoryStream()) {
                            byte[] block=new byte[81920]; int n;
                            while((n=await source.ReadAsync(block,0,block.Length).ConfigureAwait(false))>0) {
                                if(buffer.Length+n>5*1024*1024) throw new InvalidDataException("驱动包大小异常");
                                buffer.Write(block,0,n);
                            }
                            data=buffer.ToArray();
                        }
                    }
                }
                if(Convert.ToHexString(SHA256.HashData(data))!=ExpectedSha256)
                    return "驱动包校验失败，未运行安装器。请从 vb-audio.com/Cable 下载官方版本。";
                string zip=Path.Combine(directory,"VBCABLE_Driver_Pack45.zip");
                await File.WriteAllBytesAsync(zip,data).ConfigureAwait(false);
                ZipFile.ExtractToDirectory(zip,directory,true);
                string installer=Path.Combine(directory,"VBCABLE_Setup_x64.exe");
                using(var process=Process.Start(new ProcessStartInfo(installer) { WorkingDirectory=directory,UseShellExecute=true,Verb="runas" })) { }
                return "VB-CABLE 官方安装器已启动。点击 Install Driver，完成后重启电脑。";
            } catch(System.ComponentModel.Win32Exception ex) when(ex.NativeErrorCode==1223) { return "已取消管理员授权，驱动未安装。"; }
            catch(Exception ex) { return "无法启动驱动安装："+ex.Message; }
        }
    }
}

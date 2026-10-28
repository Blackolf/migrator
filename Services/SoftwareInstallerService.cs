using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SystemMigrator.Services
{
    public class SoftwareInstallerService
    {
        private static readonly HttpClient _httpClient = new HttpClient();

        /// <summary>
        /// 從 URL 下載並靜默安裝
        /// </summary>
        public async Task<bool> InstallFromUrlAsync(string url, string downloadDir, string fileName, string silentArgs, string targetDir, IProgress<double> progress, CancellationToken token)
        {
            string filePath = Path.Combine(downloadDir, fileName);
            
            // 1. 下載檔案
            if (!await DownloadFileAsync(url, filePath, progress, token))
                return false;

            // 2. 執行靜默安裝
            return await RunSilentInstallAsync(filePath, silentArgs, targetDir);
        }

        /// <summary>
        /// 從本地資料夾模糊匹配並靜默安裝 (終極保底)
        /// </summary>
        public async Task<bool> InstallFromLocalAsync(string localDir, string softwareName, string silentArgs, string targetDir)
        {
            if (!Directory.Exists(localDir)) return false;

            // 模糊匹配：尋找檔名包含軟體名稱的 .exe 或 .msi
            var files = Directory.GetFiles(localDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                .Where(f => Path.GetFileNameWithoutExtension(f).Contains(softwareName, StringComparison.OrdinalIgnoreCase));

            string? matchedFile = files.FirstOrDefault();
            if (matchedFile == null) return false;

            return await RunSilentInstallAsync(matchedFile, silentArgs, targetDir);
        }

        /// <summary>
        /// 核心：執行靜默安裝並等待完成
        /// </summary>
        private async Task<bool> RunSilentInstallAsync(string filePath, string silentArgs, string targetDir)
        {
            try
            {
                // 替換參數中的路徑變數
                string finalArgs = silentArgs.Replace("{TargetDir}", $"\"{targetDir}\"");

                var startInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = finalArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Verb = "runas" // 請求管理員權限
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null) return false;
                    
                    // 非同步等待，設定超時 (例如 10 分鐘)
                    var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    await Task.Run(() => process.WaitForExit(), cts.Token);
                    
                    // 某些安裝包退出碼非 0 但也算成功 (如需要重啟)，這裡簡單判斷
                    return process.ExitCode == 0 || process.ExitCode == 3010; 
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"安裝失敗: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 核心：非同步下載檔案並回報進度
        /// </summary>
        private async Task<bool> DownloadFileAsync(string url, string savePath, IProgress<double> progress, CancellationToken token)
        {
            try
            {
                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var canReportProgress = totalBytes != -1;

                    using (var contentStream = await response.Content.ReadAsStreamAsync(token))
                    using (var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var totalRead = 0L;
                        var buffer = new byte[8192];
                        var isMoreToRead = true;

                        do
                        {
                            var read = await contentStream.ReadAsync(buffer, token);
                            if (read == 0) isMoreToRead = false;
                            else
                            {
                                await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                                totalRead += read;
                                if (canReportProgress) progress.Report((double)totalRead / totalBytes * 100);
                            }
                        } while (isMoreToRead);
                    }
                }
                return true;
            }
            catch { return false; }
        }
    }
}
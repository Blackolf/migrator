using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace SystemMigrator.Services // 確保命名空間是這個
{
    public class RestoreEngine
    {
        /// <summary>
        /// 將 ZIP 備份包還原到目標路徑
        /// </summary>
        public async Task<bool> RestoreFromZipAsync(string zipFilePath, string targetPath, IProgress<double> progress, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(zipFilePath)) return false;

                    // 解析環境變數 (如 %APPDATA% -> C:\Users\xxx\AppData\Roaming)
                    string resolvedTarget = Environment.ExpandEnvironmentVariables(targetPath);
                    
                    // 確保目標目錄存在
                    if (!Directory.Exists(resolvedTarget))
                        Directory.CreateDirectory(resolvedTarget);

                    // 統計總文件數以計算進度
                    using (var archive = ZipFile.OpenRead(zipFilePath))
                    {
                        long totalEntries = archive.Entries.Count;
                        long processedEntries = 0;

                        foreach (var entry in archive.Entries)
                        {
                            if (token.IsCancellationRequested) return false;

                            // 跳過目錄節點
                            if (string.IsNullOrEmpty(entry.Name)) 
                            {
                                processedEntries++;
                                continue;
                            }

                            string destinationPath = Path.GetFullPath(Path.Combine(resolvedTarget, entry.FullName));
                            
                            // 安全檢查：防止 Zip Slip 漏洞 (路徑穿越)
                            if (!destinationPath.StartsWith(resolvedTarget))
                                throw new Exception("嘗試解壓到目標目錄之外！");

                            // 確保子目錄存在
                            string? dirPath = Path.GetDirectoryName(destinationPath);
                            if (dirPath != null && !Directory.Exists(dirPath))
                                Directory.CreateDirectory(dirPath);

                            // 解壓並覆蓋
                            entry.ExtractToFile(destinationPath, overwrite: true);

                            processedEntries++;
                            progress.Report((double)processedEntries / totalEntries * 100);
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"還原失敗: {ex.Message}");
                    return false;
                }
            }, token);
        }
    }
}
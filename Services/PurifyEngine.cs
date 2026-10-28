using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SystemMigrator.Services
{
    public class PurifyEngine
    {
        // 全局黑名單：絕對不備份的垃圾文件夾和後綴
        private readonly HashSet<string> _blacklistDirs = new(StringComparer.OrdinalIgnoreCase) 
        { 
            "Cache", "GPUCache", "Code Cache", "Temp", "Logs", "CrashDumps", 
            "node_modules", ".git", "__pycache__", "bin", "obj", "shadercache" 
        };
        
        private readonly HashSet<string> _blacklistExts = new(StringComparer.OrdinalIgnoreCase) 
        { 
            ".tmp", ".log", ".bak", ".dmp", ".cache" 
        };

        // 將目錄打包為 ZIP，並應用淨化規則
        public async Task<bool> BackupWithPurifyAsync(string sourceDir, string zipFilePath, IProgress<double> progress, CancellationToken token)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string resolvedSource = Environment.ExpandEnvironmentVariables(sourceDir);
                    if (!Directory.Exists(resolvedSource)) return false;

                    if (File.Exists(zipFilePath)) File.Delete(zipFilePath);

                    // 先統計總文件數，用於計算進度
                    var allFiles = Directory.EnumerateFiles(resolvedSource, "*", SearchOption.AllDirectories)
                                            .Where(f => !IsBlacklisted(f)).ToList();
                    long totalFiles = allFiles.Count;
                    long processedFiles = 0;

                    using (var archive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
                    {
                        foreach (var file in allFiles)
                        {
                            if (token.IsCancellationRequested) return false;

                            // 計算相對路徑，保持目錄結構
                            string relativePath = Path.GetRelativePath(resolvedSource, file);
                            archive.CreateEntryFromFile(file, relativePath, CompressionLevel.Optimal);

                            processedFiles++;
                            if (totalFiles > 0)
                                progress.Report((double)processedFiles / totalFiles * 100);
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"淨化備份失敗: {ex.Message}");
                    return false;
                }
            }, token);
        }

        // 判斷文件是否命中黑名單
        private bool IsBlacklisted(string filePath)
        {
            // 檢查擴展名
            if (_blacklistExts.Contains(Path.GetExtension(filePath))) return true;

            // 檢查路徑中是否包含黑名單目錄
            string[] parts = filePath.Split(Path.DirectorySeparatorChar);
            foreach (var part in parts)
            {
                if (_blacklistDirs.Contains(part)) return true;
            }
            return false;
        }
    }
}
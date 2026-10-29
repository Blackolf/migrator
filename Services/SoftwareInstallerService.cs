using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SystemMigrator.Models;

namespace SystemMigrator.Services
{
    /// <summary>
    /// 軟體安裝管線（三級降級）：
    /// 第 1 級：Winget（有已知 ID 就用 --id，否則用 --name 精確比對）
    /// 第 2 級：依鏡像模式解析候選 URL → 測速／依序回退下載 → 靜默安裝
    /// 第 3 級：本地安裝包目錄模糊匹配 → 靜默安裝
    /// 每一級的結果都寫進 MigrationTask 的獨立日誌。
    /// </summary>
    public sealed class SoftwareInstallerService
    {
        private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromMinutes(30) };

        private readonly MirrorService _mirrors;

        public SoftwareInstallerService(MirrorService mirrors)
        {
            _mirrors = mirrors;
        }

        // ==================== 三級降級主流程 ====================

        /// <summary>
        /// 對一個任務執行三級降級安裝。回傳 true 表示已成功安裝。
        /// </summary>
        public async Task<bool> InstallWithFallbackAsync(MigrationTask task, string downloadDirectory, CancellationToken token)
        {
            Directory.CreateDirectory(downloadDirectory);

            // ---------- 第 1 級：Winget ----------
            task.AddLog("【第 1 級】Winget 安裝", LogLevel.Info);
            if (await TryWingetAsync(task, token))
            {
                task.AddLog("Winget 安裝成功。", LogLevel.Success);
                return true;
            }

            // ---------- 第 2 級：鏡像／官方下載 ----------
            MirrorPackage? package = _mirrors.FindPackage(task.WingetId) ?? _mirrors.FindPackage(task.Name);
            if (package == null)
            {
                task.AddLog($"【第 2 級】鏡像庫中沒有「{task.Name}」的安裝規則，略過。", LogLevel.Info);
            }
            else
            {
                task.AddLog($"【第 2 級】下載安裝（規則：{package.DisplayName}，模式：{_mirrors.ModeDisplay}）", LogLevel.Info);
                if (await TryDownloadAndInstallAsync(task, package, downloadDirectory, token))
                {
                    task.AddLog("鏡像／官方下載安裝成功。", LogLevel.Success);
                    return true;
                }
            }

            // ---------- 第 3 級：本地安裝包 ----------
            string localDirectory = string.IsNullOrWhiteSpace(task.LocalInstallerDir)
                ? Path.Combine(AppContext.BaseDirectory, "Installers")
                : task.LocalInstallerDir;

            task.AddLog($"【第 3 級】本地安裝包目錄：{localDirectory}", LogLevel.Info);
            if (await InstallFromLocalAsync(localDirectory, task.Name, task.SilentArgs, task.TargetInstallDir))
            {
                task.AddLog("本地安裝包安裝成功。", LogLevel.Success);
                return true;
            }

            task.AddLog("三個層級皆未成功安裝此軟體。", LogLevel.Error);
            return false;
        }

        // ==================== 第 1 級：Winget ====================

        private async Task<bool> TryWingetAsync(MigrationTask task, CancellationToken token)
        {
            string? winget = ResolveWingetPath();
            if (string.IsNullOrWhiteSpace(winget))
            {
                task.AddLog("系統找不到 winget.exe，略過此層級。", LogLevel.Error);
                return false;
            }

            string arguments = string.IsNullOrWhiteSpace(task.WingetId)
                ? $"install --name \"{task.Name}\" --exact --silent --accept-package-agreements --accept-source-agreements"
                : $"install --id \"{task.WingetId}\" --exact --silent --accept-package-agreements --accept-source-agreements";

            task.AddLog($"執行：winget {arguments}", LogLevel.Info);

            try
            {
                var startInfo = new ProcessStartInfo(winget, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) task.AddLog(e.Data!, LogLevel.Info);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) task.AddLog(e.Data!, LogLevel.Error);
                };

                if (!process.Start()) return false;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync(token);

                if (process.ExitCode == 0) return true;

                // winget 對「已安裝」與「找不到套件」用不同代碼，這裡只做日誌提示。
                task.AddLog($"Winget 結束，退出碼 {process.ExitCode}。", LogLevel.Error);
                return false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                task.AddLog($"Winget 執行失敗：{ex.Message}", LogLevel.Error);
                return false;
            }
        }

        private static string? ResolveWingetPath()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string candidate = Path.Combine(local, "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(candidate) ? candidate : "winget";
        }

        // ==================== 第 2 級：鏡像下載 ====================

        private async Task<bool> TryDownloadAndInstallAsync(
            MigrationTask task,
            MirrorPackage package,
            string downloadDirectory,
            CancellationToken token)
        {
            var candidates = _mirrors.Resolve(package).ToList();
            if (candidates.Count == 0)
            {
                task.AddLog("沒有任何可用的下載來源。", LogLevel.Error);
                return false;
            }

            // 自動模式先測速，把最快來源排到最前面，其餘作為回退。
            if (_mirrors.Mode == MirrorMode.Auto && candidates.Count > 1)
            {
                MirrorCandidate? fastest = await _mirrors.PickFastestAsync(package, token);
                if (fastest != null)
                {
                    candidates = new[] { fastest }
                        .Concat(candidates.Where(c => !string.Equals(c.Url, fastest.Url, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }
            }

            string fileName = string.IsNullOrWhiteSpace(package.FileName)
                ? $"{SanitizeFileName(package.Key)}.exe"
                : package.FileName;
            string savePath = Path.Combine(downloadDirectory, fileName);

            foreach (MirrorCandidate candidate in candidates)
            {
                token.ThrowIfCancellationRequested();

                task.AddLog($"嘗試來源：{candidate.Source.Name}", LogLevel.Info);
                task.AddLog($"URL：{candidate.Url}", LogLevel.Info);

                var progress = new Progress<double>(percent =>
                {
                    task.Progress = percent;
                    task.StatusText = $"下載中 {percent:F0}%（{candidate.Source.Name}）";
                });

                if (!await DownloadOnceAsync(candidate.Url, savePath, progress, token))
                {
                    task.AddLog($"來源 {candidate.Source.Name} 下載失敗，切換下一個來源。", LogLevel.Error);
                    continue;
                }

                if (!VerifySha256(savePath, package.Sha256, task)) continue;

                string silentArgs = string.IsNullOrWhiteSpace(package.SilentArgs) ? task.SilentArgs : package.SilentArgs;
                if (await RunSilentInstallAsync(savePath, silentArgs, task.TargetInstallDir, task, token))
                {
                    return true;
                }

                task.AddLog("安裝程式回報失敗，嘗試下一個來源。", LogLevel.Error);
            }

            return false;
        }

        /// <summary>
        /// 單次下載（帶進度）。失敗時刪除半成品並回傳 false。
        /// </summary>
        private async Task<bool> DownloadOnceAsync(string url, string savePath, IProgress<double> progress, CancellationToken token)
        {
            string? directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            try
            {
                using HttpResponseMessage response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1L;
                bool canReport = totalBytes > 0;

                using Stream contentStream = await response.Content.ReadAsStreamAsync(token);
                using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                long totalRead = 0;
                var buffer = new byte[81920];
                int read;

                while ((read = await contentStream.ReadAsync(buffer, token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
                    totalRead += read;
                    if (canReport) progress.Report((double)totalRead / totalBytes * 100);
                }

                progress.Report(100);
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDelete(savePath);
                throw;
            }
            catch
            {
                TryDelete(savePath);
                return false;
            }
        }

        private static bool VerifySha256(string filePath, string? expected, MigrationTask task)
        {
            if (string.IsNullOrWhiteSpace(expected)) return true;

            try
            {
                using FileStream stream = File.OpenRead(filePath);
                string actual = Convert.ToHexString(SHA256.HashData(stream));
                if (string.Equals(actual, expected.Trim(), StringComparison.OrdinalIgnoreCase)) return true;

                task.AddLog($"SHA256 不符，已刪除檔案。預期 {expected}，實際 {actual}", LogLevel.Error);
                TryDelete(filePath);
                return false;
            }
            catch (Exception ex)
            {
                task.AddLog($"SHA256 校驗失敗：{ex.Message}", LogLevel.Error);
                return false;
            }
        }

        // ==================== 第 3 級：本地安裝包 ====================

        /// <summary>
        /// 從本地資料夾模糊匹配並靜默安裝（終極保底）。
        /// </summary>
        public async Task<bool> InstallFromLocalAsync(string localDirectory, string softwareName, string silentArgs, string targetDirectory)
        {
            if (!Directory.Exists(localDirectory)) return false;

            try
            {
                string? matched = Directory
                    .EnumerateFiles(localDirectory, "*.*", SearchOption.AllDirectories)
                    .Where(IsInstallerFile)
                    .FirstOrDefault(file =>
                        Path.GetFileNameWithoutExtension(file).Contains(softwareName, StringComparison.OrdinalIgnoreCase));

                if (matched == null) return false;

                return await RunSilentInstallAsync(matched, silentArgs, targetDirectory, task: null, CancellationToken.None);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInstallerFile(string path) =>
            path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 從 URL 下載並靜默安裝（保留舊 API，供單檔安裝使用）。
        /// </summary>
        public async Task<bool> InstallFromUrlAsync(string url, string downloadDirectory, string fileName, string silentArgs, string targetDirectory, IProgress<double> progress, CancellationToken token)
        {
            string filePath = Path.Combine(downloadDirectory, fileName);
            if (!await DownloadOnceAsync(url, filePath, progress, token)) return false;
            return await RunSilentInstallAsync(filePath, silentArgs, targetDirectory, task: null, token);
        }

        // ==================== 靜默安裝 ====================

        /// <summary>
        /// 執行靜默安裝並等待完成（.msi 走 msiexec，.exe 直接執行）。
        /// </summary>
        private async Task<bool> RunSilentInstallAsync(
            string filePath,
            string silentArgs,
            string targetDirectory,
            MigrationTask? task,
            CancellationToken token)
        {
            if (!File.Exists(filePath))
            {
                task?.AddLog($"找不到安裝檔：{filePath}", LogLevel.Error);
                return false;
            }

            string finalArgs = (silentArgs ?? "").Replace("{TargetDir}", $"\"{targetDirectory}\"");

            var startInfo = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            if (filePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = "msiexec.exe";
                startInfo.Arguments = $"/i \"{filePath}\" /qn /norestart {finalArgs}".Trim();
            }
            else if (filePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = filePath;
                startInfo.Arguments = finalArgs;
            }
            else
            {
                task?.AddLog($"不支援的安裝檔類型：{Path.GetExtension(filePath)}", LogLevel.Error);
                return false;
            }

            task?.AddLog($"開始靜默安裝：{Path.GetFileName(filePath)} {startInfo.Arguments}".TrimEnd(), LogLevel.Info);

            try
            {
                using var process = new Process { StartInfo = startInfo };
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) task?.AddLog(e.Data!, LogLevel.Info);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data)) task?.AddLog(e.Data!, LogLevel.Error);
                };

                if (!process.Start()) return false;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // 安裝程式最長等 10 分鐘；超時視為失敗（避免任務永遠卡住）。
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));

                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // 已經結束就忽略。
                    }

                    throw;
                }
                catch (OperationCanceledException)
                {
                    task?.AddLog("安裝逾時（超過 10 分鐘），已中止。", LogLevel.Error);
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // 已經結束就忽略。
                    }

                    return false;
                }

                // 0 = 成功；3010 = 成功但需要重新啟動。
                bool ok = process.ExitCode is 0 or 3010;
                task?.AddLog($"安裝程式退出碼：{process.ExitCode}", ok ? LogLevel.Success : LogLevel.Error);
                return ok;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                task?.AddLog($"安裝失敗：{ex.Message}", LogLevel.Error);
                return false;
            }
        }

        // ==================== 工具 ====================

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "installer";
            string safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
            return string.IsNullOrWhiteSpace(safe) ? "installer" : safe;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 刪不掉半成品不影響流程。
            }
        }
    }
}

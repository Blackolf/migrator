using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SystemMigrator.Models;

namespace SystemMigrator.Services
{
    /// <summary>
    /// 分區映像引擎：
    /// 1. 用 DISM 把分區擷取成 WIM（完整映像，可還原到其他機器）；
    /// 2. 用 DISM 把 WIM 套用到目標分區（分區安裝）；
    /// 3. 用 Robocopy 做檔案級複製（適合純資料分區）；
    /// 4. 用 bcdboot 重建 UEFI 引導。
    /// 所有子程序輸出會即時回報百分比與日誌。
    /// </summary>
    public sealed class DiskImageService
    {
        private static readonly Regex PercentPattern = new(@"(\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled);
        private static readonly Regex IndexPattern = new(@"^\s*Index\s*:\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NamePattern = new(@"^\s*Name\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SizePattern = new(@"^\s*Size\s*:\s*([\d,]+)\s*bytes", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>日誌事件。</summary>
        public event Action<string, LogLevel>? Log;

        /// <summary>進度事件（0-100）。</summary>
        public event Action<double>? Progress;

        /// <summary>DISM 是否可用（Windows 內建）。</summary>
        public static bool IsDismAvailable =>
            File.Exists(Path.Combine(Environment.SystemDirectory, "dism.exe"));

        // ==================== 備份 ====================

        /// <summary>
        /// 把分區擷取成 WIM 映像。
        /// </summary>
        public async Task<bool> CaptureWimAsync(
            PartitionInfo source,
            string imagePath,
            string compression,
            bool checkIntegrity,
            CancellationToken token)
        {
            if (!IsDismAvailable)
            {
                Report("找不到 dism.exe，無法建立 WIM 映像。", LogLevel.Error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(source.DriveLetter))
            {
                Report($"{source.DisplayLetter} 沒有掛載磁碟機代號，無法擷取。", LogLevel.Error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(imagePath))
            {
                Report("尚未指定映像檔路徑。", LogLevel.Error);
                return false;
            }

            string? directory = Path.GetDirectoryName(imagePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                Report("映像檔路徑無效。", LogLevel.Error);
                return false;
            }

            Directory.CreateDirectory(directory);

            // 注意：DISM 對已存在的 WIM 是「附加映像」而不是覆寫，所以先刪除。
            if (File.Exists(imagePath))
            {
                Report($"映像檔已存在，先刪除舊檔：{imagePath}", LogLevel.Info);
                File.Delete(imagePath);
            }

            string captureDir = $"{source.DriveLetter}:\\";
            string imageName = Sanitize($"SystemMigrator {DateTime.Now:yyyy-MM-dd HH:mm} {source.DriveLetter}");
            string compress = NormalizeCompression(compression);

            string arguments =
                "/English /Capture-Image " +
                $"/ImageFile:\"{imagePath}\" " +
                $"/CaptureDir:\"{captureDir}\" " +
                $"/Name:\"{imageName}\" " +
                $"/Description:\"SystemMigrator V1.0.3\" " +
                $"/Compress:{compress}" +
                (checkIntegrity ? " /CheckIntegrity" : "");

            Report($"開始擷取 {captureDir} → {Path.GetFileName(imagePath)}（壓縮：{compress}）", LogLevel.Info);

            int exitCode = await RunAsync("dism.exe", arguments, HandlePercentLine, token);
            bool ok = exitCode == 0;

            Report(
                ok ? $"WIM 映像建立完成：{imagePath}" : $"DISM 擷取失敗（退出碼 {exitCode}）。",
                ok ? LogLevel.Success : LogLevel.Error);

            return ok;
        }

        /// <summary>
        /// 用 Robocopy 把分區內容檔案級複製到目標資料夾（不做壓縮，可中斷續傳）。
        /// </summary>
        public async Task<bool> CaptureFolderAsync(
            PartitionInfo source,
            string targetDirectory,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(source.DriveLetter))
            {
                Report($"{source.DisplayLetter} 沒有掛載磁碟機代號，無法複製。", LogLevel.Error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                Report("尚未指定目標資料夾。", LogLevel.Error);
                return false;
            }

            Directory.CreateDirectory(targetDirectory);

            string arguments =
                $"\"{source.DriveLetter}:\\\\\" \"{targetDirectory}\" " +
                "/E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /XJ /NFL /NDL /NJH /NJS " +
                "/XD \"System Volume Information\" \"$Recycle.Bin\" \"Windows\\WinSxS\\Temp\"";

            Report($"開始檔案級複製 {source.DriveLetter}: → {targetDirectory}", LogLevel.Info);

            int exitCode = await RunAsync("robocopy.exe", arguments, HandlePercentLine, token);

            // Robocopy 0-7 都算成功，8 以上才是錯誤。
            bool ok = exitCode is >= 0 and < 8;

            Report(
                ok ? $"檔案級複製完成（Robocopy 退出碼 {exitCode}）。" : $"檔案級複製失敗（退出碼 {exitCode}）。",
                ok ? LogLevel.Success : LogLevel.Error);

            return ok;
        }

        // ==================== 安裝 / 還原 ====================

        /// <summary>
        /// 把 WIM 映像套用到目標分區（分區安裝）。
        /// </summary>
        public async Task<bool> ApplyWimAsync(
            string imagePath,
            int imageIndex,
            PartitionInfo target,
            bool formatFirst,
            CancellationToken token)
        {
            if (!IsDismAvailable)
            {
                Report("找不到 dism.exe，無法套用映像。", LogLevel.Error);
                return false;
            }

            if (!File.Exists(imagePath))
            {
                Report($"映像檔不存在：{imagePath}", LogLevel.Error);
                return false;
            }

            if (string.IsNullOrWhiteSpace(target.DriveLetter))
            {
                Report($"{target.DisplayLetter} 沒有掛載磁碟機代號，無法作為安裝目標。", LogLevel.Error);
                return false;
            }

            if (target.IsSystemPartition)
            {
                Report("目標是「目前正在執行」的系統分區，禁止覆蓋（請從 WinPE 執行）。", LogLevel.Error);
                return false;
            }

            if (target.Kind is PartitionKind.Efi or PartitionKind.Msr)
            {
                Report($"目標分區是 {target.KindText}，不能作為安裝目標。", LogLevel.Error);
                return false;
            }

            if (imageIndex < 1) imageIndex = 1;

            if (formatFirst)
            {
                Report($"格式化目標分區 {target.DisplayLetter}（NTFS 快速格式化）...", LogLevel.Info);
                string formatArgs = $"{target.DriveLetter}: /FS:NTFS /Q /Y /V:SM";
                int formatCode = await RunAsync("format.com", formatArgs, _ => { }, token);
                if (formatCode != 0)
                {
                    Report($"格式化失敗（退出碼 {formatCode}），已中止。", LogLevel.Error);
                    return false;
                }
            }

            string arguments =
                "/English /Apply-Image " +
                $"/ImageFile:\"{imagePath}\" " +
                $"/Index:{imageIndex} " +
                $"/ApplyDir:\"{target.RootPath}\"";

            Report($"開始套用映像到 {target.RootPath}（索引 {imageIndex}）...", LogLevel.Info);

            int exitCode = await RunAsync("dism.exe", arguments, HandlePercentLine, token);
            bool ok = exitCode == 0;

            Report(
                ok ? $"映像已套用到 {target.RootPath}" : $"DISM 套用失敗（退出碼 {exitCode}）。",
                ok ? LogLevel.Success : LogLevel.Error);

            return ok;
        }

        /// <summary>
        /// 用 bcdboot 重建 UEFI 引導。espDriveLetter 可空，空則用 mountvol 自動掛載 ESP。
        /// </summary>
        public async Task<bool> RepairBootAsync(
            PartitionInfo windowsPartition,
            string? espDriveLetter,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(windowsPartition.DriveLetter))
            {
                Report("未指定 Windows 所在分區。", LogLevel.Error);
                return false;
            }

            string windowsDirectory = Path.Combine(windowsPartition.RootPath, "Windows");
            if (!Directory.Exists(windowsDirectory))
            {
                Report($"目標分區找不到 Windows 資料夾：{windowsDirectory}", LogLevel.Error);
                return false;
            }

            string? mountedEsp = null;

            try
            {
                string? esp = (espDriveLetter ?? "").Trim().TrimEnd(':');
                if (string.IsNullOrEmpty(esp))
                {
                    esp = FindFreeDriveLetter();
                    if (string.IsNullOrEmpty(esp))
                    {
                        Report("找不到可用的磁碟機代號來掛載 ESP。", LogLevel.Error);
                        return false;
                    }

                    Report($"自動掛載 EFI 系統分割到 {esp}: ...", LogLevel.Info);
                    int mountCode = await RunAsync("mountvol.exe", $"{esp}: /S", _ => { }, token);
                    if (mountCode != 0)
                    {
                        Report($"掛載 ESP 失敗（退出碼 {mountCode}）。", LogLevel.Error);
                        return false;
                    }

                    mountedEsp = esp;
                }

                Report($"執行 bcdboot \"{windowsDirectory}\" /s {esp}: /f UEFI", LogLevel.Info);
                int exitCode = await RunAsync(
                    "bcdboot.exe",
                    $"\"{windowsDirectory}\" /s {esp}: /f UEFI",
                    _ => { },
                    token);

                bool ok = exitCode == 0;
                Report(
                    ok ? "UEFI 引導重建完成。" : $"bcdboot 失敗（退出碼 {exitCode}）。",
                    ok ? LogLevel.Success : LogLevel.Error);

                return ok;
            }
            finally
            {
                if (mountedEsp != null)
                {
                    Report($"卸載 ESP（{mountedEsp}:）...", LogLevel.Info);
                    await RunAsync("mountvol.exe", $"{mountedEsp}: /D", _ => { }, CancellationToken.None);
                }
            }
        }

        // ==================== 映像資訊 ====================

        /// <summary>
        /// 讀取 WIM 內的映像索引清單（Index / Name / Size）。
        /// </summary>
        public async Task<IReadOnlyList<WimImageInfo>> GetWimInfoAsync(string imagePath, CancellationToken token)
        {
            var images = new List<WimImageInfo>();

            if (!File.Exists(imagePath))
            {
                Report($"映像檔不存在：{imagePath}", LogLevel.Error);
                return images;
            }

            WimImageInfo? current = null;

            void ParseLine(string line)
            {
                Match index = IndexPattern.Match(line);
                if (index.Success)
                {
                    current = new WimImageInfo { Index = int.Parse(index.Groups[1].Value) };
                    images.Add(current);
                    return;
                }

                if (current == null) return;

                Match name = NamePattern.Match(line);
                if (name.Success && string.IsNullOrEmpty(current.Name))
                {
                    current.Name = name.Groups[1].Value.Trim();
                    return;
                }

                Match size = SizePattern.Match(line);
                if (size.Success)
                {
                    long.TryParse(size.Groups[1].Value.Replace(",", ""), out long bytes);
                    current.SizeBytes = bytes;
                }
            }

            int exitCode = await RunAsync(
                "dism.exe",
                $"/English /Get-WimInfo /WimFile:\"{imagePath}\"",
                ParseLine,
                token);

            if (exitCode != 0)
            {
                Report($"讀取映像資訊失敗（退出碼 {exitCode}）。", LogLevel.Error);
                return images;
            }

            foreach (WimImageInfo image in images)
            {
                Report($"映像 #{image.Index}：{image.Name}（{image.SizeText}）", LogLevel.Info);
            }

            return images;
        }

        // ==================== 進程執行 ====================

        private async Task<int> RunAsync(
            string fileName,
            string arguments,
            Action<string> onLine,
            CancellationToken token)
        {
            var startInfo = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.SystemDirectory
            };

            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data!);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) onLine(e.Data!);
            };

            if (!process.Start())
            {
                Report($"無法啟動 {fileName}。", LogLevel.Error);
                return -1;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 進程已結束。
                }
            });

            await process.WaitForExitAsync(CancellationToken.None);
            token.ThrowIfCancellationRequested();

            return process.ExitCode;
        }

        private void HandlePercentLine(string line)
        {
            Match match = PercentPattern.Match(line);
            if (!match.Success) return;

            if (double.TryParse(match.Groups[1].Value, out double percent))
            {
                Progress?.Invoke(Math.Clamp(percent, 0, 100));
            }
        }

        // ==================== 工具 ====================

        private static string NormalizeCompression(string? compression) => compression?.Trim().ToLowerInvariant() switch
        {
            "fast" => "fast",
            "none" => "none",
            _ => "max"
        };

        private static string Sanitize(string name) =>
            string.Join("_", name.Split(Path.GetInvalidFileNameChars()));

        private static string? FindFreeDriveLetter()
        {
            var used = new HashSet<char>(
                DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));

            for (char letter = 'Z'; letter >= 'D'; letter--)
            {
                if (!used.Contains(letter)) return letter.ToString();
            }

            return null;
        }

        private void Report(string message, LogLevel level) => Log?.Invoke(message, level);
    }
}

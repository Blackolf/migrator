using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemMigrator.Models;

namespace SystemMigrator.Services
{
    public class TaskSchedulerService
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly Queue<MigrationTask> _queue = new();
        private readonly SoftwareInstallerService? _installer;
        private readonly string _downloadDirectory;
        private bool _isProcessing = false;

        public event Action<double>? OverallProgressChanged;
        public event Action<string>? OverallStatusChanged;

        // 綁定到 UI 的活躍任務列表
        public ObservableCollection<MigrationTask> ActiveTasks { get; } = new();

        public TaskSchedulerService(
            int maxConcurrency = 3,
            SoftwareInstallerService? installer = null,
            string? downloadDirectory = null)
        {
            _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            _installer = installer;
            _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "SystemMigrator", "Downloads");
        }

        // 將任務加入隊列
        public void EnqueueTask(MigrationTask task)
        {
            _queue.Enqueue(task);
            ActiveTasks.Add(task);
            ProcessQueue();
        }

        // 調度引擎核心：嘗試啟動下一個任務
        private async void ProcessQueue()
        {
            if (_isProcessing) return;
            _isProcessing = true;

            while (_queue.Count > 0)
            {
                // 等待信號量 (如果已達到最大並發數，這裡會阻塞等待)
                await _semaphore.WaitAsync();

                if (_queue.TryDequeue(out var task))
                {
                    // 啟動任務 (不等待其完成，實現並行)
                    _ = ExecuteTaskAsync(task);
                }
                else
                {
                    _semaphore.Release(); // 隊列空了，釋放信號量
                }
            }
            _isProcessing = false;
        }

        // 執行單個任務
        private async Task ExecuteTaskAsync(MigrationTask task)
        {
            var cts = new CancellationTokenSource();

            try
            {
                // 記錄開始時間與日誌
                task.StartTime = DateTime.Now;
                task.AddLog("任務開始調度...", LogLevel.Info);

                // ================= 階段 1：軟體安裝（三級降級） =================
                if (task.IsInstallTask)
                {
                    task.Status = Models.TaskStatus.Installing;
                    task.StatusText = "正在安裝軟體（Winget → 鏡像 → 本地）...";
                    task.AddLog($"開始安裝：{task.Name}", LogLevel.Info);

                    if (_installer == null)
                    {
                        throw new Exception("未設定軟體安裝服務。");
                    }

                    bool installed = await _installer.InstallWithFallbackAsync(task, _downloadDirectory, cts.Token);
                    if (!installed)
                    {
                        throw new Exception("軟體安裝失敗（三級降級皆未成功）。");
                    }
                }

                // ================= 階段 2：數據遷移 =================
                if (task.SourcePaths.Any() && !string.IsNullOrEmpty(task.BackupZipPath))
                {
                    task.Status = Models.TaskStatus.Migrating;
                    task.StatusText = task.IsBackupMode ? "正在淨化打包..." : "正在還原數據...";
                    task.AddLog($"開始數據遷移: {(task.IsBackupMode ? "備份" : "還原")}", LogLevel.Info);

                    var purify = new PurifyEngine();
                    var restore = new RestoreEngine();
                    var progressReporter = new Progress<double>(p => task.Progress = p);

                    bool success = task.IsBackupMode
                        ? await purify.BackupWithPurifyAsync(task.SourcePaths[0], task.BackupZipPath, progressReporter, cts.Token)
                        : await restore.RestoreFromZipAsync(task.BackupZipPath, task.SourcePaths[0], progressReporter, cts.Token);

                    if (!success) throw new Exception("數據遷移失敗。");
                    task.AddLog("數據遷移完成。", LogLevel.Success);
                }

                task.Status = Models.TaskStatus.Completed;
                task.StatusText = "任務完美完成！";
                task.Progress = 100;
                task.AddLog("所有步驟執行成功。", LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                task.Status = Models.TaskStatus.Cancelled;
                task.StatusText = "任務已取消。";
                task.AddLog("任務被用戶取消。", LogLevel.Error);
            }
            catch (Exception ex)
            {
                task.Status = Models.TaskStatus.Failed;
                task.StatusText = $"失敗: {ex.Message}";
                task.AddLog($"發生錯誤: {ex.Message}", LogLevel.Error);
            }
            finally
            {
                // 記錄結束時間
                task.EndTime = DateTime.Now;
                _semaphore.Release();
                cts.Dispose();

                // 更新總體進度
                int total = ActiveTasks.Count;
                int completed = ActiveTasks.Count(t => t.Status == Models.TaskStatus.Completed
                                                       || t.Status == Models.TaskStatus.Failed
                                                       || t.Status == Models.TaskStatus.Cancelled);
                double overallPct = total > 0 ? (double)completed / total * 100 : 0;
                OverallProgressChanged?.Invoke(overallPct);
                OverallStatusChanged?.Invoke(completed == total && total > 0 ? "所有任務已完成！" : $"執行中... ({completed}/{total})");
            }
        }
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using SystemMigrator.Models;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Linq;

namespace SystemMigrator.Services
{
    public class TaskSchedulerService
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly Queue<MigrationTask> _queue = new();
        private bool _isProcessing = false;

        // 綁定到 UI 的活躍任務列表
        public ObservableCollection<MigrationTask> ActiveTasks { get; } = new();

        // 設定最大並行數 (例如 3 個任務同時執行)
        public TaskSchedulerService(int maxConcurrency = 3)
        {
            _semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
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

        // 執行單個任務 (這裡用模擬邏輯代替真實的安裝/遷移)
        private async Task ExecuteTaskAsync(MigrationTask task)
        {
            var cts = new CancellationTokenSource();
            try
            {
                // ================= 階段 1：Winget 安裝 =================
                if (!string.IsNullOrEmpty(task.WingetId))
                {
                    task.Status = Models.TaskStatus.Installing;
                    task.StatusText = $"正在透過 Winget 安裝 {task.Name}...";
                    
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "winget",
                        Arguments = $"install --id {task.WingetId} --silent --accept-package-agreements --accept-source-agreements",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process != null)
                        {
                            // 非同步讀取輸出，嘗試解析進度 (Winget 輸出格式如: 已下載 50%)
                            process.OutputDataReceived += (sender, e) =>
                            {
                                if (!string.IsNullOrEmpty(e.Data))
                                {
                                    var match = Regex.Match(e.Data, @"(\d+)%");
                                    if (match.Success && double.TryParse(match.Groups[1].Value, out double pct))
                                    {
                                        task.Progress = pct;
                                    }
                                }
                            };
                            process.BeginOutputReadLine();
                            
                            await Task.Run(() => process.WaitForExit(), cts.Token);
                            
                            if (process.ExitCode != 0)
                            {
                                throw new Exception($"Winget 安裝失敗，退出碼: {process.ExitCode}");
                            }
                        }
                    }
                    task.Progress = 100;
                }

                // ================= 階段 2：數據淨化遷移 =================
                if (task.SourcePaths.Any() && !string.IsNullOrEmpty(task.BackupZipPath))
                {
                    task.Status = Models.TaskStatus.Migrating;
                    task.StatusText = "正在淨化並打包配置/存檔...";
                    task.Progress = 0;

                    var purifyEngine = new PurifyEngine();
                    var progressReporter = new Progress<double>(p => task.Progress = p);

                    // 這裡以第一個源路徑為例，實際可循環處理多個
                    bool success = await purifyEngine.BackupWithPurifyAsync(
                        task.SourcePaths[0], 
                        task.BackupZipPath, 
                        progressReporter, 
                        cts.Token
                    );

                    if (!success) throw new Exception("數據打包失敗或路徑不存在。");
                    task.Progress = 100;
                }

                // ================= 完成 =================
                task.Status = Models.TaskStatus.Completed;
                task.StatusText = "任務完美完成！";
            }
            catch (OperationCanceledException)
            {
                task.Status = Models.TaskStatus.Cancelled;
                task.StatusText = "任務已取消。";
            }
            catch (Exception ex)
            {
                task.Status = Models.TaskStatus.Failed;
                task.StatusText = $"失敗: {ex.Message}";
            }
            finally
            {
                _semaphore.Release();
                cts.Dispose();
            }
        }

        // 輔助方法：模擬進度條增長
        private async Task SimulateProgress(MigrationTask task, double start, double end, int delayMs)
        {
            double step = (end - start) / 10;
            for (int i = 0; i <= 10; i++)
            {
                if (task.Status == Models.TaskStatus.Cancelled) return;
                task.Progress = start + (step * i);
                await Task.Delay(delayMs / 10);
            }
            task.Progress = end;
        }
    }
}
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
        public event Action<double>? OverallProgressChanged;
        public event Action<string>? OverallStatusChanged;

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
            var logger = LoggerService.Instance; // 獲取日誌實例

            try
            {
                logger.Log($"[{task.Name}] 開始執行任務...", LogLevel.Info);
                
                // ... (保留原有的安裝與遷移邏輯) ...
                // 在安裝成功或遷移成功的地方，可以加入：
                // logger.Log($"[{task.Name}] 安裝/遷移成功！", LogLevel.Success);

                task.Status = Models.TaskStatus.Completed;
                task.StatusText = "任務完美完成！";
                logger.Log($"[{task.Name}] 任務完成。", LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                task.Status = Models.TaskStatus.Cancelled;
                task.StatusText = "任務已取消。";
                logger.Log($"[{task.Name}] 任務被用戶取消。", LogLevel.Error); // 沒完成顯示灰色
            }
            catch (Exception ex)
            {
                task.Status = Models.TaskStatus.Failed;
                task.StatusText = $"失敗: {ex.Message}";
                logger.Log($"[{task.Name}] 發生錯誤: {ex.Message}", LogLevel.Error); // 錯誤代碼寫入終端，顯示灰色
            }
            finally
            {
                
                _semaphore.Release();
                cts.Dispose();
                
                // 計算並觸發總體進度
                int total = ActiveTasks.Count;
                int completed = ActiveTasks.Count(t => t.Status == Models.TaskStatus.Completed || t.Status == Models.TaskStatus.Failed || t.Status == Models.TaskStatus.Cancelled);
                double overallPct = total > 0 ? (double)completed / total * 100 : 0;
                OverallProgressChanged?.Invoke(overallPct);
                
                if (completed == total && total > 0) {
                    OverallStatusChanged?.Invoke("所有任務已完成！");
                } else {
                    OverallStatusChanged?.Invoke($"執行中... ({completed}/{total})");
                }
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
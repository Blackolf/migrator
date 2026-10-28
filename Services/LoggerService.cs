using System;
using System.Collections.ObjectModel;
using System.Windows;
using SystemMigrator.Models;

namespace SystemMigrator.Services
{
    public class LoggerService
    {
        public static LoggerService Instance { get; } = new LoggerService();
        public ObservableCollection<LogEntry> Logs { get; } = new();

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            // 確保在 UI 執行緒更新
            Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Add(new LogEntry { Message = message, Level = level });
                // 限制日誌數量，避免記憶體爆掉
                if (Logs.Count > 500) Logs.RemoveAt(0); 
            });
        }

        public void Clear()
        {
            Application.Current.Dispatcher.Invoke(() => Logs.Clear());
        }
    }
}
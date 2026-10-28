using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace SystemMigrator.Models
{
    public enum TaskStatus { Pending, Downloading, Installing, Migrating, Completed, Failed, Cancelled }

    public class InstalledApp : INotifyPropertyChanged
    {
        public string DisplayName { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string IconPath { get; set; } = "";
        public ImageSource? IconImage { get; set; }
        public string Category { get; set; } = "常用軟體";

        // 靜態事件：當任何一個軟體的勾選狀態改變時觸發
        public static event Action? SelectionChanged;

        private bool _isSelected;
        public bool IsSelected 
        { 
            get => _isSelected; 
            set 
            { 
                if (_isSelected != value)
                {
                    _isSelected = value; 
                    OnPropertyChanged();
                    // 觸發靜態事件，通知 UI 刷新分組計數
                    SelectionChanged?.Invoke(); 
                }
            } 
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // ================= 備份項目模型 =================
    public class BackupItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string Category { get; set; } = "";
        public long SizeInMB { get; set; }
        public DateTime? LastBackupTime { get; set; }

        // 靜態事件
        public static event Action? SelectionChanged;

        private bool _isSelected;
        public bool IsSelected 
        { 
            get => _isSelected; 
            set 
            { 
                if (_isSelected != value)
                {
                    _isSelected = value; 
                    OnPropertyChanged();
                    SelectionChanged?.Invoke();
                }
            } 
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
    public class MigrationTask : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "未知任務";
        public string WingetId { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string LocalInstallerDir { get; set; } = "";
        public string SilentArgs { get; set; } = "/S";
        public string TargetInstallDir { get; set; } = "";
        public List<string> SourcePaths { get; set; } = new();
        public string BackupZipPath { get; set; } = "";
        public bool IsBackupMode { get; set; } = true;

        // --- 新增：時間與日誌 ---
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public ObservableCollection<LogEntry> Logs { get; } = new();

        private TaskStatus _status = TaskStatus.Pending;
        public TaskStatus Status { get => _status; set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusDisplayText)); } }

        private double _progress = 0;
        public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); } }

        private string _statusText = "準備中...";
        public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }

        public string StatusDisplayText => Status switch
        {
            TaskStatus.Pending => "排隊中", TaskStatus.Downloading => "下載中", TaskStatus.Installing => "安裝中",
            TaskStatus.Migrating => "遷移中", TaskStatus.Completed => "已完成", TaskStatus.Failed => "失敗",
            TaskStatus.Cancelled => "已取消", _ => "未知"
        };

        // 封裝日誌寫入方法，確保在 UI 執行緒更新
        public void AddLog(string message, LogLevel level = LogLevel.Info)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Logs.Add(new LogEntry { Message = message, Level = level });
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
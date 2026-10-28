using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SystemMigrator.Models
{
    public enum TaskStatus { Pending, Downloading, Installing, Migrating, Completed, Failed, Cancelled }

    // 掃描到的已安裝軟體
    public class InstalledApp
    {
        public string DisplayName { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string IconPath { get; set; } = "";
        public ImageSource? IconImage { get; set; }
        public bool IsSelected { get; set; }
    }

    // 掃描到的備份項目 (配置/存檔)
    public class BackupItem
    {
        public string Name { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public string Category { get; set; } = "";
        public bool IsSelected { get; set; }
        public long SizeInMB { get; set; }
    }

    // 任務調度模型
    public class MigrationTask : INotifyPropertyChanged
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string Name { get; set; } = "未知任務";
        public string WingetId { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public string LocalInstallerDir { get; set; } = "";
        public string SilentArgs { get; set; } = "/S";
        public string TargetInstallDir { get; set; } = "";
        public List<string> SourcePaths { get; set; } = new();
        public string BackupZipPath { get; set; } = "";
        public bool IsBackupMode { get; set; } = true;

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

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
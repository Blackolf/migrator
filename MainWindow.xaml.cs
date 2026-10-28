using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using SystemMigrator.Models;
using SystemMigrator.Services;

namespace SystemMigrator
{
    public partial class MainWindow : Window
    {
        private readonly ScannerService _scanner = new();
        private readonly TaskSchedulerService _scheduler = new(3); // 最大並發 3
        private readonly SteamResolver _steamResolver = new();

        public MainWindow()
        {
            InitializeComponent();
            TasksItemsControl.ItemsSource = _scheduler.ActiveTasks;
            
            // 初始化 Steam 解析
            string steamPath = @"C:\Program Files (x86)\Steam\steamapps";
            if (Directory.Exists(steamPath)) _steamResolver.Initialize(steamPath);
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            BtnScan.IsEnabled = false;
            TxtGlobalStatus.Text = "正在掃描系統...";

            var apps = _scanner.ScanInstalledApps();
            LstApps.ItemsSource = apps;

            var backups = _scanner.ScanBackupItems();
            LstBackups.ItemsSource = backups;

            TxtGlobalStatus.Text = $"掃描完成！找到 {apps.Count} 個軟體，{backups.Count} 個備份項。";
            BtnScan.IsEnabled = true;
        }

        private void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            var selectedApps = (LstApps.ItemsSource as System.Collections.IEnumerable)?.Cast<InstalledApp>().Where(a => a.IsSelected).ToList();
            var selectedBackups = (LstBackups.ItemsSource as System.Collections.IEnumerable)?.Cast<BackupItem>().Where(b => b.IsSelected).ToList();

            if ((selectedApps == null || !selectedApps.Any()) && (selectedBackups == null || !selectedBackups.Any()))
            {
                MessageBox.Show("請至少勾選一個軟體或備份項目！");
                return;
            }

            string backupDir = TxtBackupDir.Text;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            // 生成任務
            if (selectedApps != null)
            {
                foreach (var app in selectedApps)
                {
                    _scheduler.EnqueueTask(new MigrationTask
                    {
                        Name = app.DisplayName,
                        WingetId = app.DisplayName, // 簡化版：直接用名稱搜索 Winget
                        StatusText = "等待調度..."
                    });
                }
            }

            if (selectedBackups != null)
            {
                foreach (var backup in selectedBackups)
                {
                    string safeName = string.Join("_", backup.Name.Split(Path.GetInvalidFileNameChars()));
                    _scheduler.EnqueueTask(new MigrationTask
                    {
                        Name = $"遷移: {backup.Name}",
                        SourcePaths = new() { backup.SourcePath },
                        BackupZipPath = Path.Combine(backupDir, $"{safeName}.zip"),
                        IsBackupMode = true, // 這裡預設為備份模式，重裝後可改為 false 進行還原
                        StatusText = "等待調度..."
                    });
                }
            }

            TxtGlobalStatus.Text = "任務已加入隊列，正在並行執行...";
        }
    }
}
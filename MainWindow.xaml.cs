using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SystemMigrator.Models;
using SystemMigrator.Services;

namespace SystemMigrator
{
    public partial class MainWindow : Window
    {
        private readonly ScannerService _scanner = new();
        private readonly TaskSchedulerService _scheduler = new(3);
        private readonly SteamResolver _steamResolver = new();
        private readonly LoggerService _logger = LoggerService.Instance;

        public MainWindow()
        {
            InitializeComponent();
            TasksItemsControl.ItemsSource = _scheduler.ActiveTasks;
            
            // 綁定日誌到 UI
            var logControl = FindName("LogScrollViewer") as ScrollViewer;
            if (logControl?.Content is ItemsControl itemsControl)
            {
                itemsControl.ItemsSource = _logger.Logs;
            }

            _scheduler.OverallProgressChanged += pct => Dispatcher.Invoke(() => PbOverall.Value = pct);
            _scheduler.OverallStatusChanged += msg => Dispatcher.Invoke(() => TxtOverallStatus.Text = msg);

            string backupDir = TxtBackupDir.Text;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            string steamPath = @"C:\Program Files (x86)\Steam\steamapps";
            if (Directory.Exists(steamPath)) _steamResolver.Initialize(steamPath);

            _logger.Log("系統遷移大師已啟動，等待掃描...", LogLevel.Info);
        }

        // 軟體列表：全選
        private void BtnSelectAllApps_Click(object sender, RoutedEventArgs e)
        {
            if (LstApps.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<InstalledApp>())
                {
                    item.IsSelected = true;
                }
                view.Refresh(); // 刷新以更新分組計數
            }
        }

        // 軟體列表：全不選
        private void BtnSelectNoneApps_Click(object sender, RoutedEventArgs e)
        {
            if (LstApps.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<InstalledApp>())
                {
                    item.IsSelected = false;
                }
                view.Refresh();
            }
        }

        // 備份列表：全選
        private void BtnSelectAllBackups_Click(object sender, RoutedEventArgs e)
        {
            if (LstBackups.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<BackupItem>())
                {
                    item.IsSelected = true;
                }
                view.Refresh();
            }
        }

        // 備份列表：全不選
        private void BtnSelectNoneBackups_Click(object sender, RoutedEventArgs e)
        {
            if (LstBackups.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<BackupItem>())
                {
                    item.IsSelected = false;
                }
                view.Refresh();
            }
        }
        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            string backupDir = TxtBackupDir.Text;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            TxtGlobalStatus.Text = "正在掃描系統...";
            BtnScan.IsEnabled = false;
            _logger.Log("開始掃描系統註冊表與目錄...", LogLevel.Info);

            var apps = _scanner.ScanInstalledApps();
            var appsView = new ListCollectionView(apps);
            appsView.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            LstApps.ItemsSource = appsView;

            var backups = _scanner.ScanBackupItems(backupDir);
            var backupsView = new ListCollectionView(backups);
            backupsView.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
            LstBackups.ItemsSource = backupsView;

            TxtGlobalStatus.Text = $"掃描完成！找到 {apps.Count} 個軟體，{backups.Count} 個備份項。";
            BtnScan.IsEnabled = true;
            _logger.Log($"掃描完成：發現 {apps.Count} 個軟體，{backups.Count} 個數據項。", LogLevel.Success);
        }

        private void BtnExecute_Click(object sender, RoutedEventArgs e)
        {
            var appsView = LstApps.ItemsSource as ListCollectionView;
            var selectedApps = appsView?.SourceCollection.Cast<InstalledApp>().Where(a => a.IsSelected).ToList();

            var backupsView = LstBackups.ItemsSource as ListCollectionView;
            var selectedBackups = backupsView?.SourceCollection.Cast<BackupItem>().Where(b => b.IsSelected).ToList();

            if ((selectedApps == null || !selectedApps.Any()) && (selectedBackups == null || !selectedBackups.Any()))
            {
                MessageBox.Show("請至少勾選一個軟體或備份項目！");
                return;
            }

            string backupDir = TxtBackupDir.Text;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            int taskCount = 0;
            if (selectedApps != null)
            {
                foreach (var app in selectedApps)
                {
                    _scheduler.EnqueueTask(new MigrationTask { Name = app.DisplayName, WingetId = app.DisplayName, StatusText = "等待調度..." });
                    taskCount++;
                }
            }
            if (selectedBackups != null)
            {
                foreach (var backup in selectedBackups)
                {
                    string safeName = string.Join("_", backup.Name.Split(Path.GetInvalidFileNameChars()));
                    _scheduler.EnqueueTask(new MigrationTask { Name = $"遷移: {backup.Name}", SourcePaths = new() { backup.SourcePath }, BackupZipPath = Path.Combine(backupDir, $"{safeName}.zip"), IsBackupMode = true, StatusText = "等待調度..." });
                    taskCount++;
                }
            }

            TxtGlobalStatus.Text = "任務已加入隊列，正在並行執行...";
            TxtOverallStatus.Text = "執行中... (0/0)";
            _logger.Log($"已生成 {taskCount} 個任務，開始並行調度...", LogLevel.Success);
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _logger.Clear();
            _logger.Log("終端日誌已清除。", LogLevel.Info);
        }
    }
}
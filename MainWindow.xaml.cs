using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
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

        public MainWindow()
        {
            InitializeComponent();
            TasksItemsControl.ItemsSource = _scheduler.ActiveTasks;

            // 【核心修復】訂閱 Model 的靜態事件，實現自動刷新分組計數
            InstalledApp.SelectionChanged += RefreshViews;
            BackupItem.SelectionChanged += RefreshViews;

            _scheduler.OverallProgressChanged += pct => Dispatcher.Invoke(() => PbOverall.Value = pct);
            _scheduler.OverallStatusChanged += msg => Dispatcher.Invoke(() => TxtOverallStatus.Text = msg);

            string backupDir = TxtBackupDir.Text;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            string steamPath = @"C:\Program Files (x86)\Steam\steamapps";
            if (Directory.Exists(steamPath)) _steamResolver.Initialize(steamPath);
        }

        // 統一刷新視圖的方法
        private void RefreshViews()
        {
            Dispatcher.Invoke(() =>
            {
                (LstApps.ItemsSource as ICollectionView)?.Refresh();
                (LstBackups.ItemsSource as ICollectionView)?.Refresh();
            });
        }

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            string backupDir = TxtBackupDir.Text;
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            TxtGlobalStatus.Text = "正在掃描系統...";
            BtnScan.IsEnabled = false;

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
            
            // 掃描完成後手動刷新一次，確保初始狀態的計數正確
            RefreshViews(); 
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
        }

        // ================= 全選/全不選按鈕邏輯 (已簡化) =================
        
        private void BtnSelectAllApps_Click(object sender, RoutedEventArgs e)
        {
            if (LstApps.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<InstalledApp>())
                {
                    item.IsSelected = true; // 現在這行代碼會自動觸發 SelectionChanged 並 Refresh
                }
            }
        }

        private void BtnSelectNoneApps_Click(object sender, RoutedEventArgs e)
        {
            if (LstApps.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<InstalledApp>())
                {
                    item.IsSelected = false; // 現在這行代碼也能完美取消 UI 上的勾選了！
                }
            }
        }

        private void BtnSelectAllBackups_Click(object sender, RoutedEventArgs e)
        {
            if (LstBackups.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<BackupItem>())
                {
                    item.IsSelected = true;
                }
            }
        }

        private void BtnSelectNoneBackups_Click(object sender, RoutedEventArgs e)
        {
            if (LstBackups.ItemsSource is ListCollectionView view)
            {
                foreach (var item in view.SourceCollection.Cast<BackupItem>())
                {
                    item.IsSelected = false;
                }
            }
        }
    }
}
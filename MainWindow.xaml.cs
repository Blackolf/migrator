using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
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
        private readonly SteamResolver _steamResolver = new();
        private readonly MirrorService _mirrors;
        private readonly SoftwareInstallerService _installer;
        private readonly TaskSchedulerService _scheduler;
        private readonly string _localInstallerRoot;

        private bool _mirrorUiReady;

        public MainWindow()
        {
            InitializeComponent();

            // ---------- 鏡像源 ----------
            _mirrors = new MirrorService();
            _mirrors.StatusChanged += message => Dispatcher.Invoke(() => TxtMirrorStatus.Text = message);

            TxtMirrorStatus.Text = _mirrors.IsLoaded
                ? $"已載入 {_mirrors.Catalog.Packages.Count} 個套件規則 · {_mirrors.ModeDisplay}"
                : $"鏡像設定載入失敗：{_mirrors.LoadError}";

            // ---------- 服務 ----------
            _installer = new SoftwareInstallerService(_mirrors);
            _scheduler = new TaskSchedulerService(3, _installer);
            _localInstallerRoot = Path.Combine(AppContext.BaseDirectory, "Installers");

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

            // 最後才同步下拉選單，避免初始化期間觸發 SelectionChanged 又存一次設定
            CmbMirrorMode.SelectedIndex = _mirrors.Mode switch
            {
                MirrorMode.Official => 1,
                MirrorMode.Domestic => 2,
                _ => 0
            };
            _mirrorUiReady = true;
        }

        // ==================== 鏡像源 ====================

        private void CmbMirrorMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_mirrorUiReady) return;
            if (CmbMirrorMode.SelectedItem is not ComboBoxItem item) return;
            if (!Enum.TryParse(item.Tag as string, out MirrorMode mode)) return;

            _mirrors.SetMode(mode);
            TxtMirrorStatus.Text = $"下載來源已切換為：{_mirrors.ModeDisplay}";
        }

        private async void BtnTestMirror_Click(object sender, RoutedEventArgs e)
        {
            BtnTestMirror.IsEnabled = false;
            TxtMirrorStatus.Text = "正在測試各鏡像來源（HEAD 測速）...";

            try
            {
                var results = await _mirrors.TestAllAsync(CancellationToken.None);

                if (results.Count == 0)
                {
                    TxtMirrorStatus.Text = "沒有可測試的鏡像來源（請檢查 Config/mirrors.json）。";
                    return;
                }

                string summary = string.Join("   |   ", results.Select(result => result.Ok
                    ? $"{result.Candidate.Source.Name}: {result.LatencyMs} ms"
                    : $"{result.Candidate.Source.Name}: 失敗"));

                TxtMirrorStatus.Text = summary;
            }
            catch (Exception ex)
            {
                TxtMirrorStatus.Text = $"測速失敗：{ex.Message}";
            }
            finally
            {
                BtnTestMirror.IsEnabled = true;
            }
        }

        // ==================== 掃描 ====================

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

            // 延遲到 UI 渲染完成後再刷新，避免視覺樹重建衝突
            ForceRefreshOnce();
        }

        // ==================== 執行 ====================

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

            if (selectedApps != null)
            {
                foreach (var app in selectedApps)
                {
                    // 有鏡像規則就用它的 Winget ID；沒有則留空，讓安裝器改用 --name 或本地包。
                    MirrorPackage? package = _mirrors.FindPackage(app.DisplayName);

                    _scheduler.EnqueueTask(new MigrationTask
                    {
                        Name = app.DisplayName,
                        WingetId = package?.WingetId ?? "",
                        LocalInstallerDir = package == null ? "" : Path.Combine(_localInstallerRoot, package.Key),
                        IsInstallTask = true,
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
                        IsBackupMode = true,
                        StatusText = "等待調度..."
                    });
                }
            }

            TxtGlobalStatus.Text = "任務已加入隊列，正在並行執行...";
            TxtOverallStatus.Text = "執行中... (0/0)";
        }

        // 批量更新標記：為 true 時，Model 的 SelectionChanged 不會觸發 Refresh
        private bool _isBatchUpdating = false;

        // 統一刷新視圖的方法（加入批量保護）
        private void RefreshViews()
        {
            if (_isBatchUpdating) return; // 批量更新中，跳過自動刷新

            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, new Action(() =>
            {
                double appOffset = AppScroll?.VerticalOffset ?? 0;
                double backupOffset = BackupScroll?.VerticalOffset ?? 0;

                (LstApps.ItemsSource as ICollectionView)?.Refresh();
                (LstBackups.ItemsSource as ICollectionView)?.Refresh();

                AppScroll?.ScrollToVerticalOffset(appOffset);
                BackupScroll?.ScrollToVerticalOffset(backupOffset);
            }));
        }

        // 掃描完成後的手動刷新（確保初始狀態正確）
        private void ForceRefreshOnce()
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                (LstApps.ItemsSource as ICollectionView)?.Refresh();
                (LstBackups.ItemsSource as ICollectionView)?.Refresh();
            }));
        }

        // ==================== 全選/全不選按鈕邏輯（批量優化版） ====================

        private void BtnSelectAllApps_Click(object sender, RoutedEventArgs e)
        {
            if (LstApps.ItemsSource is ListCollectionView view)
            {
                _isBatchUpdating = true; // 暫停自動刷新
                foreach (var item in view.SourceCollection.Cast<InstalledApp>())
                {
                    item.IsSelected = true; // 這 100 次賦值不會觸發 Refresh
                }
                _isBatchUpdating = false; // 恢復自動刷新
                RefreshViews(); // 統一刷新一次
            }
        }

        private void BtnSelectNoneApps_Click(object sender, RoutedEventArgs e)
        {
            if (LstApps.ItemsSource is ListCollectionView view)
            {
                _isBatchUpdating = true;
                foreach (var item in view.SourceCollection.Cast<InstalledApp>())
                {
                    item.IsSelected = false;
                }
                _isBatchUpdating = false;
                RefreshViews();
            }
        }

        private void BtnSelectAllBackups_Click(object sender, RoutedEventArgs e)
        {
            if (LstBackups.ItemsSource is ListCollectionView view)
            {
                _isBatchUpdating = true;
                foreach (var item in view.SourceCollection.Cast<BackupItem>())
                {
                    item.IsSelected = true;
                }
                _isBatchUpdating = false;
                RefreshViews();
            }
        }

        private void BtnSelectNoneBackups_Click(object sender, RoutedEventArgs e)
        {
            if (LstBackups.ItemsSource is ListCollectionView view)
            {
                _isBatchUpdating = true;
                foreach (var item in view.SourceCollection.Cast<BackupItem>())
                {
                    item.IsSelected = false;
                }
                _isBatchUpdating = false;
                RefreshViews();
            }
        }
    }
}

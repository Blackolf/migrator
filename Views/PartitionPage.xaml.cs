using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SystemMigrator.Models;
using SystemMigrator.Services;

namespace SystemMigrator.Views
{
    /// <summary>
    /// 「分區備份與安裝」頁：
    /// 左側列舉磁碟與分區（單選），右側提供 WIM 映像備份、Robocopy 檔案級複製、映像還原與引導重建。
    /// 所有操作共用一個 MigrationTask 來記錄獨立日誌、進度與開始/結束時間。
    /// </summary>
    public partial class PartitionPage : UserControl
    {
        private readonly PartitionService _partitionService = new();
        private readonly DiskImageService _imageService = new();

        private CancellationTokenSource? _cts;

        /// <summary>本頁的任務物件（日誌／進度／狀態綁定來源）。</summary>
        public MigrationTask Job { get; } = new() { Name = "分區任務" };

        /// <summary>目前列舉到的分區。</summary>
        public ObservableCollection<PartitionInfo> Partitions { get; } = new();

        public PartitionPage()
        {
            InitializeComponent();
            DataContext = this;

            _partitionService.Log += (message, level) => Job.AddLog(message, level);
            _imageService.Log += (message, level) => Job.AddLog(message, level);
            _imageService.Progress += percent => Dispatcher.Invoke(() => Job.Progress = percent);

            Loaded += async (_, _) => await RefreshDisksAsync();
            // 注意：刻意不綁 Unloaded 取消。切換分頁時 WPF 會把內容移出視覺樹並觸發 Unloaded，
            // 若在這裡取消，使用者在備份途中切到另一個分頁就會中斷任務。
        }

        private PartitionInfo? SelectedPartition => Partitions.FirstOrDefault(p => p.IsSelected);

        // ==================== 磁碟列舉 ====================

        private async Task RefreshDisksAsync()
        {
            BtnRefreshDisks.IsEnabled = false;
            TxtDiskHint.Text = "正在讀取磁碟與分區資訊（完整資訊需要管理員權限）...";

            try
            {
                IReadOnlyList<PartitionInfo> partitions = await Task.Run(() => _partitionService.Enumerate());

                Partitions.Clear();
                foreach (PartitionInfo partition in partitions) Partitions.Add(partition);

                var view = new ListCollectionView(Partitions);
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PartitionInfo.DiskDisplay)));
                DiskItems.ItemsSource = view;

                int diskCount = partitions
                    .Select(p => p.DiskNumber)
                    .Where(number => number >= 0)
                    .Distinct()
                    .Count();

                TxtDiskHint.Text = partitions.Count == 0
                    ? "找不到任何固定磁碟分區。請確認磁碟是否在線，或改以管理員身分執行。"
                    : $"共 {diskCount} 顆磁碟、{partitions.Count} 個分區；系統分區已標記，還原時會自動排除。";

                // ESP 下拉：列出有磁碟機代號的 EFI 分區，空值代表自動掛載。
                CmbEsp.ItemsSource = partitions
                    .Where(p => p.Kind == PartitionKind.Efi && !string.IsNullOrEmpty(p.DriveLetter))
                    .Select(p => p.DisplayLetter)
                    .ToList();

                UpdateSelectionUi();
            }
            catch (Exception ex)
            {
                Job.AddLog($"讀取磁碟失敗：{ex.Message}", LogLevel.Error);
                TxtDiskHint.Text = "讀取磁碟失敗，請確認程式是否以管理員身分執行。";
            }
            finally
            {
                BtnRefreshDisks.IsEnabled = true;
            }
        }

        // ==================== 選取 ====================

        private void Row_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: PartitionInfo target }) return;

            foreach (PartitionInfo partition in Partitions)
            {
                if (!ReferenceEquals(partition, target) && partition.IsSelected)
                {
                    partition.IsSelected = false;
                }
            }

            UpdateSelectionUi();
        }

        private void Row_Unchecked(object sender, RoutedEventArgs e)
        {
            UpdateSelectionUi();
        }

        private void UpdateSelectionUi()
        {
            PartitionInfo? selected = SelectedPartition;

            if (selected == null)
            {
                TxtSelection.Text = "尚未選擇分區";
                BtnBackup.IsEnabled = false;
                BtnApply.IsEnabled = false;
                return;
            }

            string warning = selected.IsSystemPartition ? "\n⚠ 這是目前執行中的系統分區。" : "";
            TxtSelection.Text =
                $"已選：{selected.DisplayLetter}  {selected.KindText}  {selected.SizeText}\n" +
                $"可用空間：{selected.FreeText}（{selected.UsageText}）\n" +
                $"磁碟：{selected.DiskDisplay}{warning}";

            bool hasLetter = !string.IsNullOrEmpty(selected.DriveLetter);
            BtnBackup.IsEnabled = hasLetter && _cts == null;

            bool canApply = hasLetter
                            && !selected.IsSystemPartition
                            && selected.Kind is not (PartitionKind.Efi or PartitionKind.Msr);

            BtnApply.IsEnabled = canApply && _cts == null;
        }

        // ==================== 備份 ====================

        private async void BtnBackup_Click(object sender, RoutedEventArgs e)
        {
            PartitionInfo? source = SelectedPartition;
            if (source == null) return;

            string imagePath = TxtImagePath.Text.Trim();
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                MessageBox.Show("請先指定備份目標（映像檔路徑或資料夾）。", "缺少目標",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool folderMode = RbFolder.IsChecked == true;

            if (!folderMode && !imagePath.EndsWith(".wim", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("WIM 完成映像請使用 .wim 副檔名。", "副檔名錯誤",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!CheckDestinationSpace(imagePath, source, folderMode))
            {
                return;
            }

            if (source.IsSystemPartition && !folderMode)
            {
                MessageBoxResult result = MessageBox.Show(
                    "你正在備份「目前執行中的系統分區」。\n" +
                    "線上擷取可能因檔案鎖定而不完整，正式遷移建議在 WinPE 離線執行。\n\n仍要繼續嗎？",
                    "系統分區提示",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes) return;
            }

            string compression = SelectedCompression;
            bool checkIntegrity = ChkIntegrity.IsChecked == true;

            await RunJobAsync(folderMode ? "檔案級備份" : "WIM 備份", async token =>
            {
                if (folderMode)
                {
                    return await _imageService.CaptureFolderAsync(source, imagePath, token);
                }

                return await _imageService.CaptureWimAsync(source, imagePath, compression, checkIntegrity, token);
            });
        }

        private bool CheckDestinationSpace(string imagePath, PartitionInfo source, bool folderMode)
        {
            try
            {
                string? root = Path.GetPathRoot(imagePath);
                if (string.IsNullOrWhiteSpace(root)) return true;

                var destination = new DriveInfo(root);
                if (!destination.IsReady) return true;

                double used = source.SizeBytes > source.FreeBytes
                    ? (double)(source.SizeBytes - source.FreeBytes)
                    : source.SizeBytes / 2.0;

                // WIM 依內容可壓縮到 40-60%；檔案級複製則是等量。
                double estimate = folderMode ? used : used * 0.6;

                if (destination.AvailableFreeSpace < estimate)
                {
                    MessageBox.Show(
                        $"目標磁碟可用空間不足。\n需要約 {PartitionInfo.FormatBytes((ulong)estimate)}，" +
                        $"目前可用 {PartitionInfo.FormatBytes((ulong)destination.AvailableFreeSpace)}。",
                        "空間不足",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }
            catch
            {
                // 無法估算時不阻止使用者。
            }

            return true;
        }

        // ==================== 還原 / 安裝 ====================

        private async void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            PartitionInfo? target = SelectedPartition;
            if (target == null) return;

            string imagePath = TxtImagePath.Text.Trim();
            if (!File.Exists(imagePath))
            {
                MessageBox.Show("請先指定存在且有效的 WIM 映像檔。", "找不到映像檔",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrEmpty(target.DriveLetter) || target.IsSystemPartition)
            {
                MessageBox.Show("此分區不能作為安裝目標。", "無效的目標",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int imageIndex = CmbImageIndex.SelectedItem is int index && index > 0 ? index : 1;

            MessageBoxResult confirm = MessageBox.Show(
                $"即將把映像還原到：\n{target.RootPath}（{target.KindText}，{target.SizeText}）\n\n" +
                $"目標分區現有資料會被覆蓋{(ChkFormat.IsChecked == true ? "，並先執行快速格式化" : "")}。\n" +
                "此操作無法復原，確定要繼續嗎？",
                "確認還原",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            bool formatFirst = ChkFormat.IsChecked == true;
            bool repairBoot = ChkRepairBoot.IsChecked == true;
            string? esp = CmbEsp.SelectedItem as string;

            await RunJobAsync("分區安裝", async token =>
            {
                bool ok = await _imageService.ApplyWimAsync(imagePath, imageIndex, target, formatFirst, token);

                if (ok && repairBoot)
                {
                    ok = await _imageService.RepairBootAsync(target, esp, token);
                }

                return ok;
            });
        }

        private async void BtnReadImage_Click(object sender, RoutedEventArgs e)
        {
            string imagePath = TxtImagePath.Text.Trim();
            if (!File.Exists(imagePath))
            {
                MessageBox.Show("請先指定存在的 WIM 映像檔。", "找不到映像檔",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnReadImage.IsEnabled = false;
            try
            {
                IReadOnlyList<WimImageInfo> images = await _imageService.GetWimInfoAsync(imagePath, CancellationToken.None);

                if (images.Count == 0)
                {
                    Job.AddLog("讀不到任何映像索引，請確認檔案是否為有效的 WIM。", LogLevel.Error);
                    return;
                }

                CmbImageIndex.ItemsSource = images.Select(image => image.Index).ToList();
                CmbImageIndex.SelectedIndex = 0;
                Job.AddLog($"已讀取 {images.Count} 個映像索引。", LogLevel.Success);
            }
            catch (Exception ex)
            {
                Job.AddLog($"讀取映像資訊失敗：{ex.Message}", LogLevel.Error);
            }
            finally
            {
                BtnReadImage.IsEnabled = true;
            }
        }

        // ==================== 瀏覽 / 取消 ====================

        private void BtnBrowseImage_Click(object sender, RoutedEventArgs e)
        {
            PartitionInfo? selected = SelectedPartition;

            if (RbFolder.IsChecked == true)
            {
                var folderDialog = new Microsoft.Win32.OpenFolderDialog { Title = "選擇檔案級複製的目標資料夾" };
                if (folderDialog.ShowDialog() == true) TxtImagePath.Text = folderDialog.FolderName;
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "選擇 WIM 映像檔位置",
                Filter = "WIM 映像 (*.wim)|*.wim|所有檔案 (*.*)|*.*",
                FileName = $"Partition_{(string.IsNullOrEmpty(selected?.DriveLetter) ? "X" : selected!.DriveLetter)}_{DateTime.Now:yyyyMMdd_HHmm}.wim",
                OverwritePrompt = true
            };

            if (dialog.ShowDialog() == true) TxtImagePath.Text = dialog.FileName;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Job.AddLog("已送出取消請求，正在中止子程序...", LogLevel.Info);
        }

        private void BtnRefreshDisks_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshDisksAsync();
        }

        // ==================== 任務包裝 ====================

        private async Task RunJobAsync(string title, Func<CancellationToken, Task<bool>> action)
        {
            if (_cts != null) return;

            _cts = new CancellationTokenSource();
            SetBusy(true);

            Job.StartTime = DateTime.Now;
            Job.EndTime = null;
            Job.Progress = 0;
            Job.Status = Models.TaskStatus.Migrating;
            Job.StatusText = $"{title}進行中...";
            Job.AddLog($"=== {title} 開始（{DateTime.Now:HH:mm:ss}）===");

            try
            {
                bool ok = await action(_cts.Token);

                Job.Status = ok ? Models.TaskStatus.Completed : Models.TaskStatus.Failed;
                Job.StatusText = ok ? $"{title}完成" : $"{title}失敗";
                Job.Progress = ok ? 100 : Job.Progress;
                Job.AddLog(ok ? $"{title}成功。" : $"{title}失敗，詳見日誌。",
                    ok ? LogLevel.Success : LogLevel.Error);
            }
            catch (OperationCanceledException)
            {
                Job.Status = Models.TaskStatus.Cancelled;
                Job.StatusText = "已取消";
                Job.AddLog("操作已取消。", LogLevel.Error);
            }
            catch (Exception ex)
            {
                Job.Status = Models.TaskStatus.Failed;
                Job.StatusText = $"失敗：{ex.Message}";
                Job.AddLog($"例外：{ex.Message}", LogLevel.Error);
            }
            finally
            {
                Job.EndTime = DateTime.Now;
                Job.AddLog($"=== 結束（{DateTime.Now:HH:mm:ss}）===");

                _cts.Dispose();
                _cts = null;
                SetBusy(false);
                UpdateSelectionUi();
            }
        }

        private void SetBusy(bool busy)
        {
            BtnBackup.IsEnabled = !busy;
            BtnApply.IsEnabled = !busy;
            BtnRefreshDisks.IsEnabled = !busy;
            BtnReadImage.IsEnabled = !busy;
            BtnCancel.IsEnabled = busy;
        }

        private string SelectedCompression =>
            (CmbCompress.SelectedItem as ComboBoxItem)?.Tag as string ?? "max";
    }
}

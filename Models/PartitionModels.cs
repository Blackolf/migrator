using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SystemMigrator.Models
{
    /// <summary>分區表格式。</summary>
    public enum PartitionStyle
    {
        Unknown = 0,
        Mbr = 1,
        Gpt = 2
    }

    /// <summary>分區用途分類。</summary>
    public enum PartitionKind
    {
        Unknown = 0,
        BasicData = 1,
        Efi = 2,
        Msr = 3,
        Recovery = 4,
        Other = 5
    }

    /// <summary>
    /// 磁碟分區資訊（由 PartitionService 透過 IOCTL 查詢，失敗時以 DriveInfo 回退）。
    /// </summary>
    public sealed class PartitionInfo : INotifyPropertyChanged
    {
        public int DiskNumber { get; set; } = -1;
        public string DiskModel { get; set; } = "未知型號";
        public ulong DiskSizeBytes { get; set; }

        public int PartitionNumber { get; set; }
        public string DriveLetter { get; set; } = "";
        public string Label { get; set; } = "";
        public string FileSystem { get; set; } = "";
        public PartitionStyle Style { get; set; } = PartitionStyle.Unknown;
        public PartitionKind Kind { get; set; } = PartitionKind.Unknown;
        public Guid TypeGuid { get; set; }
        public long StartingOffset { get; set; }
        public ulong SizeBytes { get; set; }
        public ulong FreeBytes { get; set; }
        public bool IsSystemPartition { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        // ==================== 顯示用屬性 ====================

        /// <summary>例如 "C:" 或 "未指派"。</summary>
        public string DisplayLetter => string.IsNullOrEmpty(DriveLetter) ? "未指派" : $"{DriveLetter}:";

        /// <summary>例如 "C:\"，未掛載時為空字串。</summary>
        public string RootPath => string.IsNullOrEmpty(DriveLetter) ? "" : $"{DriveLetter}:\\";

        public string StyleText => Style switch
        {
            PartitionStyle.Gpt => "GPT",
            PartitionStyle.Mbr => "MBR",
            _ => "未知"
        };

        public string KindText => Kind switch
        {
            PartitionKind.Efi => "EFI 系統分割",
            PartitionKind.Msr => "MSR 保留分割",
            PartitionKind.Recovery => "復原分割",
            PartitionKind.BasicData => "主分割（資料）",
            PartitionKind.Other => "其他分割",
            _ => "未知分割"
        };

        public string SizeText => FormatBytes(SizeBytes);

        public string FreeText => string.IsNullOrEmpty(DriveLetter) ? "—" : FormatBytes(FreeBytes);

        public string UsageText
        {
            get
            {
                if (SizeBytes == 0) return "";
                double used = SizeBytes - FreeBytes;
                double percent = Math.Round(used / SizeBytes * 100);
                return $"{percent:F0}% 已用";
            }
        }

        /// <summary>分組標題：磁碟 0 · 型號 (容量)。</summary>
        public string DiskDisplay => DiskNumber < 0
            ? "磁碟資訊（無法取得磁碟編號）"
            : $"磁碟 {DiskNumber} · {DiskModel}（{FormatBytes(DiskSizeBytes)}）";

        public string SubText => DiskNumber < 0
            ? $"{KindText} · {StyleText}"
            : $"分割 {PartitionNumber} · {KindText} · {StyleText}";

        public static string FormatBytes(ulong bytes)
        {
            if (bytes == 0) return "0 B";

            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double value = bytes;
            int unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>分區備份 / 還原的操作參數。</summary>
    public sealed class PartitionOperationOptions
    {
        public PartitionInfo? Source { get; set; }
        public PartitionInfo? Target { get; set; }

        /// <summary>WIM 映像檔路徑，或檔案級複製的目標資料夾。</summary>
        public string ImagePath { get; set; } = "";

        /// <summary>DISM 壓縮等級：max / fast / none。</summary>
        public string Compression { get; set; } = "max";

        /// <summary>擷取時寫入校驗資訊。</summary>
        public bool CheckIntegrity { get; set; }

        /// <summary>還原前快速格式化目標分區。</summary>
        public bool FormatTarget { get; set; }

        /// <summary>還原後執行 bcdboot 重建引導。</summary>
        public bool RepairBoot { get; set; }

        /// <summary>ESP 磁碟機代號（可空，空則自動掛載）。</summary>
        public string? EspDriveLetter { get; set; }
    }

    /// <summary>WIM 映像內的單一映像索引資訊。</summary>
    public sealed class WimImageInfo
    {
        public int Index { get; set; }
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public long SizeBytes { get; set; }

        public string SizeText => PartitionInfo.FormatBytes((ulong)Math.Max(0, SizeBytes));
        public string Display => $"#{Index} {Name}（{SizeText}）";
    }
}

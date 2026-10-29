using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SystemMigrator.Models
{
    /// <summary>
    /// 下載鏡像來源模式。
    /// </summary>
    public enum MirrorMode
    {
        /// <summary>國內鏡像優先，失敗才回退官方源（預設）。</summary>
        Auto = 0,

        /// <summary>只使用官方源。</summary>
        Official = 1,

        /// <summary>只使用國內鏡像（沒有對應鏡像時仍會回退官方源並記錄日誌）。</summary>
        Domestic = 2
    }

    /// <summary>
    /// 單一鏡像來源（官方源也算一種來源）。
    /// </summary>
    public sealed class MirrorSource
    {
        /// <summary>唯一識別碼，例如 official、npmmirror、huaweicloud。</summary>
        public string Id { get; set; } = "";

        /// <summary>顯示名稱，例如「npmmirror（阿里）」。</summary>
        public string Name { get; set; } = "";

        /// <summary>Official 或 Domestic。</summary>
        public string Region { get; set; } = "Official";

        /// <summary>數字越小越優先嘗試。</summary>
        public int Priority { get; set; } = 100;

        /// <summary>是否啟用；停用後 Resolve 不會產生此來源的候選 URL。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 主機替換規則（可選）：{ "官方主機": "鏡像主機" }。
        /// 只適用於「路徑結構與官方完全相同」的鏡像，例如某些反向代理；不確定就不要填。
        /// </summary>
        public Dictionary<string, string> HostRewrite { get; set; } = new();

        [JsonIgnore]
        public bool IsOfficial => string.Equals(Id, "official", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(Region, "official", StringComparison.OrdinalIgnoreCase);

        public override string ToString() => Name;
    }

    /// <summary>
    /// 一個可安裝軟體的鏡像規則（對應 Config/mirrors.json 的 packages 節點）。
    /// </summary>
    public sealed class MirrorPackage
    {
        /// <summary>短鍵，例如 git、node、python、vlc。</summary>
        public string Key { get; set; } = "";

        /// <summary>顯示名稱，用於模糊比對掃描到的軟體。</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>Winget 套件 ID，例如 Git.Git。</summary>
        public string WingetId { get; set; } = "";

        /// <summary>官方下載 URL。</summary>
        public string OfficialUrl { get; set; } = "";

        /// <summary>下載後的檔名（含副檔名），供三級降級與本地快取使用。</summary>
        public string FileName { get; set; } = "";

        /// <summary>靜默安裝參數；留空則使用任務上的 SilentArgs。</summary>
        public string SilentArgs { get; set; } = "";

        /// <summary>可選的 SHA256 校驗值（小寫十六進位），留空則不校驗。</summary>
        public string? Sha256 { get; set; }

        /// <summary>各鏡像源的專屬下載 URL：{ "npmmirror": "https://..." }。</summary>
        public Dictionary<string, string> Mirrors { get; set; } = new();

        public override string ToString() => $"{DisplayName} ({WingetId})";
    }

    /// <summary>Config/mirrors.json 的根物件。</summary>
    public sealed class MirrorCatalog
    {
        public MirrorMode DefaultMode { get; set; } = MirrorMode.Auto;
        public List<MirrorSource> Sources { get; set; } = new();
        public List<MirrorPackage> Packages { get; set; } = new();
    }

    /// <summary>解析後的一個候選下載來源。</summary>
    public sealed class MirrorCandidate
    {
        public MirrorSource Source { get; set; } = new();
        public string Url { get; set; } = "";

        public override string ToString() => $"{Source.Name}: {Url}";
    }

    /// <summary>鏡像測速結果。</summary>
    public sealed class MirrorTestResult
    {
        public MirrorCandidate Candidate { get; set; } = new();
        public bool Ok { get; set; }
        public int LatencyMs { get; set; }
        public int StatusCode { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>使用者偏好設定（存於 %APPDATA%\SystemMigrator\settings.json）。</summary>
    public sealed class AppSettings
    {
        public MirrorMode MirrorMode { get; set; } = MirrorMode.Auto;
    }
}

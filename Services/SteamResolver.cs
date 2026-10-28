using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SystemMigrator.Services
{
    public class SteamResolver
    {
        private Dictionary<string, string> _appIdToName = new();

        // 初始化：掃描 Steam 目錄下的 appmanifest 文件
        public void Initialize(string steamAppsPath)
        {
            if (!Directory.Exists(steamAppsPath)) return;

            // 查找所有 appmanifest_*.acf 文件
            var manifestFiles = Directory.GetFiles(steamAppsPath, "appmanifest_*.acf");
            foreach (var file in manifestFiles)
            {
                try
                {
                    string content = File.ReadAllText(file);
                    // 使用正則提取 appid 和 name
                    var appIdMatch = Regex.Match(content, @"""appid""\s+""(\d+)""");
                    var nameMatch = Regex.Match(content, @"""name""\s+""(.+?)""");

                    if (appIdMatch.Success && nameMatch.Success)
                    {
                        string appId = appIdMatch.Groups[1].Value;
                        string name = nameMatch.Groups[1].Value;
                        _appIdToName[appId] = name;
                    }
                }
                catch { /* 忽略讀取錯誤的文件 */ }
            }
        }

        // 解析路徑：如果資料夾名是純數字，嘗試轉換為遊戲名
        public string ResolveFolderName(string folderName)
        {
            // 檢查是否為純數字
            if (Regex.IsMatch(folderName, @"^\d+$"))
            {
                if (_appIdToName.TryGetValue(folderName, out string? gameName))
                {
                    return gameName; // 成功解析為遊戲名
                }
                return $"未知 Steam 遊戲 (ID: {folderName})";
            }
            return folderName; // 不是純數字，返回原名
        }
    }
}
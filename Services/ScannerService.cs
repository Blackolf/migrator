using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SystemMigrator.Models;

namespace SystemMigrator.Services
{
    public class ScannerService
    {
        // 智能分類引擎
        private string GetCategory(string name, string path)
        {
            string lowerName = name.ToLower();
            if (lowerName.Contains("microsoft") || lowerName.Contains("windows") || lowerName.Contains("edge") || 
                lowerName.Contains("onedrive") || lowerName.Contains("office") || path.Contains("WindowsApps"))
                return "微軟/系統自帶";
            if (lowerName.Contains("chrome") || lowerName.Contains("firefox") || lowerName.Contains("browser"))
                return "瀏覽器";
            if (lowerName.Contains("steam") || lowerName.Contains("epic") || lowerName.Contains("origin") || lowerName.Contains("battle"))
                return "遊戲平台";
            if (lowerName.Contains("visual studio") || lowerName.Contains("code") || lowerName.Contains("git") || 
                lowerName.Contains("node") || lowerName.Contains("python") || lowerName.Contains("java"))
                return "開發工具";
            if (lowerName.Contains("photoshop") || lowerName.Contains("premiere") || lowerName.Contains("blender") || lowerName.Contains("adobe"))
                return "設計/影音";
            return "常用軟體";
        }

        public List<InstalledApp> ScanInstalledApps()
        {
            var apps = new List<InstalledApp>();
            string[] paths = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var path in paths)
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key == null) continue;

                foreach (var subKeyName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null) continue;

                    var name = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(name) || name.Contains("Update") || name.Contains("Redistributable")) continue;

                    string installLoc = subKey.GetValue("InstallLocation") as string ?? "";
                    string category = GetCategory(name, installLoc);
                    
                    // 微軟/系統自帶軟體預設不勾選
                    bool isMicrosoft = category == "微軟/系統自帶";

                    apps.Add(new InstalledApp
                    {
                        DisplayName = name,
                        InstallLocation = installLoc,
                        UninstallString = subKey.GetValue("UninstallString") as string ?? "",
                        IconPath = subKey.GetValue("DisplayIcon") as string ?? "",
                        Category = category,
                        IsSelected = !isMicrosoft 
                    });
                }
            }
            return apps.OrderBy(a => a.Category).ThenBy(a => a.DisplayName).ToList();
        }

        // 新增：傳入備份目錄，以便計算上次備份時間
        public List<BackupItem> ScanBackupItems(string backupDir)
        {
            var items = new List<BackupItem>
            {
                new() { Name = "VS Code 配置", SourcePath = @"%APPDATA%\Code\User", Category = "開發工具", IsSelected = true },
                new() { Name = "Chrome 書籤與配置", SourcePath = @"%LOCALAPPDATA%\Google\Chrome\User Data\Default", Category = "瀏覽器", IsSelected = true },
                new() { Name = "我的文檔 (My Games)", SourcePath = @"%USERPROFILE%\Documents\My Games", Category = "遊戲存檔", IsSelected = true },
                new() { Name = "Steam 存檔", SourcePath = @"%USERPROFILE%\Saved Games", Category = "遊戲存檔" }
            };
            
            foreach(var item in items)
            {
                try {
                    string p = Environment.ExpandEnvironmentVariables(item.SourcePath);
                    if(Directory.Exists(p)) {
                        item.SizeInMB = Directory.GetFiles(p, "*", SearchOption.AllDirectories)
                            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } }) / 1024 / 1024;
                    }
                    
                    // 檢查上次備份時間
                    string safeName = string.Join("_", item.Name.Split(Path.GetInvalidFileNameChars()));
                    string zipPath = Path.Combine(backupDir, $"{safeName}.zip");
                    if (File.Exists(zipPath)) {
                        item.LastBackupTime = File.GetLastWriteTime(zipPath);
                    }
                } catch {}
            }
            return items;
        }
    }
}
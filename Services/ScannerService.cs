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

                    apps.Add(new InstalledApp
                    {
                        DisplayName = name,
                        InstallLocation = subKey.GetValue("InstallLocation") as string ?? "",
                        UninstallString = subKey.GetValue("UninstallString") as string ?? "",
                        IconPath = subKey.GetValue("DisplayIcon") as string ?? ""
                    });
                }
            }
            return apps.OrderBy(a => a.DisplayName).ToList();
        }

        public List<BackupItem> ScanBackupItems()
        {
            var items = new List<BackupItem>
            {
                new() { Name = "VS Code 配置", SourcePath = @"%APPDATA%\Code\User", Category = "開發工具", IsSelected = true },
                new() { Name = "Chrome 書籤", SourcePath = @"%LOCALAPPDATA%\Google\Chrome\User Data\Default", Category = "瀏覽器" },
                new() { Name = "我的文檔 (My Games)", SourcePath = @"%USERPROFILE%\Documents\My Games", Category = "遊戲存檔", IsSelected = true },
                new() { Name = "Steam 存檔", SourcePath = @"%USERPROFILE%\Saved Games", Category = "遊戲存檔" }
            };
            
            // 計算大小 (簡化版)
            foreach(var item in items)
            {
                try {
                    string p = Environment.ExpandEnvironmentVariables(item.SourcePath);
                    if(Directory.Exists(p)) item.SizeInMB = Directory.GetFiles(p, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024 / 1024;
                } catch {}
            }
            return items;
        }
    }
}
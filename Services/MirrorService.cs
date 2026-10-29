using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SystemMigrator.Models;

namespace SystemMigrator.Services
{
    /// <summary>
    /// 下載鏡像源管理：
    /// 1. 從 Config/mirrors.json 載入來源與套件規則；
    /// 2. 依「自動／官方／國內」模式解析出候選下載 URL（含失敗回退順序）；
    /// 3. 支援 HEAD 測速挑最快來源；
    /// 4. 把使用者的模式選擇持久化到 %APPDATA%\SystemMigrator\settings.json。
    /// </summary>
    public sealed class MirrorService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
        private readonly string _configPath;
        private readonly string _settingsPath;
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _latencyCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>目前已載入的鏡像清單（載入失敗時為內建的最小回退清單）。</summary>
        public MirrorCatalog Catalog { get; private set; } = new();

        /// <summary>目前的下載來源模式。</summary>
        public MirrorMode Mode { get; private set; } = MirrorMode.Auto;

        /// <summary>設定檔是否成功載入。</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>載入失敗原因（成功時為 null）。</summary>
        public string? LoadError { get; private set; }

        public string ModeDisplay => Mode switch
        {
            MirrorMode.Official => "僅官方源",
            MirrorMode.Domestic => "僅國內鏡像",
            _ => "自動（國內優先）"
        };

        /// <summary>狀態訊息（UI 訂閱後顯示在工具列）。</summary>
        public event Action<string>? StatusChanged;

        public MirrorService(string? baseDirectory = null)
        {
            string root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory!;
            _configPath = Path.Combine(root, "Config", "mirrors.json");
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SystemMigrator",
                "settings.json");

            Load();
        }

        // ==================== 載入 / 儲存 ====================

        /// <summary>載入鏡像設定檔；失敗時建立最小回退清單並記錄 LoadError。</summary>
        public bool Load()
        {
            IsLoaded = false;
            LoadError = null;

            try
            {
                if (!File.Exists(_configPath))
                {
                    Catalog = CreateFallbackCatalog();
                    LoadError = $"找不到鏡像設定檔：{_configPath}";
                    return false;
                }

                string json = File.ReadAllText(_configPath);
                MirrorCatalog? catalog = JsonSerializer.Deserialize<MirrorCatalog>(json, JsonOptions);
                if (catalog == null || catalog.Sources.Count == 0)
                {
                    Catalog = CreateFallbackCatalog();
                    LoadError = "鏡像設定檔內容無效（sources 為空）。";
                    return false;
                }

                Catalog = catalog;
                Mode = catalog.DefaultMode;
                LoadSettings(); // 使用者選擇優先於 defaultMode
                IsLoaded = true;
                StatusChanged?.Invoke($"鏡像設定已載入：{Catalog.Packages.Count} 個套件規則，{ModeDisplay}");
                return true;
            }
            catch (Exception ex)
            {
                Catalog = CreateFallbackCatalog();
                LoadError = ex.Message;
                return false;
            }
        }

        /// <summary>切換下載來源模式並立即持久化。</summary>
        public void SetMode(MirrorMode mode)
        {
            Mode = mode;
            SaveSettings();
            StatusChanged?.Invoke($"下載來源已切換：{ModeDisplay}");
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                string json = File.ReadAllText(_settingsPath);
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null) Mode = settings.MirrorMode;
            }
            catch
            {
                // 設定檔毀損不應影響啟動，沿用 defaultMode。
            }
        }

        private void SaveSettings()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new AppSettings { MirrorMode = Mode }, JsonOptions));
            }
            catch
            {
                // 無法寫入設定時忽略，功能仍可用。
            }
        }

        private static MirrorCatalog CreateFallbackCatalog() => new()
        {
            DefaultMode = MirrorMode.Auto,
            Sources = new List<MirrorSource>
            {
                new() { Id = "official", Name = "官方源", Region = "Official", Priority = 100, Enabled = true }
            },
            Packages = new List<MirrorPackage>()
        };

        // ==================== 查詢 / 解析 ====================

        /// <summary>
        /// 以 Winget ID、短鍵或顯示名稱尋找對應的安裝規則（先精確、後模糊）。
        /// </summary>
        public MirrorPackage? FindPackage(string? wingetIdOrName)
        {
            if (string.IsNullOrWhiteSpace(wingetIdOrName)) return null;
            string key = wingetIdOrName.Trim();

            MirrorPackage? exact = Catalog.Packages.FirstOrDefault(p =>
                string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.WingetId, key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.DisplayName, key, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            if (key.Length < 4) return null;

            return Catalog.Packages.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.DisplayName) &&
                (p.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                 key.Contains(p.DisplayName, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// 依照目前模式，把一個套件解析成「依序嘗試」的候選下載來源。
        /// 自動／國內模式會把官方源排在最後作為保底。
        /// </summary>
        public IReadOnlyList<MirrorCandidate> Resolve(MirrorPackage package)
        {
            var candidates = new List<MirrorCandidate>();

            foreach (MirrorSource source in OrderedSources())
            {
                if (!source.Enabled) continue;
                if (Mode == MirrorMode.Official && !source.IsOfficial) continue;
                if (Mode == MirrorMode.Domestic && source.IsOfficial) continue;

                MirrorCandidate? candidate = BuildCandidate(package, source);
                if (candidate == null) continue;
                if (candidates.Any(c => string.Equals(c.Url, candidate.Url, StringComparison.OrdinalIgnoreCase))) continue;

                candidates.Add(candidate);
            }

            // 國內鏡像沒有此套件（或全部停用）時，仍以官方源保底，避免功能整條失效。
            if (candidates.Count == 0)
            {
                MirrorSource? official = Catalog.Sources.FirstOrDefault(s => s.IsOfficial && s.Enabled);
                if (official != null)
                {
                    MirrorCandidate? fallback = BuildCandidate(package, official);
                    if (fallback != null) candidates.Add(fallback);
                }
            }

            return candidates;
        }

        private IEnumerable<MirrorSource> OrderedSources()
        {
            IEnumerable<MirrorSource> sources = Catalog.Sources.Where(s => s.Enabled);

            return Mode switch
            {
                MirrorMode.Official => sources.OrderByDescending(s => s.IsOfficial).ThenBy(s => s.Priority),
                MirrorMode.Domestic => sources.Where(s => !s.IsOfficial).OrderBy(s => s.Priority),
                _ => sources.Where(s => !s.IsOfficial).OrderBy(s => s.Priority)
                            .Concat(sources.Where(s => s.IsOfficial).OrderBy(s => s.Priority))
            };
        }

        private MirrorCandidate? BuildCandidate(MirrorPackage package, MirrorSource source)
        {
            string? url;

            if (source.IsOfficial) url = package.OfficialUrl;
            else if (TryGetIgnoreCase(package.Mirrors, source.Id, out string? mirrorUrl)) url = mirrorUrl;
            else url = TryRewrite(package.OfficialUrl, source);

            if (string.IsNullOrWhiteSpace(url)) return null;

            return new MirrorCandidate { Source = source, Url = url.Trim() };
        }

        /// <summary>主機替換：只替換 host，保留 path/query（僅適用路徑結構一致的鏡像）。</summary>
        private static string? TryRewrite(string officialUrl, MirrorSource source)
        {
            if (source.HostRewrite == null || source.HostRewrite.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(officialUrl)) return null;
            if (!Uri.TryCreate(officialUrl, UriKind.Absolute, out Uri? uri)) return null;

            foreach (KeyValuePair<string, string> pair in source.HostRewrite)
            {
                if (!string.Equals(pair.Key, uri.Host, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;

                var builder = new UriBuilder(uri) { Host = pair.Value, Scheme = Uri.UriSchemeHttps };
                return builder.Uri.ToString();
            }

            return null;
        }

        private static bool TryGetIgnoreCase(IDictionary<string, string> dictionary, string key, out string? value)
        {
            if (dictionary.TryGetValue(key, out string? direct) && !string.IsNullOrWhiteSpace(direct))
            {
                value = direct;
                return true;
            }

            foreach (KeyValuePair<string, string> pair in dictionary)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = null;
            return false;
        }

        // ==================== 測速 ====================

        /// <summary>對單一候選來源做輕量測速（HEAD，失敗時退回 Range GET）。</summary>
        public async Task<MirrorTestResult> TestAsync(MirrorCandidate candidate, CancellationToken token)
        {
            var result = new MirrorTestResult { Candidate = candidate };
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, candidate.Url);
                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

                if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Forbidden)
                {
                    using var getRequest = new HttpRequestMessage(HttpMethod.Get, candidate.Url);
                    getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using HttpResponseMessage getResponse =
                        await _http.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, token);
                    result.StatusCode = (int)getResponse.StatusCode;
                    result.Ok = (int)getResponse.StatusCode is >= 200 and < 400;
                }
                else
                {
                    result.StatusCode = (int)response.StatusCode;
                    result.Ok = (int)response.StatusCode is >= 200 and < 400;
                }
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.Message;
            }

            stopwatch.Stop();
            result.LatencyMs = result.Ok ? (int)stopwatch.ElapsedMilliseconds : int.MaxValue;

            lock (_gate) _latencyCache[candidate.Source.Id] = result.LatencyMs;

            return result;
        }

        /// <summary>
        /// 對所有啟用來源測速（每個來源取第一個有規則的套件當代表）。
        /// </summary>
        public async Task<IReadOnlyList<MirrorTestResult>> TestAllAsync(CancellationToken token)
        {
            var representatives = new List<MirrorCandidate>();

            foreach (MirrorSource source in Catalog.Sources.Where(s => s.Enabled))
            {
                foreach (MirrorPackage package in Catalog.Packages)
                {
                    MirrorCandidate? candidate = BuildCandidate(package, source);
                    if (candidate == null) continue;
                    representatives.Add(candidate);
                    break;
                }
            }

            var results = new List<MirrorTestResult>();
            using var semaphore = new SemaphoreSlim(4);

            IEnumerable<Task> tasks = representatives.Select(async candidate =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    MirrorTestResult result = await TestAsync(candidate, token);
                    lock (_gate) results.Add(result);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // 使用者取消測速：回傳已完成的部分。
            }

            return results.OrderBy(r => r.LatencyMs).ToList();
        }

        /// <summary>
        /// 自動模式下挑選最快的可用來源；其他模式直接回傳第一個候選。
        /// </summary>
        public async Task<MirrorCandidate?> PickFastestAsync(MirrorPackage package, CancellationToken token)
        {
            IReadOnlyList<MirrorCandidate> candidates = Resolve(package);
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1 || Mode == MirrorMode.Official) return candidates[0];

            var results = new List<MirrorTestResult>();
            using var semaphore = new SemaphoreSlim(4);

            IEnumerable<Task> tasks = candidates.Select(async candidate =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    MirrorTestResult result = await TestAsync(candidate, token);
                    lock (_gate) results.Add(result);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                return candidates[0];
            }

            MirrorTestResult? fastest = results.Where(r => r.Ok).OrderBy(r => r.LatencyMs).FirstOrDefault();
            if (fastest == null)
            {
                StatusChanged?.Invoke("所有鏡像測速皆失敗，改用第一個候選來源。");
                return candidates[0];
            }

            StatusChanged?.Invoke($"測速完成，最快來源：{fastest.Candidate.Source.Name}（{fastest.LatencyMs} ms）");
            return fastest.Candidate;
        }
    }
}

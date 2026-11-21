using System.Text.Json;
using System.Collections.Concurrent;
using System.Text;

namespace 青阳AI;

// ============================================================================
// 本地大模型部署（2026-10-29 整合）
//
// 合并自原来的四个文件：
//   LocalModelCatalog.cs    —— 模型目录（≤8B、只收 Q4_K_M、全走 hf-mirror）
//   LocalModelDownloader.cs —— GGUF 下载（进度/重试/落盘）
//   OllamaLocalService.cs   —— 探测/安装/启动本机 Ollama（经 Shizuku）
//   TermuxGuide.cs          —— Termux + proot-distro 部署指引
// ============================================================================

// ────────────── 原 LocalModelCatalog.cs ──────────────
/// <summary>
/// 本地可部署大模型的目录源。
///
/// 设计要点（按用户 2026-10-04 需求）：
///  1. 自动去拉取「公开可部署的轻量模型」，**最高 8B，再高不显示**，最低 0.几 B 都可以
///  2. 只收 **Q4_K_M 量化**过的（用户写的是 Q4KM），其余量化不要
///  3. 每个模型后面写 **推荐内存**
///  4. 所有来源必须是**国内加速源** —— 统一走 hf-mirror.com（国内可直连的 HF 镜像），不直连 huggingface.co
///
/// 拉取策略（**内置优先**，保证任何时候都有可靠选择）：
///  ① 内置清单 —— Qwen / Llama / Gemma / Phi 等知名系列，链接已逐条实测可下
///  ② hf-mirror 在线拉取 —— 多关键词搜索后**逐个查仓库拿真实文件名**（否则会 404），作为补充追加
/// </summary>
public static class LocalModelCatalog
{
    /// <summary>目录里的一条模型（可部署）。</summary>
    public sealed class Item
    {
        /// <summary>展示名，如 "Qwen2.5 7B Instruct"</summary>
        public string Name { get; set; } = "";
        /// <summary>参数量（单位 B，十亿），用于排序与「≤8B」过滤</summary>
        public double Params { get; set; }
        /// <summary>量化档，固定 Q4_K_M</summary>
        public string Quant { get; set; } = "Q4_K_M";
        /// <summary>推荐内存（GB）—— 展示在模型名后面</summary>
        public double RamGB { get; set; }
        /// <summary>归属的组织/仓库名，用于拼接下载地址</summary>
        public string Repo { get; set; } = "";
        /// <summary>仓库里的文件名（GGUF）</summary>
        public string FileName { get; set; } = "";
        /// <summary>来源标记："hf-mirror"（在线拉取） / "builtin"（内置清单）</summary>
        public string Source { get; set; } = "builtin";

        /// <summary>下载地址：统一走 hf-mirror.com（国内可直连的 HuggingFace 镜像）。</summary>
        public string DownloadUrl => $"https://hf-mirror.com/{Repo}/resolve/main/{FileName}";

        /// <summary>
        /// 列表里显示的一行文本。刻意做短，保证「推荐内存」不被窄 Picker 截断：
        ///   名称里一般已含参数量（如 "Qwen2.5 0.5B Instruct"），就不再重复括号里的参数；
        ///   内存放前面并且用短词「需 XGB」，让最该看的信息第一时间可见。
        ///   形如："Qwen2.5 0.5B Instruct · 需 1GB · Q4_K_M"
        /// </summary>
        public string Display => $"{Name} · 需 {RamGB:0.#}GB · {Quant}";

        /// <summary>是否满足「≤ 8B」的硬上限。</summary>
        public bool IsAllowed => Params > 0 && Params <= 8.0;
    }

    /// <summary>非对话类关键词（embedding / 重排 / 语音 / 图像等），命中直接排除。</summary>
    private static readonly string[] DenyWords =
    {
        "embed", "rerank", "whisper", "bge-", "gte-", "t5", "clip", "stable-diffusion",
    };

    /// <summary>推荐内存档位。</summary>
    private static readonly double[] RamSteps = { 1, 2, 3, 4, 6, 8, 12, 16 };

    /// <summary>默认参数量上限（B）。用户要求「最高 8B，再高不显示」。</summary>
    public const double MaxParams = 8.0;

    /// <summary>已缓存的结果（进程内）。</summary>
    private static List<Item>? _cache;

    /// <summary>
    /// 内置清单：**全部经实测验证**（hf-mirror.com 可直连下载），Q4_K_M + ≤8B + 推荐内存。
    /// 在线拉取失败时用这份兜底，保证离线可用。
    /// 注意：注意 Qwen2.5-7B 官方仓库是**分片** GGUF（...-00001-of-00002.gguf），单文件拿不到，故不收录。
    /// </summary>
    public static List<Item> Builtin => new()
    {
        new Item { Name = "Qwen2.5 0.5B Instruct", Params = 0.5, RamGB = 1,
                   Repo = "bartowski/Qwen2.5-0.5B-Instruct-GGUF", FileName = "Qwen2.5-0.5B-Instruct-Q4_K_M.gguf" },
        new Item { Name = "Qwen2.5 0.5B Instruct（官方）", Params = 0.5, RamGB = 1,
                   Repo = "Qwen/Qwen2.5-0.5B-Instruct-GGUF", FileName = "qwen2.5-0.5b-instruct-q4_k_m.gguf" },
        new Item { Name = "Qwen2.5 1.5B Instruct", Params = 1.5, RamGB = 2,
                   Repo = "Qwen/Qwen2.5-1.5B-Instruct-GGUF", FileName = "qwen2.5-1.5b-instruct-q4_k_m.gguf" },
        new Item { Name = "Llama 3.2 1B Instruct", Params = 1, RamGB = 2,
                   Repo = "bartowski/Llama-3.2-1B-Instruct-GGUF", FileName = "Llama-3.2-1B-Instruct-Q4_K_M.gguf" },
        new Item { Name = "Qwen2.5 3B Instruct", Params = 3, RamGB = 4,
                   Repo = "Qwen/Qwen2.5-3B-Instruct-GGUF", FileName = "qwen2.5-3b-instruct-q4_k_m.gguf" },
        new Item { Name = "Llama 3.2 3B Instruct", Params = 3, RamGB = 4,
                   Repo = "bartowski/Llama-3.2-3B-Instruct-GGUF", FileName = "Llama-3.2-3B-Instruct-Q4_K_M.gguf" },
        new Item { Name = "Gemma 2 2B Instruct", Params = 2, RamGB = 3,
                   Repo = "bartowski/gemma-2-2b-it-GGUF", FileName = "gemma-2-2b-it-Q4_K_M.gguf" },
        new Item { Name = "Phi-3.5 Mini Instruct", Params = 3.8, RamGB = 4,
                   Repo = "bartowski/Phi-3.5-mini-instruct-GGUF", FileName = "Phi-3.5-mini-instruct-Q4_K_M.gguf" },
        new Item { Name = "Qwen3 4B", Params = 4, RamGB = 4,
                   Repo = "Qwen/Qwen3-4B-GGUF", FileName = "Qwen3-4B-Q4_K_M.gguf" },
        new Item { Name = "Qwen3 8B", Params = 8, RamGB = 8,
                   Repo = "Qwen/Qwen3-8B-GGUF", FileName = "Qwen3-8B-Q4_K_M.gguf" },
    };

    /// <summary>
    /// 拉取可部署模型清单（带缓存）。
    /// 策略：**内置清单（知名模型，已逐条验证可用）在前**，在线拉取的作为补充追加在后。
    /// 这样即使在线源不稳定，用户也总能拿到一批可靠的选择。
    /// </summary>
    public static async Task<List<Item>> FetchAsync(bool force = false)
    {
        if (!force && _cache is { Count: > 0 }) return _cache!;

        // ① 基准：内置清单（Qwen / Llama / Gemma / Phi 等知名系列，链接实测有效）
        var list = Builtin;

        // ② 补充：hf-mirror 在线拉取（会逐个查真实文件名，保证能下）
        try
        {
            var online = await FetchFromHfMirrorAsync().ConfigureAwait(false);
            var have = new HashSet<string>(list.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var o in online)
                if (have.Add(o.Name)) list.Add(o);
        }
        catch { }

        // ③ 去重 + 过滤 ≤8B + 按参数量升序
        list = list.Where(x => x.IsAllowed)
                   .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                   .Select(g => g.First())
                   .OrderBy(x => x.Params)
                   .ToList();

        _cache = list;
        return list;
    }

    /// <summary>从 hf-mirror.com 的 HF 兼容搜索接口拉 GGUF 模型（国内镜像）。</summary>
    private static async Task<List<Item>> FetchFromHfMirrorAsync()
    {
        var result = new List<Item>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("QingYangAI/1.0");

        // 多关键词搜索：单搜 "GGUF" 命中 Q4_K_M 的很少，分几路并行提高覆盖面
        string[] queries =
        {
            "GGUF", "Q4_K_M", "Qwen2.5-GGUF", "Qwen3-GGUF",
            "Llama-3.2-GGUF", "gemma-2-GGUF", "Phi-3.5-GGUF", "MiniCPM-GGUF",
        };

        foreach (var q in queries)
        {
            try
            {
                var url = "https://hf-mirror.com/api/models?search=" + Uri.EscapeDataString(q) +
                          "&limit=60&sort=downloads&direction=-1";
                using var resp = await http.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    try
                    {
                        var repo = m.TryGetProperty("id", out var idp) ? idp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(repo)) continue;
                        if (!repo.Contains("GGUF", StringComparison.OrdinalIgnoreCase) &&
                            !repo.Contains("gguf", StringComparison.Ordinal)) continue;
                        var shortName = repo.Contains('/') ? repo[(repo.LastIndexOf('/') + 1)..] : repo;
                        var item = FromRepoName(repo, shortName, "hf-mirror");
                        if (item != null) result.Add(item);
                    }
                    catch { }
                }
            }
            catch { /* 单个关键词失败不影响其他 */ }
        }

        // 搜索接口只给仓库名，不给文件名。逐个查仓库拿到**真实的 Q4_K_M 文件名**，
        // 否则下载会 404。只对前若干个候选做（控制请求数），其余走内置清单兜底。
        var picked = result.Where(x => x.IsAllowed)
                           .GroupBy(x => x.Repo, StringComparer.OrdinalIgnoreCase)
                           .Select(g => g.First())
                           .OrderBy(x => x.Params)
                           .Take(24)
                           .ToList();

        var resolved = new List<Item>();
        foreach (var it in picked)
        {
            try
            {
                var real = await ResolveRealFileNameAsync(http, it).ConfigureAwait(false);
                if (real != null) resolved.Add(real);
            }
            catch { }
        }
        return resolved.Count > 0 ? resolved : result;
    }

    /// <summary>查某个仓库，拿到它真正的 Q4_K_M（单文件、非分片）文件名。</summary>
    private static async Task<Item?> ResolveRealFileNameAsync(HttpClient http, Item item)
    {
        var url = "https://hf-mirror.com/api/models/" + item.Repo;
        using var resp = await http.GetAsync(url).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("siblings", out var sib) || sib.ValueKind != JsonValueKind.Array)
            return null;

        string? best = null;
        foreach (var s in sib.EnumerateArray())
        {
            var fn = s.TryGetProperty("rfilename", out var f) ? f.GetString() : null;
            if (string.IsNullOrWhiteSpace(fn)) continue;
            if (!fn.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            var lower = fn.ToLowerInvariant();
            if (!(lower.Contains("q4_k_m") || lower.Contains("q4km"))) continue;
            // 分片文件（-00001-of-00002）单独拿不了，跳过
            if (lower.Contains("of-0") || lower.Contains("-of-")) continue;
            best = fn;
            break;
        }
        if (best == null) return null;

        return new Item
        {
            Name = item.Name,
            Params = item.Params,
            RamGB = EstimateRam(item.Params),
            Source = item.Source,
            Repo = item.Repo,
            FileName = best,
        };
    }

    /// <summary>把「仓库名」解析成一条可部署项：抽参数量、判定 Q4_K_M、算推荐内存。</summary>
    private static Item? FromRepoName(string repo, string rawName, string source)
    {
        var text = (repo + " " + rawName).ToLowerInvariant();

        // 只收 Q4_K_M（用户写 Q4KM）
        if (!(text.Contains("q4_k_m") || text.Contains("q4km") || text.Contains("q4-k-m"))) return null;
        // 排除明显的非对话类（embedding/reranker/whisper/t5 等）
        if (DenyWords.Any(d => text.Contains(d))) return null;

        var p = ExtractParams(text);
        if (p <= 0 || p > MaxParams) return null;   // 最高 8B

        var pretty = PrettyName(repo);
        return new Item
        {
            Name = pretty,
            Params = p,
            RamGB = EstimateRam(p),
            Source = source,
            Repo = repo,
            FileName = GuessFileName(repo),
        };
    }

    /// <summary>从文本里抽出参数量（B）。支持 0.5b / 1b / 1.5b / 7b / 3.8b 等写法。</summary>
    private static double ExtractParams(string lower)
    {
        var m = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+(?:\.\d+)?)\s*b(?:[-_ ]|$|\b)");
        if (!m.Success) return 0;
        return double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>推荐内存：Q4_K_M 下大致 = 参数量 × 0.75GB + 1GB 余量，向上取整到常用档。</summary>
    private static double EstimateRam(double p)
    {
        var need = p * 0.75 + 1.0;
        foreach (var s in RamSteps) if (need <= s) return s;
        return Math.Ceiling(need);
    }

    private static string PrettyName(string repo)
    {
        var last = repo.Contains('/') ? repo[(repo.LastIndexOf('/') + 1)..] : repo;
        last = last.Replace("-GGUF", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("_GGUF", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("-gguf", "", StringComparison.OrdinalIgnoreCase);
        // 去掉常见的量化/仓库前缀噪声
        last = last.Replace("bartowski/", "", StringComparison.OrdinalIgnoreCase);
        if (last.Length > 34) last = last[..33] + "…";
        return last.Trim();
    }

    private static string GuessFileName(string repo)
    {
        var last = repo.Contains('/') ? repo[(repo.LastIndexOf('/') + 1)..] : repo;
        return last + "-Q4_K_M.gguf";
    }
}

// ────────────── 原 LocalModelDownloader.cs ──────────────
/// <summary>
/// 本地模型下载 / 部署服务。
///
/// 按用户 2026-10-04 需求：
///  - 「下载模型和安装模型的进度实时在**通知栏**与 **APP 内**显示」
///  - 「要有**后台拉取不中断**下载与安装的能力」
///
/// 实现方式：
///  - 下载走 <see cref="HttpClient"/> + 流式写文件，边写边回报进度
///  - Android 上前台 `Service`（<c>LocalModelService</c>）持常驻通知，进程被杀也不中断
///  - 跨平台进度事件 <see cref="ProgressChanged"/> 供 App 内 UI 订阅
///  - 任务状态持久化到 Preferences，App 重启后可续/可查
/// </summary>
public static class LocalModelDownloader
{
    /// <summary>单个任务的实时状态。</summary>
    public sealed class TaskState
    {
        public string Name { get; set; } = "";
        public string Repo { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Url { get; set; } = "";
        /// <summary>目标路径（<see cref="ExportDir"/>，外部推理服务能读到）</summary>
        public string TargetPath { get; set; } = "";
        /// <summary>是否输出到了可对外的目录（false = 只能待在 App 私有目录）</summary>
        public bool Exported { get; set; }
        public long TotalBytes { get; set; }
        public long GotBytes { get; set; }
        /// <summary>0~1，未知总长时为 0</summary>
        public double Fraction => TotalBytes > 0 ? Math.Clamp((double)GotBytes / TotalBytes, 0, 1) : 0;
        /// <summary>"下载中" / "已完成" / "失败" / "已取消"</summary>
        public string Status { get; set; } = "等待中";
        public string? Error { get; set; }
        public DateTime StartedAt { get; set; } = DateTime.Now;

        public string ProgressText
        {
            get
            {
                var gotMb = GotBytes / 1024.0 / 1024.0;
                if (TotalBytes > 0)
                {
                    var totMb = TotalBytes / 1024.0 / 1024.0;
                    return $"{Fraction * 100:0.0}%  （{gotMb:0.#}/{totMb:0.#} MB）";
                }
                return $"{gotMb:0.#} MB";
            }
        }
    }

    /// <summary>进度变化（App 内 UI 订阅这个）。</summary>
    public static event Action<TaskState>? ProgressChanged;

    /// <summary>当前活动任务（按文件名索引）。</summary>
    public static ConcurrentDictionary<string, TaskState> Active { get; } = new();

    /// <summary>已完成（可部署）的模型清单（按模型 key）。</summary>
    public static ConcurrentDictionary<string, string> Deployed { get; } = new();

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>模型落盘目录：App 私有目录 models/（不需要任何存储权限）。</summary>
    public static string ModelsDir
    {
        get
        {
            var dir = Path.Combine(FileSystem.AppDataDirectory, "models");
            try { Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>
    /// 对外导出目录：**外部推理服务能读到的地方**。
    ///
    /// 为什么需要它：App 私有目录（<see cref="ModelsDir"/>）别的程序访问不到，
    /// 而真正跑推理的是用户自己装的 Ollama / LM Studio / llama.cpp —— 它们读不到私有目录，
    /// 所以光下载到私有目录，模型其实"用不上"。
    ///
    /// 优先用 App 的外部专属目录（`Android/data/包名/files/models`，**不需要任何权限**，
    /// 且能被同机上的其他 App / Termux 访问）；拿不到就退回私有目录。
    /// </summary>
    public static string ExportDir
    {
        get
        {
            try
            {
                // MAUI 的 AppDataDirectory 在 Android 上已是 /data/data/<pkg>/files（私有）。
                // 这里主动找一个「外部专属」目录：/storage/emulated/0/Android/data/<pkg>/files/models
                var ext = TryGetExternalFilesDir();
                if (!string.IsNullOrEmpty(ext))
                {
                    var dir = Path.Combine(ext, "models");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
            }
            catch { }
            return ModelsDir;
        }
    }

    /// <summary>内部/外部目录是否一致（一致 = 没能找到可对外访问的目录）。</summary>
    public static bool ExportDirIsPrivate
    {
        get
        {
            try { return Path.GetFullPath(ExportDir) == Path.GetFullPath(ModelsDir); }
            catch { return true; }
        }
    }

    /// <summary>启动时从 Preferences 恢复「已部署」清单。</summary>
    public static void RestoreDeployed()
    {
        try
        {
            var json = Preferences.Default.Get("LocalDeployedModels", "{}");
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (map == null) return;
            foreach (var kv in map) Deployed[kv.Key] = kv.Value;
        }
        catch { }
    }

    private static void SaveDeployed()
    {
        try
        {
            var json = JsonSerializer.Serialize(Deployed.ToDictionary(k => k.Key, v => v.Value));
            Preferences.Default.Set("LocalDeployedModels", json);
        }
        catch { }
    }

    /// <summary>该模型是否已部署（本地有文件）。</summary>
    public static bool IsDeployed(string fileName)
        => Deployed.ContainsKey(fileName) && File.Exists(Deployed[fileName]);

    /// <summary>下载并部署一个模型（后台不中断）。返回最终状态。</summary>
    public static async Task<TaskState> DownloadAsync(LocalModelCatalog.Item item, CancellationToken ct = default)
    {
        var key = string.IsNullOrWhiteSpace(item.FileName) ? item.Name : item.FileName;
        var exportDir = ExportDir;
        var state = new TaskState
        {
            Name = item.Name,
            Repo = item.Repo,
            FileName = key,
            Url = item.DownloadUrl,
            TargetPath = Path.Combine(exportDir, Sanitize(key)),
            Exported = !ExportDirIsPrivate,
            Status = "下载中",
        };
        Active[key] = state;
        Notify(state);

        // Android：拉起前台 Service，保证后台不中断
        AndroidBackground.Start(key, item.Display);

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tmp = state.TargetPath + ".part";
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("QingYangAI/1.0");
            using var resp = await http.GetAsync(state.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                                        .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            state.TotalBytes = resp.Content.Headers.ContentLength ?? 0;
            Notify(state);

            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buf = new byte[81920];
                int n;
                var lastPush = DateTime.UtcNow;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    state.GotBytes += n;
                    // 节流：最多每 200ms 刷一次通知/事件，别把 UI 刷爆
                    if ((DateTime.UtcNow - lastPush).TotalMilliseconds >= 200)
                    {
                        lastPush = DateTime.UtcNow;
                        Notify(state);
                    }
                }
            }

            // 落位（覆盖旧文件）
            try { if (File.Exists(state.TargetPath)) File.Delete(state.TargetPath); } catch { }
            File.Move(tmp, state.TargetPath, true);

            state.Status = "已完成";
            Deployed[key] = state.TargetPath;
            SaveDeployed();
            Notify(state);
        }
        catch (OperationCanceledException)
        {
            state.Status = "已取消";
            Notify(state);
        }
        catch (Exception ex)
        {
            state.Status = "失败";
            state.Error = ex.Message;
            Notify(state);
        }
        finally
        {
            Gate.Release();
            AndroidBackground.Stop(key);
            // 完成后从活动表移除（保留 Deployed）
            Active.TryRemove(key, out _);
            Notify(state);
        }

        return state;
    }

    /// <summary>删除已部署的模型文件。</summary>
    public static bool Delete(string fileName)
    {
        try
        {
            if (Deployed.TryRemove(fileName, out var path) && File.Exists(path)) File.Delete(path);
            SaveDeployed();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 把已下载的模型**再复制一份到对外目录**（给先前版本下载到私有目录的用户用来补救）。
    /// 返回目标路径；失败返回 null。
    /// </summary>
    public static string? ExportExisting(string fileName)
    {
        try
        {
            if (!Deployed.TryGetValue(fileName, out var src) || !File.Exists(src)) return null;

            var dstDir = ExportDir;
            var dst = Path.Combine(dstDir, Sanitize(fileName));
            if (Path.GetFullPath(src) == Path.GetFullPath(dst)) return dst;   // 已经在对外目录

            Directory.CreateDirectory(dstDir);
            File.Copy(src, dst, overwrite: true);

            Deployed[fileName] = dst;
            SaveDeployed();
            return dst;
        }
        catch { return null; }
    }

    /// <summary>把落盘文件名还原成模型名（去掉量化后缀）。全工程统一用这个。</summary>
    public static string FileNameToModelName(string fileName)
    {
        var n = fileName;
        foreach (var suffix in new[] { "-Q4_K_M.gguf", "-q4_k_m.gguf", "_Q4_K_M.gguf", ".gguf" })
        {
            if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                n = n[..^suffix.Length];
                break;
            }
        }
        return n;
    }

    private static void Notify(TaskState state)
    {
        try { ProgressChanged?.Invoke(state); } catch { }
        AndroidBackground.Update(state);
    }

    /// <summary>
    /// 文件名清洗：只保留安全字符，彻底杜绝路径穿越。
    ///
    /// 注意：不能只依赖 Path.GetInvalidFileNameChars()：
    ///    · Android(Linux) 上 .NET 只返回 '\0' 和 '/'，**反斜杠不拦**；
    ///    · ".." 本身是合法文件名，不会被它拦掉。
    /// 所以这里显式把路径分隔符、冒号、以及 ".." 一并处理掉，
    /// 保证结果只能落在目标目录内。
    /// </summary>
    private static string Sanitize(string name)
    {
        name = (name ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

        // 显式拦路径分隔符（Linux 下反斜杠不在 InvalidFileNameChars 里）
        name = name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

        // 逐段清理 ".."，避免任何形式的目录上跳
        while (name.Contains("..")) name = name.Replace("..", "_");

        name = name.Trim().TrimStart('.');
        if (name.Length == 0) name = "model.gguf";
        return name;
    }

    /// <summary>取外部专属文件目录（Android: /storage/emulated/0/Android/data/&lt;pkg&gt;/files）。非 Android 返回空。</summary>
    private static string? TryGetExternalFilesDir()
    {
#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;
            var dir = ctx.GetExternalFilesDir(null);
            return dir?.AbsolutePath;
        }
        catch { return null; }
#else
        return null;
#endif
    }
}

/// <summary>
/// Android 后台桥：把下载任务托管到前台 Service，实现「后台拉取不中断」+ 通知栏实时进度。
/// 非 Android 平台为无操作（用 <c>#if ANDROID</c> 隔离，实现在 Platforms/Android/LocalModelService.cs）。
/// </summary>
public static partial class AndroidBackground
{
#if ANDROID
    static partial void StartImpl(string key, string title);
    static partial void UpdateImpl(string key, string title, string text, int percent, bool done);
    static partial void StopImpl(string key);
#endif

    public static void Start(string key, string title)
    {
#if ANDROID
        StartImpl(key, title);
#endif
    }

    public static void Update(LocalModelDownloader.TaskState s)
    {
#if ANDROID
        var pct = (int)Math.Round(s.Fraction * 100);
        var done = s.Status is "已完成" or "失败" or "已取消";
        var text = s.Status == "下载中" ? s.ProgressText : $"{s.Status} {(s.Error ?? "")}".Trim();
        UpdateImpl(s.FileName, s.Name, text, pct, done);
#endif
    }

    public static void Stop(string key)
    {
#if ANDROID
        StopImpl(key);
#endif
    }
}

// ────────────── 原 OllamaLocalService.cs ──────────────
/// <summary>
/// 本机 Ollama 服务的探测 / 安装 / 启动（通过 Shizuku 以 shell 身份执行）。
///
/// ── 为什么要这个 ──────────────────────────────────────────────
/// 用户 2026-10-04 需求：「下载模型前先检测有没有本地 ollama 服务，如果没有自动下载安装至本地
/// （国内加速镜像），之后下载模型时自动用 ollama 加载 gguf」。
///
/// ── 但有几个硬事实必须记住（别在别处再"发现"一遍）────────────
/// 1. **Ollama 官方没有 Android 版**。官方只发布 macOS / Linux / Windows 三平台。
///    官方发布的 `ollama-linux-arm64.tar.zst` 是给 **Linux(glibc)** 用的，
///    而 Android 是 **bionic libc**，两者 ABI 不兼容 → 直接跑必然起不来。
///    **手机上唯一能真正跑通的路径是 Termux + proot-distro**（见 <see cref="TermuxGuide"/>）。
/// 2. 所以本模块的定位是 **「能装就装，装不成/跑不起来就明确告诉用户，绝不假装成功」**。
/// 3. 检测优先于安装：很多用户其实已经在**电脑**上跑着 Ollama，只是 App 地址没填对。
///    检测到「本机没有，但局域网有配置」时，直接引导用户用现成的，而不是瞎装。
///
/// ── 权限前提 ─────────────────────────────────────────────────
/// 执行任何 shell 都要 **Shizuku 在线且已授权**（<see cref="ShizukuRunner.Available"/>）。
/// 没有 Shizuku 时本模块所有操作都会立刻返回明确失败，由 UI 层提示
/// 「需要 Shizuku 才能自动安装/启动本地服务」。
/// </summary>
public static class OllamaLocalService
{
    /// <summary>本机 Ollama 默认端口。</summary>
    public const int DefaultPort = 11434;

    /// <summary>安装目录（Shizuku 以 shell 身份运行，写到用户可访问的位置）。</summary>
    public const string InstallDir = "/data/local/tmp/ollama";

    /// <summary>
    /// 国内加速镜像前缀（GitHub 代理），按顺序尝试，最后兜底官方直连。
    ///
    /// 注意：这些是社区代理站，**可用性会变**；而且对 1.5 GB 这种大文件，
    ///    部分站会限速或限制单文件大小 —— 所以不写死单一镜像，全失败时如实列出每个源的错误。
    /// 注意：用法是「前缀拼接」：`https://ghfast.top/` + 完整的 GitHub 原始 URL。
    /// </summary>
    private static readonly string[] MirrorPrefixes =
    {
        "https://ghfast.top/",
        "https://gh-proxy.com/",
        "https://ghproxy.net/",
        "",   // 最后兜底：直连官方
    };

    /// <summary>
    /// Ollama 官方 release 版本（**写死**）。
    ///
    /// 为什么不写 `latest`：代理站对 `releases/latest/download/...` 这种重定向链的支持普遍很差
    /// （常见 404 / 403），而 `releases/download/&lt;固定版本&gt;/&lt;文件&gt;` 是最稳的路径。
    /// 官方发新版后改这里即可。
    /// </summary>
    public const string OllamaVersion = "v0.35.1";

    /// <summary>
    /// 各 ABI 对应的官方发布包名。
    ///
    /// 注意：2026 年起官方把发布格式从 `.tgz` 换成了 `.tar.zst`，文件名也变了 —— 别再写 `ollama-linux-arm64.tgz`。
    /// 注意：官方**不再提供 armv7（32 位 ARM）包**，只有 arm64 / amd64，所以 32 位设备这条路走不通。
    /// </summary>
    private static string? AssetNameFor(string abi) => abi switch
    {
        "arm64-v8a" => "ollama-linux-arm64.tar.zst",
        "x86_64" => "ollama-linux-amd64.tar.zst",
        _ => null,
    };

    /// <summary>官方 release 直链（GitHub）。</summary>
    private static string ReleaseUrlFor(string assetName)
        => $"https://github.com/ollama/ollama/releases/download/{OllamaVersion}/{assetName}";

    /// <summary>探测结果类型。</summary>
    public enum ProbeKind
    {
        /// <summary>本机已有可执行的 ollama 命令。</summary>
        CliFound,
        /// <summary>本机 11434 端口已有服务在跑（不管是不是 ollama）。</summary>
        ServerRunning,
        /// <summary>本机既没命令也没服务。</summary>
        None,
        /// <summary>没权限探测（没 Shizuku）。</summary>
        NoPermission,
    }

    /// <summary>探测结果。</summary>
    public sealed class ProbeResult
    {
        public ProbeKind Kind { get; set; }
        /// <summary>ollama 版本号（CliFound 时有值，如 "0.5.7"）。</summary>
        public string? Version { get; set; }
        /// <summary>可执行文件路径（CliFound 时有值）。</summary>
        public string? CliPath { get; set; }
        /// <summary>给用户看的一句话说明。</summary>
        public string Message { get; set; } = "";
        /// <summary>原始输出（排查用）。</summary>
        public string Raw { get; set; } = "";
    }

    /// <summary>
    /// 检测本机是否已有可用的 Ollama。
    /// 顺序：① 有没有 `ollama` 命令（PATH / 常见安装位）→ ② 11434 端口有没有服务在听。
    /// </summary>
    public static async Task<ProbeResult> DetectAsync()
    {
        if (!ShizukuRunner.Available())
        {
            return new ProbeResult
            {
                Kind = ProbeKind.NoPermission,
                Message = "未检测（没有 Shizuku 权限，无法执行本机命令）。",
            };
        }

        // ① 找 ollama 可执行文件
        //    - `command -v ollama`：PATH 里的
        //    - 依次补几个常见位置（官方脚本装到 /usr/local/bin，手动放的可能在 /data/local/tmp）
        var findCmd =
            "command -v ollama 2>/dev/null "
          + "|| for p in /usr/local/bin/ollama /usr/bin/ollama "
          + $"{InstallDir}/bin/ollama {InstallDir}/ollama /system/bin/ollama; do "
          + "[ -x \"$p\" ] && echo \"$p\" && break; done";

        var r = await ShizukuRunner.ExecuteAsync(findCmd).ConfigureAwait(false);
        var path = (r.Stdout ?? "").Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .FirstOrDefault()?.Trim();

        if (!string.IsNullOrEmpty(path))
        {
            // 拿版本号（顺便验证它真能跑起来，而不是个坏文件）
            // 加超时：glibc 不兼容的二进制有时会直接挂住而不是报错
            var v = await ShizukuRunner.ExecuteAsync($"\"{path}\" --version 2>&1", 30_000)
                .ConfigureAwait(false);
            var verText = (v.Stdout ?? "").Trim();
            if (v.Ok)
            {
                return new ProbeResult
                {
                    Kind = ProbeKind.CliFound,
                    CliPath = path,
                    Version = verText,
                    Message = $"本机已安装 Ollama：{path}",
                    Raw = verText,
                };
            }

            // 文件在但跑不起来（多半就是 glibc/bionic 不兼容）
            return new ProbeResult
            {
                Kind = ProbeKind.None,
                CliPath = path,
                Message = $"找到 {path}，但它跑不起来：{verText}",
                Raw = verText,
            };
        }

        // ② 没命令 → 看 11434 端口有没有服务
        var port = await ShizukuRunner.ExecuteAsync(
            $"curl -s -m 3 http://127.0.0.1:{DefaultPort}/api/tags 2>/dev/null "
          + $"|| wget -q -O - -T 3 http://127.0.0.1:{DefaultPort}/api/tags 2>/dev/null").ConfigureAwait(false);
        var body = (port.Stdout ?? "").Trim();
        if (body.Contains("\"models\"", StringComparison.OrdinalIgnoreCase) || body.StartsWith("{"))
        {
            return new ProbeResult
            {
                Kind = ProbeKind.ServerRunning,
                Message = $"本机 {DefaultPort} 端口已有推理服务在运行。",
                Raw = body.Length > 300 ? body[..300] : body,
            };
        }

        return new ProbeResult
        {
            Kind = ProbeKind.None,
            Message = "本机没有安装 Ollama，也没有服务在运行。",
        };
    }

    /// <summary>
    /// 下载并安装 Ollama。
    ///
    /// ── 2026-10-05 重写（真机反馈「一直下载失败」）────────────────
    /// 旧实现有三个叠加的坑，这次全修掉了：
    ///   ① **Android 系统不自带 `curl`**（只有 toybox 的 wget，很多 ROM 连 wget 都不全）
    ///      → 旧代码 `curl -L ...` 直接 `sh: curl: not found`，于是四个镜像"全部失败"，
    ///        用户看到的就是「一直下载失败」，**跟网络和镜像其实没关系**。
    ///      现在改成 **App 用自己的 HttpClient 下载**，彻底不依赖手机上的命令行工具，还能拿到真实进度。
    ///   ② **文件名过期**：官方已把发布格式从 `.tgz` 换成 `.tar.zst`，
    ///      旧的 `ollama-linux-arm64.tgz` 是 404。
    ///   ③ **`latest/download` 重定向链**被代理站普遍拒绝 → 改用固定版本路径。
    ///
    /// 注意：但类注释第 1 条依然成立：Android 上这个 Linux(glibc) 二进制**跑不起来**。
    ///    所以流程是「下载 → 解压 → **立刻验证**」，验证失败就如实报告并引导 Termux 方案，
    ///    绝不假装成功。
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(Action<string>? progress = null)
    {
        if (!ShizukuRunner.Available())
            return (false, "没有 Shizuku 权限，无法自动安装。请改用 Termux 方案，或在电脑上跑 Ollama。");

        var abi = AndroidAbi();
        var asset = AssetNameFor(abi);
        if (asset is null)
            return (false, $"官方没有为「{abi}」提供 Linux 包（只发布了 arm64 / amd64）。\n\n"
                         + "32 位 ARM 设备请改用 Termux 方案，或在电脑上跑 Ollama。");

        progress?.Invoke($"检测到架构 {abi}，开始下载 {asset}（约 1.5 GB）…");

        // ① 下载：App 自己下，绕开手机上不存在的 curl/wget
        var (local, dlErr) = await DownloadPackageAsync(asset, progress, CancellationToken.None)
            .ConfigureAwait(false);
        if (local is null)
            return (false, "下载失败。各下载源的错误：\n" + dlErr + "\n\n"
                         + "建议改用 Termux 方案，或在电脑上跑 Ollama（设置页有对应指引）。");

        var remote = $"{InstallDir}/{asset}";
        try
        {
            // ② 拷进安装目录
            //    （App 私有目录 shell 读不到，所以先落在「对外可读」目录，再由 shell cp 过去）
            progress?.Invoke("正在拷入安装目录…");
            await ShizukuRunner.ExecuteAsync($"mkdir -p {InstallDir}").ConfigureAwait(false);

            var cp = await ShizukuRunner.ExecuteAsync($"cp \"{local}\" \"{remote}\"", 600_000)
                .ConfigureAwait(false);
            if (!cp.Ok)
                return (false, $"拷入安装目录失败：{Trim(cp.Stderr ?? cp.Stdout)}");

            // ③ 解压（zstd，多重兜底 —— Android 的 tar 不一定支持 --zstd）
            progress?.Invoke("正在解压（zstd）…");
            var (xok, xmsg) = await ExtractAsync(remote).ConfigureAwait(false);
            if (!xok)
                return (false, "解压失败：" + Trim(xmsg) + "\n\n"
                             + "Android 自带的 tar / zstd 工具往往不完整。"
                             + "若反复失败，请改用 Termux 方案（Termux 里可 `apt install zstd`）。");

            // 加执行权限（tar 一般已带，保险起见）
            await ShizukuRunner.ExecuteAsync(
                $"chmod +x {InstallDir}/bin/* {InstallDir}/ollama 2>/dev/null").ConfigureAwait(false);

            // ④ 重点：立刻验证：真能跑才算成功
            progress?.Invoke("验证安装…");
            var check = await ShizukuRunner.ExecuteAsync(
                $"{InstallDir}/bin/ollama --version 2>&1 || {InstallDir}/ollama --version 2>&1",
                60_000).ConfigureAwait(false);
            var verOut = (check.Stdout ?? "").Trim();

            if (!check.Ok || verOut.Length == 0)
            {
                return (false,
                    "下载和解压都成功了，但程序**启动失败**。\n\n"
                  + $"原始输出：{Trim(verOut)}\n\n"
                  + "这是预期内的结果：Ollama 官方只提供 Linux(glibc) 版，"
                  + "而 Android 用的是 bionic libc，两者 ABI 不兼容，所以这个二进制在手机上跑不起来。\n\n"
                  + "→ 请点下面的「Termux 部署指引」，用 Termux 建一个带 glibc 的环境"
                  + "（这是手机上唯一能真正跑通的路）。");
            }

            return (true, $"安装成功：{Trim(verOut)}");
        }
        finally
        {
            // ⑤ 清理：源包 1.5 GB，留着会把手机塞满
            try { File.Delete(local); } catch { }
            await ShizukuRunner.ExecuteAsync($"rm -f \"{remote}\" \"{InstallDir}/ollama.tar\"")
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 用 App 自身的 <see cref="HttpClient"/> 下载发布包到对外目录。
    ///
    /// 为什么不用手机上的 curl/wget：**Android 系统默认不带 `curl`**，
    /// 而 toybox 的 wget 在很多 ROM 上也不完整、且未必支持 https。
    /// App 自己有网络权限，直接下最可靠，还能拿到真实进度。
    ///
    /// 返回落盘路径；全部源都失败时返回 null + 各源错误明细。
    /// </summary>
    private static async Task<(string? Path, string Error)> DownloadPackageAsync(
        string assetName, Action<string>? progress, CancellationToken ct)
    {
        var official = ReleaseUrlFor(assetName);

        // 依次尝试：国内加速镜像 → 官方直连（顺序见 MirrorPrefixes）
        var urls = new List<string>();
        foreach (var p in MirrorPrefixes) urls.Add(p + official);

        var dir = LocalModelDownloader.ExportDir;   // 对外可读目录（shell 才能 cp 走）
        var dst = Path.Combine(dir, assetName);
        var tmp = dst + ".part";

        var errors = new List<string>();
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();

            var shown = url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
                ? "官方直连"
                : new Uri(url).Host;
            try
            {
                progress?.Invoke($"正在下载（{shown}）…");
                using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("QingYangAI/1.0");

                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"{shown}: HTTP {(int)resp.StatusCode}");
                    continue;
                }

                var total = resp.Content.Headers.ContentLength ?? 0;
                long got = 0;
                var lastPush = DateTime.UtcNow;

                await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write,
                                                     FileShare.None, 81920, true))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                        got += n;
                        // 节流：最多每 300ms 刷一次 UI，别把界面刷爆
                        if ((DateTime.UtcNow - lastPush).TotalMilliseconds >= 300)
                        {
                            lastPush = DateTime.UtcNow;
                            var mb = got / 1024.0 / 1024.0;
                            progress?.Invoke(total > 0
                                ? $"正在下载（{shown}）{got * 100.0 / total:0.0}%  "
                                + $"{mb:0.#}/{total / 1024.0 / 1024.0:0.#} MB"
                                : $"正在下载（{shown}）{mb:0.#} MB");
                        }
                    }
                }

                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst, true);
                return (dst, "");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errors.Add($"{shown}: {ex.Message}");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        return (null, errors.Count > 0 ? string.Join("\n", errors) : "没有可用的下载源");
    }

    /// <summary>
    /// 解压 `.tar.zst`。Android 自带的 tar 不一定支持 zstd，所以依序试三种方式，
    /// 全失败时把每条的真实输出拼起来返回（便于排查）。
    /// </summary>
    private static async Task<(bool Ok, string Msg)> ExtractAsync(string archive)
    {
        var attempts = new[]
        {
            // ① GNU tar 的 --zstd
            $"cd {InstallDir} && tar --zstd -xf \"{archive}\" 2>&1",
            // ② zstd 先解成 .tar，再 tar 解包
            $"cd {InstallDir} && ( zstd -d -f \"{archive}\" -o ollama.tar && tar -xf ollama.tar ) 2>&1",
            // ③ unzstd（zstd 的别名，部分环境只装了它）
            $"cd {InstallDir} && ( unzstd -f \"{archive}\" -o ollama.tar && tar -xf ollama.tar ) 2>&1",
        };

        var msgs = new List<string>();
        foreach (var cmd in attempts)
        {
            // 1.5 GB 解压在手机上可能要几分钟，给足 30 分钟
            var r = await ShizukuRunner.ExecuteAsync(cmd, 1_800_000).ConfigureAwait(false);
            if (r.Ok) return (true, "");
            msgs.Add(Trim(r.Stderr ?? r.Stdout));
        }

        return (false, string.Join(" | ", msgs));
    }

    /// <summary>启动 Ollama 服务（后台常驻，监听所有网卡，方便手机/局域网访问）。</summary>
    public static async Task<(bool Ok, string Message)> StartServerAsync()
    {
        if (!ShizukuRunner.Available())
            return (false, "没有 Shizuku 权限，无法启动服务。");

        // 先探测 11434 有没有已经在跑
        var running = await ShizukuRunner.ExecuteAsync(
            $"curl -s -m 2 http://127.0.0.1:{DefaultPort}/api/tags 2>/dev/null").ConfigureAwait(false);
        if ((running.Stdout ?? "").TrimStart().StartsWith("{"))
            return (true, "服务已经在运行。");

        // 找可执行文件
        var find = await ShizukuRunner.ExecuteAsync(
            $"command -v ollama 2>/dev/null || ls {InstallDir}/bin/ollama 2>/dev/null || ls {InstallDir}/ollama 2>/dev/null")
            .ConfigureAwait(false);
        var bin = (find.Stdout ?? "").Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                     .FirstOrDefault()?.Trim();
        if (string.IsNullOrEmpty(bin))
            return (false, "找不到 ollama 可执行文件，请先安装。");

        // 后台起服务：OLLAMA_HOST=0.0.0.0 让局域网可达；nohup + & 脱离
        var start = await ShizukuRunner.ExecuteAsync(
            $"OLLAMA_HOST=0.0.0.0:{DefaultPort} nohup \"{bin}\" serve >/dev/null 2>&1 & echo started")
            .ConfigureAwait(false);

        // 等几秒再探一次
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(1500).ConfigureAwait(false);
            var probe = await ShizukuRunner.ExecuteAsync(
                $"curl -s -m 2 http://127.0.0.1:{DefaultPort}/api/tags 2>/dev/null").ConfigureAwait(false);
            if ((probe.Stdout ?? "").TrimStart().StartsWith("{"))
                return (true, "服务已启动（监听 0.0.0.0:11434）。");
        }

        return (false, $"启动命令已发出，但 {DefaultPort} 端口没起来。"
                     + $"输出：{Trim(start.Stdout ?? start.Stderr)}");
    }

    /// <summary>
    /// 让 Ollama 加载一个本地 .gguf 文件（通过 Modelfile）。
    /// 对应需求原话「之后下载模型时自动用 ollama 加载 gguf」。
    /// </summary>
    public static async Task<(bool Ok, string Message)> LoadGgufAsync(string ggufPath, string modelName)
    {
        if (!ShizukuRunner.Available())
            return (false, "没有 Shizuku 权限，无法自动加载模型。");

        if (string.IsNullOrWhiteSpace(ggufPath) || !ggufPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            return (false, "模型文件路径不合法（需要 .gguf 文件）。");

        // 找一个可写的工作目录放 Modelfile
        var work = InstallDir;
        var mf = $"{work}/Modelfile.{Sanitize(modelName)}";
        // Modelfile 内容：FROM <绝对路径>
        var script = $"mkdir -p {work} && printf 'FROM %s\\n' \"{ggufPath}\" > \"{mf}\" && cat \"{mf}\"";
        var wrote = await ShizukuRunner.ExecuteAsync(script).ConfigureAwait(false);
        if (!wrote.Ok)
            return (false, $"写 Modelfile 失败：{Trim(wrote.Stderr ?? wrote.Stdout)}");

        // 先解析出 ollama 可执行文件的真实路径（不能在引号里塞命令替换）
        var binRes = await ShizukuRunner.ExecuteAsync(
            $"command -v ollama 2>/dev/null || echo {InstallDir}/bin/ollama").ConfigureAwait(false);
        var bin = (binRes.Stdout ?? "").Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                       .FirstOrDefault()?.Trim() ?? "";
        if (string.IsNullOrEmpty(bin))
            return (false, "找不到 ollama 可执行文件，请先安装。");

        var create = await ShizukuRunner.ExecuteAsync(
            $"\"{bin}\" create \"{modelName}\" -f \"{mf}\" 2>&1", 600_000).ConfigureAwait(false);

        if (create.Ok)
            return (true, $"已加载为模型「{modelName}」，现在可以在聊天里选它。");

        return (false, $"加载失败：{Trim(create.Stderr ?? create.Stdout)}\n\n"
                     + "可能原因：服务没在跑、磁盘空间不足、或该 gguf 与 Ollama 版本不兼容。");
    }

    /// <summary>当前手机的 ABI（供下载选架构）。</summary>
    public static string AndroidAbi()
    {
#if ANDROID
        try
        {
            var abis = global::Android.OS.Build.SupportedAbis;
            if (abis is { Count: > 0 }) return abis[0];
        }
        catch { }
#endif
        return "arm64-v8a";
    }

    // ollama 可执行文件查找片段（供 LoadGguf 复用）
    private static string Sanitize(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString();
    }

    private static string Trim(string? s)
    {
        s = (s ?? "").Trim();
        return s.Length switch { 0 => "(无输出)", > 400 => s[..400] + "…", _ => s };
    }
}

// ────────────── 原 TermuxGuide.cs ──────────────
/// <summary>
/// Termux 部署指引 —— **手机上唯一能真正跑通 Ollama 的路径**。
///
/// ── 为什么必须走 Termux ────────────────────────────────────────
/// Ollama 官方只提供 Linux(glibc) 二进制，而 Android 用的是 bionic libc，
/// 直接扔进 /data/local/tmp 跑必然失败（详见 <see cref="OllamaLocalService"/> 类注释）。
/// 唯一办法是在 Android 上**造一个 glibc 环境** —— Termux 的 `proot-distro`
/// 可以装一个完整的 Ubuntu（真 glibc），在里面装 Ollama 就能跑。
///
/// ── 关键：网络是共享的（整个方案成立的前提）────────────────────
/// proot **不做网络命名空间隔离**，所以 Ubuntu 里 `ollama serve` 监听的 127.0.0.1:11434
/// **就是手机的 127.0.0.1:11434** —— 本 App 直接填 <see cref="ServiceUrl"/> 就能连上。
///
/// ── 前提 ──────────────────────────────────────────────────────
/// 用户需自行安装 Termux。本 App **不代装也代不了**：Termux 得从 F-Droid / GitHub 获取，
/// 应用商店里的版本多已停止更新，而且装 APK 本身要用户手动确认。
/// </summary>
public static class TermuxGuide
{
    /// <summary>Termux 的包名。</summary>
    public const string PackageName = "com.termux";

    /// <summary>Termux 官方获取渠道（应用商店里的版本多已停更，别用）。</summary>
    public const string DownloadHint = "F-Droid（f-droid.org）或 GitHub（github.com/termux/termux-app）";

    /// <summary>
    /// Termux 是否已安装。
    ///
    /// 用 <c>GetLaunchIntentForPackage</c> 判断（比 GetPackageInfo 省事，
    /// 也不用处理 Android 13+ 的 PackageInfoFlags 重载差异）。
    ///
    /// 注意：Android 11+ 有「包可见性」限制：**必须在 AndroidManifest 的 &lt;queries&gt; 里声明 com.termux**，
    ///    否则即使装了这里也会返回 false。
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
#if ANDROID
            try
            {
                var ctx = global::Android.App.Application.Context;
                return ctx.PackageManager?.GetLaunchIntentForPackage(PackageName) is not null;
            }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>拉起 Termux。返回 false 表示没装 / 拉不起来。</summary>
    public static bool Launch()
    {
#if ANDROID
        try
        {
            var ctx = global::Android.App.Application.Context;
            var intent = ctx.PackageManager?.GetLaunchIntentForPackage(PackageName);
            if (intent is null) return false;
            intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
            ctx.StartActivity(intent);
            return true;
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>第 1 步（Termux 里）：装 proot-distro 并装一个 Ubuntu。</summary>
    public const string Cmd1 = "pkg update -y && pkg install -y proot-distro && proot-distro install ubuntu";

    /// <summary>第 2 步（Termux 里）：进入 Ubuntu（提示符会变成 root@localhost）。</summary>
    public const string Cmd2 = "proot-distro login ubuntu";

    /// <summary>第 3 步（Ubuntu 内）：装依赖 + 用官方脚本装 Ollama（这一步最慢）。</summary>
    public const string Cmd3 = "apt update && apt install -y curl zstd && curl -fsSL https://ollama.com/install.sh | sh";

    /// <summary>第 4 步（Ubuntu 内）：起服务，监听所有网卡。</summary>
    public const string Cmd4 = "OLLAMA_HOST=0.0.0.0:11434 ollama serve &";

    /// <summary>第 3 步卡住时的备用方案：直接从国内镜像拉包手动装。</summary>
    public static string Cmd3Alt =>
        "apt update && apt install -y curl zstd && "
      + $"curl -fL {OllamaMirror} -o /tmp/ollama.tar.zst && "
      + "mkdir -p /usr/local && tar --zstd -xf /tmp/ollama.tar.zst -C /usr/local && "
      + "ln -sf /usr/local/bin/ollama /usr/bin/ollama && ollama --version";

    /// <summary>走国内加速镜像的官方包地址。</summary>
    private static string OllamaMirror =>
        "https://ghfast.top/https://github.com/ollama/ollama/releases/download/"
      + $"{OllamaLocalService.OllamaVersion}/ollama-linux-arm64.tar.zst";

    /// <summary>本 App 该填的服务地址（proot 不隔离网络，Ubuntu 的 127.0.0.1 就是手机的 127.0.0.1）。</summary>
    public static string ServiceUrl => $"http://127.0.0.1:{OllamaLocalService.DefaultPort}";

    /// <summary>给用户看的分步说明（Label 不渲染 markdown，所以这里不用星号）。</summary>
    public static string Steps { get; } =
        "在手机上真正把模型跑起来，需要用 Termux 造一个 glibc 环境"
      + "（Android 自带的是 bionic，官方 Linux 版 Ollama 在里面跑不起来）。\n\n"
      + "① 先装 Termux：" + DownloadHint + "\n"
      + "    装好后建议先在 Termux 里执行 termux-wake-lock，防止被系统休眠杀掉。\n\n"
      + "② 在 Termux 里粘贴执行：\n    " + Cmd1 + "\n\n"
      + "③ 进入 Ubuntu 环境：\n    " + Cmd2 + "\n\n"
      + "④ 在 Ubuntu 里装 Ollama（这一步较慢，耐心等）：\n    " + Cmd3 + "\n"
      + "    若卡住，改用国内镜像的手动安装命令（见「复制全部命令」里的备用条）。\n\n"
      + "⑤ 启动服务：\n    " + Cmd4 + "\n\n"
      + "⑥ 回到本页，把服务地址填成：" + ServiceUrl + "\n"
      + "    然后点「拉取模型名」确认能连上。\n\n"
      + "说明：proot 不做网络隔离，所以 Ubuntu 里的 127.0.0.1:11434 就是手机的 127.0.0.1:11434，"
      + "本 App 可以直接连。";

    /// <summary>
    /// 一键复制的完整命令。
    /// 注意：提示用户「按步骤逐段粘贴，不要一次全贴」—— 第 2 步会进入交互式 shell，后面的命令不会自动接着跑。
    /// </summary>
    public static string CopyText { get; } =
        "# 第 1 步（Termux）\n" + Cmd1 + "\n\n"
      + "# 第 2 步（Termux，执行后会进入 Ubuntu）\n" + Cmd2 + "\n\n"
      + "# 第 3 步（Ubuntu 内）\n" + Cmd3 + "\n"
      + "# 若第 3 步卡住，改用这条（走国内镜像手动装）\n" + Cmd3Alt + "\n\n"
      + "# 第 4 步（Ubuntu 内）\n" + Cmd4 + "\n";
}


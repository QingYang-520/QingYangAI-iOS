using System;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls;

namespace 青阳AI;

// ============================================================================
// 核心小工具（2026-10-29 整合）
//
// 合并自原来的六个文件：
//   InstructionParser.cs —— 隐藏指令协议解析
//   TokenUsageStats.cs   —— token 用量统计与跨厂商字段映射
//   ApiErrors.cs         —— API 错误码人话化
//   ReminderParser.cs    —— 中文时间解析（承诺提醒用）
//   BackupService.cs     —— 备份/恢复
//   ChatJump.cs          —— 跨页消息定位通道
// ============================================================================

// ────────────── 原 InstructionParser.cs ──────────────
/// <summary>
/// 隐藏指令协议解析：{cmd:"..."} / {api:"..."} / {img:"..."} / {mood:"..."} / {avatar:"..."} / {say:"..."}。
/// - Extract：从整段文本提取某个标记的所有值；
/// - MatchLen：显示层过滤用——判断缓冲区某位置是否是指令开头（指令不打字上屏，只做副作用）。
/// </summary>
public static class InstructionParser
{
    /// <summary>全部指令标记（按最长匹配优先无需排序，逐个全字比对）。</summary>
    public static readonly string[] Markers =
    {
        "{cmd:\"", "{api:\"", "{img:\"", "{mood:\"", "{avatar:\"", "{say:\"",
        "{read:\"", "{write:\"", "{append:\"", "{edit:\"", "{ls:\"", "{del:\"",
        "{download:\"", "{browse:\"", "{web:\"",
        // {remind:"时间|内容"}：承诺提醒（用户说"提醒我"时她答应下来的事）。
        // 与其它业务指令并列放在 {done:" 之前，保持"动作类指令"和"收尾类指令"的分组习惯。
        "{remind:\"",
        "{done:\"", "{todo:\"", "{plan:\"",
    };

    /// <summary>提取 content 里所有 {marker:"值"} 的值。</summary>
    public static List<string> Extract(string content, string marker)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(content)) return results;

        var token = "{marker:\"".Replace("marker", marker);
        int idx = 0;
        while (idx < content.Length)
        {
            int start = content.IndexOf(token, idx, StringComparison.Ordinal);
            if (start < 0) break;
            int valueStart = start + token.Length;
            int valueEnd = content.IndexOf('"', valueStart);
            if (valueEnd < 0) break;
            var value = content[valueStart..valueEnd].Trim();
            if (!string.IsNullOrEmpty(value)) results.Add(value);
            idx = valueEnd + 1;
        }
        return results;
    }

    /// <summary>
    /// 把文本里**所有指令 token**（`{marker:"值"}`）整段删掉，只留自然语言。
    ///
    /// 指令只做副作用，不该出现在聊天气泡里、也不该被存进历史。
    /// 注意：必须在**管线跑完之后**才调用 —— 管线（{cmd}/{api}/{img}/{browse}/{download}）
    /// 还得从原文里把参数抠出来，提前删了它们就取不到地址了。
    /// </summary>
    public static string RemoveInstructions(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var m in Markers)
        {
            int idx = 0;
            while (idx < text.Length)
            {
                int start = text.IndexOf(m, idx, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf('"', start + m.Length);
                if (end < 0) break;
                int close = text.IndexOf('}', end);
                if (close < 0) break;
                text = text.Remove(start, close - start + 1);
                idx = start;
            }
        }
        return text.Trim();
    }

    /// <summary>
    /// 显示层过滤：buf 的 pos 处是否指令开头。
    /// 返回 >0 = 命中（值即标记长度，显示层应跳过整段 token）；
    /// -1 = 流未收完且尾部疑似指令前缀，等更多字符再判断；0 = 不是指令。
    /// </summary>
    public static int MatchLen(System.Text.StringBuilder buf, int pos, bool readDone)
    {
        foreach (var m in Markers)
        {
            int avail = buf.Length - pos;
            if (avail >= m.Length)
            {
                bool eq = true;
                for (int j = 0; j < m.Length; j++)
                {
                    if (buf[pos + j] != m[j]) { eq = false; break; }
                }
                if (eq) return m.Length;
            }
            else
            {
                bool prefix = true;
                for (int j = 0; j < avail; j++)
                {
                    if (buf[pos + j] != m[j]) { prefix = false; break; }
                }
                if (prefix && !readDone) return -1;
            }
        }
        return 0;
    }
}

// ────────────── 原 TokenUsageStats.cs ──────────────
/// <summary>
/// 与厂商无关的 usage 内部模型：不管上游字段叫什么，最终都收敛成这两个数。
/// </summary>
public sealed class TokenUsage
{
    /// <summary>本次请求的 prompt（输入）token 总数。</summary>
    public int PromptTokens { get; set; }

    /// <summary>其中命中缓存的 token 数。上游没有缓存字段时为 0，不抛异常。</summary>
    public int CachedTokens { get; set; }

    /// <summary>缓存命中率（百分比，保留 1 位小数；prompt 为 0 时返回 0）。</summary>
    public double HitRate
    {
        get
        {
            if (PromptTokens <= 0) return 0.0;
            double rate = (double)CachedTokens / (double)PromptTokens * 100.0;
            return Math.Round(rate, 1, MidpointRounding.AwayFromZero);
        }
    }
}

/// <summary>
/// 多厂商 usage 字段映射层。
/// 覆盖 OpenAI / DeepSeek / 智谱 / Moonshot / Anthropic / Gemini / 火山等常见结构：
/// prompt 字段、cached 字段、嵌套 details 容器全部做别名匹配（忽略大小写与下划线）。
/// 解析失败或缺字段一律返回 0，绝不抛异常。
/// </summary>
public static class TokenUsageMapper
{
    // prompt（输入）token 的别名
    private static readonly string[] PromptKeys =
    {
        "prompt_tokens", "input_tokens", "prompt_token_count", "input_token_count",
        "promptTokens", "inputTokens", "prompt_token", "input_token", "total_tokens"
    };

    // 缓存命中 token 的别名
    private static readonly string[] CachedKeys =
    {
        "cached_tokens", "cache_tokens", "prompt_cache_hit_tokens", "cache_read_input_tokens",
        "cachedTokens", "cacheTokens", "promptCacheHitTokens", "cachedContentTokenCount",
        "cached_content_token_count", "cached_prompt_tokens", "cache_hit_tokens"
    };

    // 缓存未命中 token 的别名（用于 prompt 缺失时反推 prompt = 命中 + 未命中）
    private static readonly string[] MissKeys =
    {
        "prompt_cache_miss_tokens", "uncached_tokens", "cache_miss_tokens",
        "uncachedTokens", "promptCacheMissTokens", "cache_creation_input_tokens"
    };

    // 嵌套容器：OpenAI 的 prompt_tokens_details、DeepSeek 的 prompt_cache_hit_token_details 等
    private static readonly string[] DetailKeys =
    {
        "prompt_tokens_details", "prompt_cache_hit_token_details", "input_tokens_details",
        "cache_tokens_details", "promptTokensDetails", "inputTokenDetails", "usage_details"
    };

    /// <summary>从完整响应 JSON 字符串里解析 usage（自动定位根节点的 usage 字段）。</summary>
    public static TokenUsage FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TokenUsage();
        try
        {
            using var doc = JsonDocument.Parse(json);
            return FromElement(doc.RootElement);
        }
        catch
        {
            return new TokenUsage();
        }
    }

    /// <summary>从 JsonElement 解析 usage。传 null / 非对象 / 无 usage 都返回空模型。</summary>
    public static TokenUsage FromElement(JsonElement? element)
    {
        if (element == null) return new TokenUsage();
        return FromElement(element.Value);
    }

    /// <summary>从 JsonElement 解析 usage。根节点带 usage 字段时自动下钻。</summary>
    public static TokenUsage FromElement(JsonElement element)
    {
        try
        {
            if (element.ValueKind != JsonValueKind.Object) return new TokenUsage();

            // 传进来的是完整响应体时，下钻到 usage 节点
            if (TryGet(element, "usage", out var inner) && inner.ValueKind == JsonValueKind.Object)
                element = inner;

            int prompt = ReadFirst(element, PromptKeys);
            int cached = ReadFirst(element, CachedKeys);

            // 顶层没找到 cached，去嵌套 details 里再找一轮
            if (cached <= 0)
            {
                foreach (var key in DetailKeys)
                {
                    if (!TryGet(element, key, out var detail) || detail.ValueKind != JsonValueKind.Object) continue;
                    cached = ReadFirst(detail, CachedKeys);
                    if (cached > 0) break;
                }
            }

            int miss = ReadFirst(element, MissKeys);

            // Anthropic 风格：input_tokens 不含 cache_read / cache_creation，需要加回来
            if (HasAny(element, "cache_read_input_tokens") && prompt > 0)
                prompt = prompt + cached;
            else if (prompt <= 0)
                prompt = cached + miss;   // DeepSeek 风格：只有命中/未命中，反推 prompt

            if (prompt < 0) prompt = 0;
            if (cached < 0) cached = 0;
            if (cached > prompt) cached = prompt;

            return new TokenUsage { PromptTokens = prompt, CachedTokens = cached };
        }
        catch
        {
            return new TokenUsage();
        }
    }

    /// <summary>按别名顺序取第一个存在的字段值（找不到返回 0）。</summary>
    private static int ReadFirst(JsonElement obj, string[] keys)
    {
        foreach (var key in keys)
        {
            if (TryGet(obj, key, out var value))
            {
                int num = ToInt(value);
                if (num > 0) return num;
            }
        }
        return 0;
    }

    /// <summary>是否存在任一别名字段。</summary>
    private static bool HasAny(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
            if (TryGet(obj, key, out _)) return true;
        return false;
    }

    /// <summary>取属性：先精确匹配，再按「忽略大小写 + 忽略下划线/连字符」匹配。</summary>
    private static bool TryGet(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (obj.TryGetProperty(key, out value)) return true;

        string target = Normalize(key);
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(Normalize(prop.Name), target, StringComparison.Ordinal))
            {
                value = prop.Value;
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string name)
    {
        var buffer = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (ch == '_' || ch == '-' || ch == ' ') continue;
            buffer.Append(char.ToLowerInvariant(ch));
        }
        return buffer.ToString();
    }

    /// <summary>容错取值：数字、数字字符串都能读，读不出就 0。</summary>
    private static int ToInt(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var i)) return i < 0 ? 0 : i;
            if (value.TryGetDouble(out var d)) return d <= 0 ? 0 : (int)Math.Round(d);
            return 0;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var s))
                return s < 0 ? 0 : s;
        }
        return 0;
    }
}

/// <summary>
/// 统计中心：本次 usage + 全局累计 usage，实现 INotifyPropertyChanged，改完自动刷新 UI。
/// 累计数持久化在 Preferences 里，重启应用不清零。
/// </summary>
public sealed class UsageStats : INotifyPropertyChanged
{
    private const string PrefTotalPrompt = "UsageStats.TotalPromptTokens";
    private const string PrefTotalCached = "UsageStats.TotalCachedTokens";

    public static UsageStats Current { get; } = new UsageStats();

    private int _lastPromptTokens;
    private int _lastCachedTokens;
    private int _totalPromptTokens;
    private int _totalCachedTokens;

    private UsageStats()
    {
        _totalPromptTokens = Preferences.Get(PrefTotalPrompt, 0);
        _totalCachedTokens = Preferences.Get(PrefTotalCached, 0);
    }

    // ---------- 本次 ----------
    public int LastPromptTokens
    {
        get => _lastPromptTokens;
        private set
        {
            if (_lastPromptTokens == value) return;
            _lastPromptTokens = value;
            OnPropertyChanged(nameof(LastPromptTokens));
            OnPropertyChanged(nameof(LastHitRate));
            OnPropertyChanged(nameof(LastText));
        }
    }

    public int LastCachedTokens
    {
        get => _lastCachedTokens;
        private set
        {
            if (_lastCachedTokens == value) return;
            _lastCachedTokens = value;
            OnPropertyChanged(nameof(LastCachedTokens));
            OnPropertyChanged(nameof(LastHitRate));
            OnPropertyChanged(nameof(LastText));
        }
    }

    /// <summary>本次缓存命中率（百分比，1 位小数）。</summary>
    public double LastHitRate => CalcRate(LastPromptTokens, LastCachedTokens);

    /// <summary>本次一行文本，直接给 UI 绑。</summary>
    public string LastText =>
        "本轮 " + FormatCount(LastPromptTokens) + " · 缓存 " + FormatCount(LastCachedTokens) +
        " · " + FormatRate(LastHitRate);

    // ---------- 全局累计 ----------
    public int TotalPromptTokens
    {
        get => _totalPromptTokens;
        private set
        {
            if (_totalPromptTokens == value) return;
            _totalPromptTokens = value;
            OnPropertyChanged(nameof(TotalPromptTokens));
            OnPropertyChanged(nameof(TotalHitRate));
            OnPropertyChanged(nameof(TotalText));
        }
    }

    public int TotalCachedTokens
    {
        get => _totalCachedTokens;
        private set
        {
            if (_totalCachedTokens == value) return;
            _totalCachedTokens = value;
            OnPropertyChanged(nameof(TotalCachedTokens));
            OnPropertyChanged(nameof(TotalHitRate));
            OnPropertyChanged(nameof(TotalText));
        }
    }

    /// <summary>全局缓存命中率（百分比，1 位小数）。</summary>
    public double TotalHitRate => CalcRate(TotalPromptTokens, TotalCachedTokens);

    /// <summary>累计一行文本，直接给 UI 绑。</summary>
    public string TotalText =>
        "累计 " + FormatCount(TotalPromptTokens) + " · 缓存 " + FormatCount(TotalCachedTokens) +
        " · " + FormatRate(TotalHitRate);

    // ---------- 更新 & 累加 ----------

    /// <summary>拿到一次模型返回的 usage：更新【本次】，并累加进【全局累计】。</summary>
    public void Apply(TokenUsage? usage)
    {
        if (usage == null) return;

        int prompt = usage.PromptTokens < 0 ? 0 : usage.PromptTokens;
        int cached = usage.CachedTokens < 0 ? 0 : usage.CachedTokens;
        if (cached > prompt) cached = prompt;
        if (prompt <= 0 && cached <= 0) return;   // 该次没有 usage 数据，不刷新

        void Update()
        {
            LastPromptTokens = prompt;
            LastCachedTokens = cached;
            TotalPromptTokens = TotalPromptTokens + prompt;
            TotalCachedTokens = TotalCachedTokens + cached;

            Preferences.Set(PrefTotalPrompt, TotalPromptTokens);
            Preferences.Set(PrefTotalCached, TotalCachedTokens);
        }

        if (MainThread.IsMainThread) Update();
        else MainThread.BeginInvokeOnMainThread(Update);
    }

    // 注：原来这里有两个从 JSON 直接累加的便捷方法（ApplyFromJson / ApplyFromElement），
    //     全项目没人调用，已删（瘦身）。要用的话直接调 Apply(TokenUsageMapper.FromJson(...)) 即可。

    /// <summary>清空累计（设置页可挂个重置入口）。</summary>
    public void ResetTotals()
    {
        void Update()
        {
            TotalPromptTokens = 0;
            TotalCachedTokens = 0;
            Preferences.Remove(PrefTotalPrompt);
            Preferences.Remove(PrefTotalCached);
        }

        if (MainThread.IsMainThread) Update();
        else MainThread.BeginInvokeOnMainThread(Update);
    }

    // ---------- 工具 ----------
    private static double CalcRate(int prompt, int cached)
    {
        if (prompt <= 0) return 0.0;
        return Math.Round((double)cached / (double)prompt * 100.0, 1, MidpointRounding.AwayFromZero);
    }

    private static string FormatRate(double rate) =>
        rate.ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string FormatCount(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ────────────── 原 ApiErrors.cs ──────────────
/// <summary>
/// 网络/接口错误的**统一处理**：错误翻人话、判断值不值得重试、把服务商的错误正文捞出来。
///
/// 抽成独立类是因为**两条路都要用**：
///   · 普通聊天（`ChatPage`）
///   · Agent 循环（`AgentLoopService`）
/// 以前这些逻辑只写在 ChatPage 里，Agent 那条路啥都没有 —— 出错就直接把整个循环打断。
/// </summary>
public static class ApiErrors
{
    // ───────────── 错误翻人话 ─────────────

    /// <summary>把异常翻成用户能看懂的话。</summary>
    public static string Friendly(Exception ex)
    {
        // ① 有 HTTP 状态码：按码给具体原因（比笼统的"网络连接失败"有用得多）
        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            int code = (int)httpEx.StatusCode.Value;
            var baseMsg = code switch
            {
                400 => "服务商拒绝了这次请求（400）。多半是模型名填错，或这个模型不支持当前参数。",
                401 => "API Key 无效，请检查设置",
                403 => "服务商拒绝了这次请求（403）：可能是 Key 没权限、余额不足，或模型没开通。",
                404 => "接口地址不对（404）。设置里的 URL 要填完整端点，比如 https://…/v1/chat/completions",
                429 => "被服务商限流了（429）。\n"
                     + "注意：不一定是总额度用完 —— 很多服务商还有「滚动时间窗」限流"
                     + "（比如 5 小时内用满，就得等窗口重置）。去控制台看「时间窗 / 5h 窗口」那一栏；"
                     + "那一栏满了就只能等它重置，或者换个模型。\n"
                     + "如果窗口也没满，那就是发得太快，稍等一会儿再发。",
                >= 500 => $"服务商那边出错了（{code}），稍后再试。",
                _ => $"服务商返回 HTTP {code}"
            };

            // 服务商正文里往往写了真正的原因（比如 insufficient_quota = 额度用完），
            // 带上它比只报个状态码有用得多
            var detail = httpEx.Message ?? "";
            if (!string.IsNullOrWhiteSpace(detail)
                && !detail.StartsWith("HTTP ", StringComparison.Ordinal))
                return baseMsg + "\n\n服务商原话：" + detail;

            return baseMsg;
        }

        // ② 超时
        if (ex is TaskCanceledException || ex is OperationCanceledException)
            return "请求超时了。可能是网络慢，或者服务商那边卡住了。";

        // ③ 底层是 Java 异常（Android 网络栈抛的）：
        //    .NET 拿到 Java 异常的 message 时经常是 "Exception_WasThrown" 这种占位符，
        //    直接甩给用户等于没提示 —— 翻成人话，并把真实类型带上，方便排查。
        var java = FindJava(ex);
        if (java != null)
            return "网络连接被中断（" + java + "）。\n"
                 + "常见原因：网络切换或断流、服务商掐线、代理/证书拦截。\n"
                 + "过一会儿重发试试；一直这样就检查网络，或换个接口地址。";

        return "网络连接失败：" + Describe(ex);
    }

    /// <summary>
    /// 值不值得自动重试一次。
    ///
    /// **重点**：Android 网络栈在连接被掐时抛的是 **Java 异常**（不是 .NET 的
    /// SocketException），以前没认出来 → 不重试 → 用户看到的就是"断断续续、每次都失败"。
    /// </summary>
    public static bool IsTransient(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException || e is System.Net.Sockets.SocketException) return true;
            if (e is IOException) return true;

            var full = e.GetType().FullName ?? "";
            var msg = e.Message ?? "";

            if (full.Contains("Java", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Exception_WasThrown", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("unexpected end of stream", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("连接中断", StringComparison.Ordinal)) return true;
        }

        if (ex is TaskCanceledException || ex is OperationCanceledException) return true;

        // 有状态码的：只有 5xx 值得重试（4xx 重试一百次也一样）
        if (ex is HttpRequestException hx && hx.StatusCode.HasValue)
            return (int)hx.StatusCode.Value >= 500;

        return false;
    }

    // ───────────── 把服务商的错误正文捞出来 ─────────────

    /// <summary>
    /// 像 `EnsureSuccessStatusCode()` 一样，但**先把服务商的错误正文捞出来**再抛。
    ///
    /// 只报状态码用户没法排查：429 到底是"发太快"还是"额度用完"？403 是"没权限"还是"欠费"？
    /// OpenAI 兼容接口会在正文里写清楚，比如
    /// `{"error":{"message":"You exceeded your current quota…","code":"insufficient_quota"}}`。
    /// 所以这里把正文抠出来塞进异常消息，最后会显示成「服务商原话：…」。
    /// </summary>
    public static async Task EnsureOkAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;

        int code = (int)resp.StatusCode;
        string detail = "";
        try { detail = await resp.Content.ReadAsStringAsync(); } catch { }

        var msg = ExtractApiError(detail);
        throw new HttpRequestException(
            string.IsNullOrWhiteSpace(msg) ? $"HTTP {code}" : msg,
            inner: null,
            statusCode: resp.StatusCode);
    }

    /// <summary>从 OpenAI 兼容的错误正文里抠出 message / code（各家格式不完全一样，广撒网）。</summary>
    public static string ExtractApiError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var m = err.TryGetProperty("message", out var mv) ? (mv.GetString() ?? "") : "";
                var c = err.TryGetProperty("code", out var cv) && cv.ValueKind == JsonValueKind.String
                        ? (cv.GetString() ?? "") : "";
                if (!string.IsNullOrWhiteSpace(m) && !string.IsNullOrWhiteSpace(c)) return $"{m}（{c}）";
                if (!string.IsNullOrWhiteSpace(m)) return m;
                if (!string.IsNullOrWhiteSpace(c)) return c;
            }

            if (root.TryGetProperty("message", out var m2) && m2.ValueKind == JsonValueKind.String)
                return m2.GetString() ?? "";
        }
        catch { /* 不是 JSON 就当纯文本处理 */ }

        return body.Length > 200 ? body[..200] : body;
    }

    // ───────────── 内部 ─────────────

    /// <summary>
    /// 往异常链里找 Java 异常（Android 网络栈抛的）。找到就返回它的类型名，没有返回 null。
    /// </summary>
    private static string? FindJava(Exception? ex)
    {
        for (int i = 0; ex != null && i < 6; i++, ex = ex.InnerException)
        {
            var full = ex.GetType().FullName ?? "";
            var msg = ex.Message ?? "";
            if (full.Contains("Java", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("Exception_WasThrown", StringComparison.OrdinalIgnoreCase))
            {
                var detail = msg.Contains("Exception_WasThrown", StringComparison.OrdinalIgnoreCase)
                    ? full
                    : (string.IsNullOrWhiteSpace(msg) ? full : full + "：" + msg);
                return string.IsNullOrWhiteSpace(detail) ? ex.GetType().Name : detail;
            }
        }
        return null;
    }

    /// <summary>
    /// 把异常链拼成一行能读的（最多四层）。
    /// 有些异常 `Message` 是空的，那时候至少把类型名带上 —— 总比空白强。
    /// </summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
        {
            var m = e.Message ?? "";
            parts.Add(string.IsNullOrWhiteSpace(m) ? e.GetType().Name : e.GetType().Name + "：" + m);
        }
        return string.Join(" ← ", parts);
    }
}

// ────────────── 原 ReminderParser.cs ──────────────
/// <summary>
/// 承诺提醒的中文时间解析器：把「明天早上8点」「20分钟后」「下周一」这类说法
/// 换算成真实的 DateTime，解析不出来就返回 null（上层据此告诉模型「没听懂时间，重说」）。
///
/// 为什么要单独有它：模型负责「说人话」，客户端负责「算时间」。
/// 让模型自己换算成 "2025-06-01T08:00" 既容易算错，又会把日期塞进正文污染语气；
/// 这里只认中文表达、容错优先，宁可返回 null 也不猜一个错误的时间。
///
/// 支持的常见说法（不区分大小写、允许带空格和标点）：
///   · 相对：20分钟后 / 3小时后 / 半小时后 / 一个半小时后 / 2天后 / 一周后 / 一会儿
///   · 日期：今天 / 明天 / 后天 / 大后天 / 周三 / 下周一 / 下下周五
///   · 时段：凌晨 / 早上 / 上午 / 中午 / 下午 / 傍晚 / 晚上 / 深夜 / 半夜
///   · 时刻：8点 / 8点半 / 8点15分 / 8:30 / 八点一刻 / 中午12点半
///   · 组合：明天早上8点 / 今晚9点 / 下周一上午10点 / 明天中午12点半
/// </summary>
public static class ReminderParser
{
    // ─────────────────────────── 对外入口 ───────────────────────────

    /// <summary>以「现在」为基准解析中文时间表达；解析不出来返回 null。</summary>
    public static DateTime? Parse(string text) => Parse(text, DateTime.Now);

    /// <summary>
    /// 解析中文时间表达。now 由调用方给（方便单测、也方便心跳里复用同一个基准时刻）。
    /// </summary>
    public static DateTime? Parse(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // ① 清洗 + 模糊词落地 + 复合时段词展开
        var t = Clean(text);

        // ② 中文数字 → 阿拉伯数字（"八点"→"8点"、"下周一"→"下周1"）
        //    必须放在 TryRelative 之前，否则「三小时后」里的「三」匹配不到 \d+
        t = NormalizeNumbers(t);

        // ③ 纯相对时长：「20分钟后」「3小时后」「半小时后」
        var rel = TryRelative(t, now);
        if (rel.HasValue) return rel;

        // ④ 日期锚点（今天 / 明天 / 后天 / 大后天 / 周几）
        bool hasDay = false;
        var baseDay = now.Date;
        if (TryWeekday(t, now, out var wkDay)) { baseDay = wkDay; hasDay = true; }
        else if (t.Contains("大后天")) { baseDay = now.Date.AddDays(3); hasDay = true; }
        else if (t.Contains("后天")) { baseDay = now.Date.AddDays(2); hasDay = true; }
        else if (t.Contains("明天")) { baseDay = now.Date.AddDays(1); hasDay = true; }
        else if (t.Contains("今天")) { baseDay = now.Date; hasDay = true; }

        // ⑤ 时段词（决定「8点」到底是早上还是晚上）
        var period = "";
        foreach (var p in Periods)
        {
            if (t.Contains(p)) { period = p; break; }
        }

        // ⑥ 具体时刻
        var (clockH, clockM, hasClock) = TryClock(t);

        // 三样一样都没有 → 这压根不是时间表达
        if (!hasDay && !hasClock && period.Length == 0) return null;

        int hour, minute;
        if (hasClock) { hour = clockH; minute = clockM; }
        else if (period.Length > 0) { hour = DefaultHourOf(period); minute = 0; }
        else { hour = 9; minute = 0; }   // 只说「明天」→ 默认早上 9 点

        if (period.Length > 0) hour = ApplyPeriod(period, hour);

        if (hour is < 0 or > 23) hour = 9;
        if (minute is < 0 or > 59) minute = 0;

        var due = baseDay.AddHours(hour).AddMinutes(minute);

        // 没说是哪天、而这个钟点今天已经过了 → 顺延到明天
        // （晚上 10 点说「8点」，指的一定是明天早上 8 点，不是今早那个已经过去的）
        if (!hasDay && due <= now) due = due.AddDays(1);

        // 算出来已经过去一小时以上：这个时间表达不成立，
        // 别建一条「立刻就炸」的提醒（「今天早上8点」在晚上说就是这种情况）
        if (due <= now.AddHours(-1)) return null;

        return due;
    }

    /// <summary>
    /// 把 <c>{remind:"时间|内容"}</c> 的值拆成（时间, 内容）。
    /// 同时兼容半角竖线和全角竖线；没有竖线时整段当内容、时间为空。
    /// </summary>
    public static (string time, string content) SplitValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ("", "");

        int i = value.IndexOf('|');
        if (i < 0) i = value.IndexOf('｜');
        if (i < 0) return ("", value.Trim());

        return (value[..i].Trim(), value[(i + 1)..].Trim());
    }

    /// <summary>把到期时刻写成给用户看的中文：「明天 08:00」「下周三 21:00」「6月3日 09:00」。</summary>
    public static string Describe(DateTime due, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        int span = (due.Date - n.Date).Days;

        string day = span switch
        {
            0 => "今天",
            1 => "明天",
            2 => "后天",
            3 => "大后天",
            >= 4 and <= 7 => "周" + WeekCn(due.DayOfWeek),
            _ => $"{due.Month}月{due.Day}日",
        };
        return $"{day} {due:HH:mm}";
    }

    // ─────────────────────────── 预处理 ───────────────────────────

    /// <summary>中文数字字符表（「两」按 2 算，口语里常见）。</summary>
    private static readonly Dictionary<char, int> CnDigits = new()
    {
        ['零'] = 0, ['〇'] = 0,
        ['一'] = 1, ['二'] = 2, ['两'] = 2, ['三'] = 3, ['四'] = 4,
        ['五'] = 5, ['六'] = 6, ['七'] = 7, ['八'] = 8, ['九'] = 9,
    };

    /// <summary>时段词表（长的在前，避免被更短的词抢先匹配）。</summary>
    private static readonly string[] Periods =
    {
        "凌晨", "早上", "早晨", "清晨", "上午", "中午",
        "下午", "傍晚", "黄昏", "晚上", "深夜", "半夜",
    };

    /// <summary>
    /// 清洗：模糊说法先落成具体时长（免得后面的数字规范化把它们拆坏）、
    /// 复合时段词展开成「日期 + 时段」、去掉空白和标点。
    /// </summary>
    private static string Clean(string text)
    {
        var t = (text ?? "").Trim();

        // 模糊说法 → 具体时长。只作用于时间部分（内容不会被传进来），所以不会误伤正文。
        t = t.Replace("一会儿", "30分钟后")
             .Replace("待会儿", "30分钟后")
             .Replace("等会儿", "30分钟后")
             .Replace("稍后", "30分钟后");

        // 复合时段词展开：「明早」=「明天早上」，这样后面只用认一种写法
        t = t.Replace("明早", "明天早上").Replace("明晨", "明天早上")
             .Replace("明晚", "明天晚上")
             .Replace("今早", "今天早上").Replace("今晨", "今天早上")
             .Replace("今晚", "今天晚上")
             .Replace("明儿", "明天").Replace("今儿", "今天")
             .Replace("夜里", "晚上").Replace("夜晚", "晚上").Replace("晚间", "晚上")
             .Replace("一刻", "15分").Replace("三刻", "45分");

        // 去空白与标点，方便正则匹配（「明天，8点」→「明天8点」）
        t = t.Replace(" ", "").Replace("\u3000", "").Replace("\t", "")
             .Replace("，", "").Replace(",", "").Replace("。", "")
             .Replace("、", "").Replace("；", "").Replace(";", "");

        return t;
    }

    private static bool IsCnDigit(char c) => CnDigits.ContainsKey(c) || c == '十' || c == '百';

    /// <summary>把一段中文数字转成整数（"二十三"→23、"十"→10、"八"→8）。</summary>
    private static int? ParseCnNumber(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;

        int section = 0, num = 0;
        bool any = false;
        foreach (var ch in s)
        {
            if (CnDigits.TryGetValue(ch, out var d)) { num = d; any = true; }
            else if (ch == '十') { section += (num == 0 ? 1 : num) * 10; num = 0; any = true; }
            else if (ch == '百') { section += (num == 0 ? 1 : num) * 100; num = 0; any = true; }
            else return null;
        }
        return any ? section + num : null;
    }

    /// <summary>整串里的中文数字段全部换成阿拉伯数字（"下周一"→"下周1"、"八点半"→"8点半"）。</summary>
    private static string NormalizeNumbers(string t)
    {
        var sb = new StringBuilder(t.Length);
        int i = 0;
        while (i < t.Length)
        {
            if (IsCnDigit(t[i]))
            {
                int j = i;
                while (j < t.Length && IsCnDigit(t[j])) j++;
                var seg = t[i..j];
                var n = ParseCnNumber(seg);
                sb.Append(n.HasValue ? n.Value.ToString() : seg);
                i = j;
            }
            else
            {
                sb.Append(t[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    // ─────────────────────────── 各段解析 ───────────────────────────

    /// <summary>相对时长：「20分钟后」「3小时后」「半小时后」「2天后」「一周后」。</summary>
    private static DateTime? TryRelative(string t, DateTime now)
    {
        // 「半小时后」「一个半小时后」——半在中文里是「半」，不参与数字规范化
        var mh = Regex.Match(t, @"(\d+)?\s*个?\s*半\s*个?\s*小时\s*(?:之?后|以后|过后)");
        if (mh.Success)
        {
            int baseHours = mh.Groups[1].Success ? int.Parse(mh.Groups[1].Value) : 0;
            return now.AddMinutes(baseHours * 60 + 30);
        }

        // 「半天后」= 12 小时
        if (t.Contains("半天后") || t.Contains("半天之后") || t.Contains("半天以后"))
            return now.AddHours(12);

        var m = Regex.Match(t,
            @"(\d+)\s*个?\s*(秒|分钟|分|小时|钟头|天|日|周|星期|礼拜)\s*(?:之?后|以后|过后)");
        if (!m.Success) return null;

        int n = int.Parse(m.Groups[1].Value);
        return m.Groups[2].Value switch
        {
            "秒" => now.AddSeconds(n),
            "分钟" or "分" => now.AddMinutes(n),
            "小时" or "钟头" => now.AddHours(n),
            "天" or "日" => now.AddDays(n),
            "周" or "星期" or "礼拜" => now.AddDays(n * 7),
            _ => null,
        };
    }

    /// <summary>
    /// 星期几：「周三」「下周一」「下下周五」「礼拜天」。
    /// 「这周/本周」不写前缀时，若今天正好是那天，按「下一个那天」算（说「周三」不会是刚刚过去的今天）。
    /// </summary>
    private static bool TryWeekday(string t, DateTime now, out DateTime day)
    {
        day = default;

        var m = Regex.Match(t, @"(下下|下|这|本|上)?\s*(?:个)?\s*(?:周|星期|礼拜)\s*([一二三四五六日天1-7])");
        if (!m.Success) return false;

        int target = m.Groups[2].Value switch
        {
            "一" or "1" => 1,
            "二" or "2" => 2,
            "三" or "3" => 3,
            "四" or "4" => 4,
            "五" or "5" => 5,
            "六" or "6" => 6,
            "日" or "天" or "7" => 0,
            _ => -1,
        };
        if (target < 0) return false;

        int today = (int)now.DayOfWeek;   // 0 = 周日
        var prefix = m.Groups[1].Value;

        // 以周一为一周起点换算成 1..7（周一=1 … 周日=7），
        // 否则「周日」在 DayOfWeek 里是 0，直接减会算出负数天。
        int todayIdx = today == 0 ? 7 : today;
        int targetIdx = target == 0 ? 7 : target;

        int delta;
        switch (prefix)
        {
            case "上":
                return false;                 // 「上周三」是过去，当提醒没意义
            case "下下":
                // 下下周 = 本周起点 + 7 + 7，即「下周的那天」再 +7
                delta = (7 - todayIdx + targetIdx) + 7;
                break;
            case "下":
                // 下X = 本周起点 + 7：距今天 = 7 - 本周第几天 + 目标第几天
                delta = 7 - todayIdx + targetIdx;
                break;
            default:
                delta = (target - today + 7) % 7;
                // 不带前缀且正好是今天 → 说的是下一个那天
                if (delta == 0 && prefix.Length == 0) delta = 7;
                break;
        }

        day = now.Date.AddDays(delta);
        return true;
    }

    /// <summary>具体时刻：「8点」「8点半」「8点15分」「8:30」「8时30分」。</summary>
    private static (int hour, int minute, bool ok) TryClock(string t)
    {
        // 8点半
        var m = Regex.Match(t, @"(\d{1,2})\s*[点时：:]\s*半");
        if (m.Success) return (int.Parse(m.Groups[1].Value), 30, true);

        // 8点15分 / 8点15 / 8:15 / 8：15 / 8时15分
        m = Regex.Match(t, @"(\d{1,2})\s*[点时：:]\s*(\d{1,2})\s*分?");
        if (m.Success)
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), true);

        // 8点 / 8时
        m = Regex.Match(t, @"(\d{1,2})\s*[点时]");
        if (m.Success) return (int.Parse(m.Groups[1].Value), 0, true);

        return (0, 0, false);
    }

    /// <summary>只说了时段、没说几点时，用各时段的默认钟点。</summary>
    private static int DefaultHourOf(string period) => period switch
    {
        "凌晨" => 5,
        "早上" or "早晨" or "清晨" => 8,
        "上午" => 10,
        "中午" => 12,
        "下午" => 15,
        "傍晚" or "黄昏" => 18,
        "晚上" => 20,
        "深夜" or "半夜" => 23,
        _ => 9,
    };

    /// <summary>
    /// 用时段词修正小时：「晚上8点」→ 20:00、「下午3点」→ 15:00、「中午1点」→ 13:00。
    /// 已经写成 24 小时制（如「晚上21点」）就原样保留。
    /// </summary>
    private static int ApplyPeriod(string period, int hour)
    {
        hour = ((hour % 24) + 24) % 24;

        switch (period)
        {
            case "凌晨":
                if (hour == 12) return 0;
                return hour;                       // 1~5 点直接用
            case "早上":
            case "早晨":
            case "清晨":
            case "上午":
                if (hour == 12) return 0;          // 「早上12点」口语里指 0 点
                return hour;
            case "中午":
                if (hour is >= 1 and <= 5) return hour + 12;   // 「中午1点」= 13 点
                return hour;                                   // 11、12 点直接用
            case "下午":
            case "傍晚":
            case "黄昏":
                if (hour < 12) return hour + 12;
                return hour;
            case "晚上":
            case "深夜":
            case "半夜":
                if (hour == 12) return 0;
                if (hour < 12) return hour + 12;
                return hour;
            default:
                return hour;
        }
    }

    /// <summary>星期几的中文写法（用于 Describe）。</summary>
    private static string WeekCn(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日",
    };
}

// ────────────── 原 BackupService.cs ──────────────
/// <summary>
/// 数据备份/恢复：把全部聊天、记忆、日记、首次相遇时间和头像打包成一个 JSON 文件
/// （通过系统分享面板保存到任意位置），导入时覆盖式恢复。
/// 注意：聊天里的图片以文件形式存在设备本地，备份只保留路径，恢复后旧图片不可迁移。
/// </summary>
public static class BackupService
{
    private sealed class BackupData
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("exportedAt")] public string ExportedAt { get; set; } = "";
        [JsonPropertyName("companionSince")] public string? CompanionSince { get; set; }
        [JsonPropertyName("avatarPng")] public string? AvatarPng { get; set; }
        [JsonPropertyName("messages")] public List<MsgRow>? Messages { get; set; }
        [JsonPropertyName("memories")] public List<MemoryRow>? Memories { get; set; }
        [JsonPropertyName("diaries")] public List<DiaryRow>? Diaries { get; set; }
    }

    /// <summary>导出到缓存文件并返回完整路径（随后用分享面板发给用户）。</summary>
    public static async Task<string> ExportAsync()
    {
        var (msgs, memories, diaries) = await ChatStore.Instance.ExportAllAsync();

        string? avatarB64 = null;
        try
        {
            if (File.Exists(AvatarService.AvatarPath))
                avatarB64 = Convert.ToBase64String(await File.ReadAllBytesAsync(AvatarService.AvatarPath));
        }
        catch { }

        var data = new BackupData
        {
            ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            CompanionSince = AppSettings.CompanionSinceText,
            AvatarPng = avatarB64,
            Messages = msgs,
            Memories = memories,
            Diaries = diaries
        };

        #if ANDROID
            var dir = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.CacheDir?.AbsolutePath
                      ?? FileSystem.CacheDirectory
                      ?? ".";
#else
            var dir = FileSystem.CacheDirectory
                      ?? ".";
#endif
        var path = Path.Combine(dir, $"qingyang_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false }));
        return path;
    }

    /// <summary>从备份文件恢复（覆盖式：清空现有聊天/记忆/日记后写入）。</summary>
    public static async Task<bool> RestoreAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var data = JsonSerializer.Deserialize<BackupData>(json);
        if (data?.Messages == null) throw new Exception("备份文件格式不正确");

        // 图片路径是设备本地路径，恢复后旧图片不可用，置空避免悬空引用
        foreach (var m in data.Messages)
        {
            m.Id = 0;
            m.ImageUrl = "";
        }

        await ChatStore.Instance.RestoreAllAsync(data.Messages, data.Memories ?? new(), data.Diaries ?? new(), data.CompanionSince);

        try
        {
            if (!string.IsNullOrWhiteSpace(data.AvatarPng))
                await File.WriteAllBytesAsync(AvatarService.AvatarPath, Convert.FromBase64String(data.AvatarPng));
        }
        catch { }

        return true;
    }
}

// ────────────── 原 ChatJump.cs ──────────────
/// <summary>
/// 跨页「回到聊天页并定位到某条消息」的通道。
///
/// 搜索页 / 收藏页点某条结果时，要回到 ChatPage 并滚到那条消息。
/// 走静态回调而不是构造参数，是因为这两个页面都是
/// <c>Navigation.PushAsync(new XxxPage())</c> 创建的，拿不到 ChatPage 实例；
/// 而 ChatPage 那边要保持「只加一个公开方法」的最小改动
/// （它同时有多个 agent 在改，不能动构造与导航逻辑）。
///
/// 用法：
///   ChatPage 打开搜索页前 → <c>ChatJump.ScrollRequested = ScrollToMessageId;</c>
///   搜索页点结果时        → <c>ChatJump.Request(MsgId);</c>
/// </summary>
internal static class ChatJump
{
    /// <summary>ChatPage 注册的定位回调（参数 = 消息主键 Id）。</summary>
    public static Action<int>? ScrollRequested;

    /// <summary>安全触发定位：没人接收 / 出错都静默忽略，绝不让点击结果变成崩溃。</summary>
    public static void Request(int msgId)
    {
        if (msgId <= 0) return;
        try { ScrollRequested?.Invoke(msgId); } catch { }
    }
}


namespace 青阳AI;

/// <summary>
/// 全项目统一的 HTTP 客户端工厂与请求辅助。
///
/// 为什么要有这个文件（2026-10-29 整合）：
///   原来 14 个文件各自 `private static readonly HttpClient _http = new()`，
///   每个 HttpClient 实例各自持有一组 socket 连接池，长期运行会累积 TIME_WAIT
///   并可能耗尽本地端口（经典 socket exhaustion 反模式）。这里统一成**共享实例**，
///   按超时需求分池：短请求一池、长任务（生图/下载）一池。
///
/// 硬规则（踩过坑，务必遵守）：
///   1. **绝不修改 DefaultRequestHeaders** —— 这些实例是共享的，后台心跳/复盘随时并发。
///      Authorization 必须每个请求自带，用 <see cref="AttachAuth"/>。
///   2. 流式请求必须 SendAsync(req, ResponseHeadersRead, ct)，不能用 PostAsync（会整段缓冲）。
///   3. 响应体必须释放（using），否则连接不归还连接池。
/// </summary>
public static class Http
{
    /// <summary>短请求池（普通 API 调用，默认 100 秒超时，与 HttpClient 默认一致）。</summary>
    public static HttpClient Short { get; } = CreateClient(TimeSpan.FromSeconds(100));

    /// <summary>长任务池（文生图轮询、文件下载等，5 分钟超时）。</summary>
    public static HttpClient Long { get; } = CreateClient(TimeSpan.FromMinutes(5));

    /// <summary>超长任务池（大文件下载，10 分钟超时）。</summary>
    public static HttpClient Download { get; } = CreateClient(TimeSpan.FromMinutes(10));

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip
                                   | System.Net.DecompressionMethods.Deflate
                                   | System.Net.DecompressionMethods.Brotli,
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>
    /// 给请求挂上 Bearer 认证。
    /// **必须每个请求单独挂**，不许写进 DefaultRequestHeaders（并发会打架/串 Key）。
    /// </summary>
    public static void AttachAuth(HttpRequestMessage req, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return;
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
    }

    /// <summary>给请求挂上 Bearer 认证（对话模型专用，自动取当前生效的 Key）。</summary>
    public static void AttachChatAuth(HttpRequestMessage req) =>
        AttachAuth(req, AppSettings.EffectiveChatApiKey);

    /// <summary>构造一个 JSON 内容的请求体。</summary>
    public static StringContent JsonContent(object body) =>
        new(System.Text.Json.JsonSerializer.Serialize(body),
            System.Text.Encoding.UTF8, "application/json");

    /// <summary>
    /// 发一个非流式 POST，返回响应正文。失败抛异常，由调用方决定是否静默。
    /// 响应体在这里释放，不会漏。
    /// </summary>
    public static async Task<string> PostJsonAsync(
        string url, object body, string? apiKey = null,
        TimeSpan? timeout = null, HttpClient? client = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        AttachAuth(req, apiKey);
        req.Content = JsonContent(body);

        using var cts = timeout.HasValue ? new CancellationTokenSource(timeout.Value) : null;
        using var resp = await (client ?? Short).SendAsync(req, cts?.Token ?? CancellationToken.None);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// <summary>
    /// 从非流式响应 JSON 里抽出 choices[0].message.content。
    /// 解析失败返回 null（调用方按"没拿到内容"处理）。
    /// </summary>
    public static string? ExtractMessageContent(string raw)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == System.Text.Json.JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c))
                return c.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 从非流式响应里抽 content 并去掉可能的 ``` 围栏与首尾空白。
    /// </summary>
    public static string ExtractCleanContent(string raw) =>
        CleanFence(ExtractMessageContent(raw) ?? "");

    /// <summary>去掉 Markdown 代码围栏与首尾空白。</summary>
    public static string CleanFence(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = text.IndexOf('\n');
            if (nl >= 0) text = text[(nl + 1)..];
            if (text.EndsWith("```", StringComparison.Ordinal)) text = text[..^3];
        }
        return text.Trim();
    }
}

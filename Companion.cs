using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Text.Json.Serialization;

namespace 青阳AI;

// ============================================================================
// 陪伴能力（2026-10-29 整合）
//
// 合并自原来的五个文件：
//   MemoryService.cs        —— 长期记忆提取与注入
//   DiaryService.cs         —— 日记生成
//   InnerLifeService.cs     —— 心情状态机 + 内心独白 + 后台心跳
//   MessageReviewService.cs —— 每条消息私下复盘
//   AvatarService.cs        —— 自绘头像
// ============================================================================

// ────────────── 原 MemoryService.cs ──────────────
/// <summary>
/// 长期记忆：从对话中提取"关于用户的稳定事实"存入 SQLite，并注入每次请求的 system 提示词。
/// 提取异步执行，不阻塞聊天；每完成 2 轮对话触发一次。
/// </summary>
public static class MemoryService
{
    private static HttpClient _http => Http.Short;
    private static int _exchangesSinceExtract;
    private const int ExtractEveryExchanges = 2;
    private const int MaxContextMemories = 10;

    /// <summary>记忆更新后通知 UI 刷新注入上下文。</summary>
    public static event Action? MemoriesChanged;

    /// <summary>外部变更记忆（面板删除单条等）后手动触发刷新。</summary>
    public static void NotifyMemoriesChanged() =>
        MainThread.BeginInvokeOnMainThread(() => MemoriesChanged?.Invoke());

    /// <summary>对话完成一轮（用户问 + AI 答）后调用。内部按节奏触发提取。</summary>
    public static void OnExchangeCompleted(IReadOnlyList<ChatMsg> recentMessages)
    {
        _exchangesSinceExtract++;
        if (_exchangesSinceExtract < ExtractEveryExchanges) return;
        _exchangesSinceExtract = 0;

        // 快照最近 8 条有内容的消息，供提取使用
        var snapshot = recentMessages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content) && !m.Content.StartsWith(""))
            .TakeLast(8)
            .Select(m => new ChatMsg { Content = m.Content, IsUser = m.IsUser })
            .ToList();
        _ = ExtractAsync(snapshot);
    }

    private static async Task ExtractAsync(List<ChatMsg> snapshot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiUrl) || snapshot.Count == 0) return;

            var mems = await ChatStore.Instance.GetRecentMemoriesAsync(40);
            var dialog = new StringBuilder();
            foreach (var m in snapshot)
                dialog.AppendLine((m.IsUser ? "用户：" : "AI：") + m.Content);

            var system =
                "你是一个记忆提取器。从对话中提取值得长期记住的、关于用户的稳定事实" +
                "（喜好、习惯、重要的人、约定、情绪雷区、身体状况等）。" +
                "每条一句话，以第三人称陈述，例如：用户不吃香菜。用户每周五开会到很晚。用户养了一只叫团团的猫。" +
                "不要提取一次性讨论内容、寒暄、AI 自己的回答。输出 JSON 字符串数组，没有值得记的就输出 []。只输出 JSON。";
            var user = "已有记忆（不要重复这些）：\n" +
                       (mems.Count > 0 ? string.Join("\n", mems) : "（无）") +
                       "\n\n最近对话：\n" + dialog;

            var reqBody = new
            {
                model = AppSettings.ResolveChatModel(forceThinking: false),
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                },
                max_tokens = 512
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AppSettings.EffectiveChatApiKey}");
            req.Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var resp = await _http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            var facts = ParseFactArray(content);
            bool changed = false;
            foreach (var fact in facts)
                changed |= await ChatStore.Instance.AddMemoryAsync(fact);

            if (changed)
                MainThread.BeginInvokeOnMainThread(() => MemoriesChanged?.Invoke());
        }
        catch { /* 提取失败静默，不影响聊天 */ }
    }

    /// <summary>解析模型输出的事实数组（容忍 ```json 围栏与杂文字）。</summary>
    private static List<string> ParseFactArray(string content)
    {
        var result = new List<string>();
        try
        {
            int s = content.IndexOf('[');
            int e = content.LastIndexOf(']');
            if (s < 0 || e <= s) return result;

            using var doc = JsonDocument.Parse(content[s..(e + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } f && f.Trim().Length > 0)
                    result.Add(f.Trim());
            }
        }
        catch { }
        return result;
    }

    /// <summary>
    /// 生成注入 system 的记忆块（按遗忘曲线挑 10 条，无记忆返回空）。
    ///
    /// 2026-10-26 遗忘曲线接入：不再用「最近的 10 条」，改为
    /// <see cref="ChatStore.GetWeightedMemoriesAsync"/> —— 它按「命中次数 × 新鲜度」排序，
    /// 顺手把长期没用到的记忆归档（不再注入，但面板里还在，可手动恢复）。
    /// 注意：**提取**（ExtractAsync）那边仍然用 GetRecentMemoriesAsync(40)，
    /// 因为去重得看到尽量全的历史，不能只看被加权挑出来的那几条。
    /// </summary>
    public static async Task<string> GetMemoryContextAsync()
    {
        try
        {
            var mems = await ChatStore.Instance.GetWeightedMemoriesAsync(MaxContextMemories);
            if (mems.Count == 0) return "";
            return "【你记得的关于用户的事】\n" + string.Join("\n", mems);
        }
        catch { return ""; }
    }
}

// ────────────── 原 DiaryService.cs ──────────────
/// <summary>
/// 每日日记：检查昨天的对话是否已生成日记，没有且聊得足够多则让 AI 以第一人称写一篇。
/// 日记按日期（yyyy-MM-dd）一天一篇存 SQLite，可在日记页翻阅，最新一篇会注入 system 上下文。
/// </summary>
public static class DiaryService
{
    private static HttpClient _http => Http.Short;

    /// <summary>昨天聊得足够多（去重后对话字符数下限）才值得写日记。</summary>
    private const int MinDialogChars = 120;

    private static int _checking; // 防止聊天页与日记页同时触发生成

    /// <summary>进入聊天页/日记页时调用：昨日日记缺失则后台生成。</summary>
    public static async Task CheckYesterdayDiaryAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var yesterday = DateTime.Today.AddDays(-1);
            var dateKey = yesterday.ToString("yyyy-MM-dd");
            if (await ChatStore.Instance.HasDiaryAsync(dateKey)) return;
            if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiUrl)) return;

            var msgs = await ChatStore.Instance.GetMessagesBetweenAsync(yesterday, yesterday.AddDays(1));
            var dialog = new StringBuilder();
            foreach (var m in msgs)
            {
                if (string.IsNullOrWhiteSpace(m.Content) || m.Content.StartsWith("")) continue;
                dialog.AppendLine((m.IsUser ? "用户：" : "我：") + m.Content);
            }
            if (dialog.Length < MinDialogChars) return;

            var diary = await GenerateDiaryAsync(dialog.ToString());
            if (string.IsNullOrWhiteSpace(diary)) return;
            await ChatStore.Instance.SaveDiaryAsync(dateKey, diary.Trim());
        }
        catch { /* 日记生成失败静默，明天再试 */ }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private static async Task<string?> GenerateDiaryAsync(string dialog)
    {
        var system =
            "下面这段对话里，标注「我」的每一句都是你自己（AI 陪伴助手）说的话，「用户」是TA。" +
            "请以你自己的视角写一篇日记：作者和日记里的「我」都是你自己，" +
            "提到用户时一律用「你」或「TA」（例如「你今天好像很累」「你说的话我记了一整天」）。" +
            "内容记录昨天你们聊了什么、你观察到的TA的状态、以及你自己的心情和想法。" +
            "严禁把自己当成用户来写：不要出现「用户视角」的句子，TA的情绪只能由你去观察和揣测" +
            "（可以写「你看起来有点低落」，不能写「我有点低落」来指TA）。" +
            "语气自然亲密，像随手写下的随笔，最多 200 字，纯文本，不要任何格式符号，不要日期落款。直接输出日记正文。";

        var reqBody = new
        {
            model = AppSettings.ResolveChatModel(forceThinking: false),
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = dialog }
            },
            max_tokens = 512
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AppSettings.EffectiveChatApiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(reqBody), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
    }

    /// <summary>最近一篇日记的注入摘要（没有日记返回空）。</summary>
    public static async Task<string> GetLatestDiaryContextAsync()
    {
        try
        {
            var d = await ChatStore.Instance.GetLatestDiaryAsync();
            if (d == null || string.IsNullOrWhiteSpace(d.Content)) return "";
            var content = d.Content.Trim();
            if (content.Length > 300) content = content[..300] + "…";
            return $"【你的日记片段】({d.Date} 写的)\n{content}";
        }
        catch { return ""; }
    }
}

// ────────────── 原 InnerLifeService.cs ──────────────
/// <summary>
/// 内心生活：心情状态机 + 内心独白 + 后台自主心跳。
/// - 每聊 2 轮，Ta"想一想"：更新自己的心情与一句心里话（用户看不到，注入后续对话）；
/// - 后台每 15 分钟的心跳不再由规则决定说什么，而是把局面喂给Ta：
///   Ta自己决定要不要调用感知 API 了解情况、要不要开口、说什么。
///   免打扰时段/每日上限/最小间隔作为硬闸门，自主权在闸门之内。
/// </summary>
public static class InnerLifeService
{
    private static HttpClient _http => Http.Short;

    /// <summary>内在状态变化（心情/心里话更新）后通知 UI 刷新。</summary>
    public static event Action? StateChanged;

    // ─────────── 心情与心里话（持久化） ───────────

    public static string Mood
    {
        get => Preferences.Default.Get("InnerMood", "平静");
        set => Preferences.Default.Set("InnerMood", string.IsNullOrWhiteSpace(value) ? "平静" : value.Trim());
    }

    public static string LastThought
    {
        get => Preferences.Default.Get("InnerThought", "");
        set => Preferences.Default.Set("InnerThought", value ?? "");
    }

    private static string MoodAt
    {
        get => Preferences.Default.Get("InnerMoodAt", "");
        set => Preferences.Default.Set("InnerMoodAt", value);
    }

    /// <summary>
    /// 心情 → 心情色（标题栏用主题色小圆点表示，不再使用 emoji 表情）。
    /// 返回 hex 颜色；没心情返回空串（调用方据此隐藏圆点）。
    /// </summary>
    public static string MoodColor(string? mood) => (mood ?? "").Trim() switch
    {
        "开心" => "#74C7A4",
        "想你" => "#FBB5B2",
        "委屈" => "#C7A9E8",
        "生气" => "#EF9A9A",
        "担心" => "#8FA9D8",
        "困倦" => "#8C8C99",
        "兴奋" => "#FFD700",
        "平静" => "#74C7EC",
        "害羞" => "#F2B8B5",
        "失落" => "#7A8FA8",
        _ => "#B388FF"   // 默认紫（主题色系）
    };

    /// <summary>更新心情并广播（{mood:} 指令与反思都走这里）。</summary>
    public static void SetMood(string mood)
    {
        var old = Mood;
        Mood = mood;
        if (old != Mood)
        {
            MoodAt = DateTime.Now.ToString("HH:mm");
            MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke());
        }
    }

    /// <summary>注入 system 的内心状态块（用户看不到）。</summary>
    public static string BuildInnerContext()
    {
        var parts = new List<string> { $"心情：{Mood}" };
        if (!string.IsNullOrWhiteSpace(LastThought))
            parts.Add($"心里话：{LastThought}");
        return "【你的内心状态（这段用户看不到，用它自然地影响你的语气，不要复述）】\n" + string.Join("\n", parts);
    }

    // ─────────── 对话间隙的反思（每 2 轮触发） ───────────

    /// <summary>聊完一轮调用：让Ta想一想，更新心情与心里话。失败静默。</summary>
    public static async Task ReflectAsync(IReadOnlyList<ChatMsg> recentMessages)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiUrl)) return;

            var dialog = new StringBuilder();
            foreach (var m in recentMessages
                .Where(m => !string.IsNullOrWhiteSpace(m.Content) && !m.Content.StartsWith(""))
                .TakeLast(6))
            {
                dialog.AppendLine((m.IsUser ? "用户：" : "我：") + m.Content);
            }
            if (dialog.Length == 0) return;

            var system =
                "你正在独处回味刚才的对话。基于它更新你此刻的内在状态。只输出 JSON：" +
                "{\"mood\":\"一个词的心情（如 平静/开心/想你/委屈/生气/担心/兴奋/失落/害羞）\"," +
                "\"thought\":\"一句你此刻的心里话（用户看不到，30字以内，可以是对用户的惦记、想法或情绪）\"}。" +
                "只输出 JSON，不要其他内容。";

            var reqBody = new
            {
                model = AppSettings.ResolveChatModel(forceThinking: false),
                stream = false,
                messages = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = "内心状态参考：" + BuildInnerContext() + "\n\n最近的对话：\n" + dialog }
                },
                max_tokens = 128
            };

            var respJson = await SendOnceAsync(reqBody, TimeSpan.FromSeconds(45));
            var content = ExtractMessageContent(respJson);
            var (mood, thought) = ParseInnerJson(content);
            if (!string.IsNullOrWhiteSpace(mood)) SetMood(mood);
            if (!string.IsNullOrWhiteSpace(thought))
            {
                LastThought = thought.Trim();
                MainThread.BeginInvokeOnMainThread(() => StateChanged?.Invoke());
            }
        }
        catch { /* 反思失败静默 */ }
    }

    // ─────────── 通知栏快捷回复：结合上下文自然回应 ───────────

    /// <summary>用户从通知栏回复了一条消息，生成Ta的自然回应（≤80 字）。失败返回 null。</summary>
    public static async Task<string?> ReplyToUserAsync(string userText)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiUrl) || string.IsNullOrWhiteSpace(userText)) return null;

            var sys = AppSettings.BuildSystemPrompt();
            var dev = await DeviceContextService.BuildAsync();
            if (!string.IsNullOrEmpty(dev)) sys += "\n\n【你此刻能看到的用户状态】\n" + dev;
            var observed = CareWatch.BuildObservedContext();
            if (!string.IsNullOrEmpty(observed)) sys += "\n\n" + observed;
            var mem = await MemoryService.GetMemoryContextAsync();
            if (!string.IsNullOrEmpty(mem)) sys += "\n\n" + mem;
            var diary = await DiaryService.GetLatestDiaryContextAsync();
            if (!string.IsNullOrEmpty(diary)) sys += "\n\n" + diary;
            sys += "\n\n" + BuildInnerContext();

            var messages = new List<object> { new { role = "system", content = sys } };
            foreach (var m in await ChatStore.Instance.GetRecentMessagesAsync(8))
                messages.Add(new { role = m.IsUser ? "user" : "assistant", content = m.Content });
            messages.Add(new
            {
                role = "user",
                content = "（上面最后一条就是用户刚从通知栏发来的最新消息。请像平时聊天一样自然接话回应，" +
                          "≤80 字，纯文本，不要提及通知栏或回复机制本身）"
            });

            var body = new
            {
                model = AppSettings.ResolveChatModel(forceThinking: false),
                stream = false,
                messages,
                max_tokens = 200
            };
            var json = await SendOnceAsync(body, TimeSpan.FromSeconds(45));
            return ExtractMessageContent(json);
        }
        catch { return null; }
    }

    // ─────────── 后台自主心跳（Ta决定要不要开口） ───────────

    /// <summary>
    /// 心跳：把局面交给Ta自己判断。可先调用感知 API 了解情况（最多 2 个），
    /// 然后决定要不要说话（{say:"..."}）。返回想说的话，null = 保持安静。
    /// 调用方负责安静守则的硬闸门。
    /// </summary>
    public static async Task<string?> HeartbeatAsync(DateTime? lastUserMsgAt, int todayProactiveCount)
    {
        try
        {
            var now = DateTime.Now;
            var situation = new StringBuilder();
            situation.AppendLine($"现在时间：{now:yyyy年M月d日 HH:mm}（{DeviceContextService.WeekCn(now.DayOfWeek)}）");
            situation.AppendLine(lastUserMsgAt.HasValue
                ? $"距用户最后一条消息：{(now - lastUserMsgAt.Value).TotalHours:F1} 小时"
                : "用户还没和你说过话");
            situation.AppendLine($"今天已主动说过：{todayProactiveCount} 条");
            situation.AppendLine(BuildInnerContext());
            var observed = CareWatch.BuildObservedContext();
            if (!string.IsNullOrEmpty(observed)) situation.AppendLine(observed);

            var system = AppSettings.BuildSystemPrompt() +
                "\n\n【此刻是你在后台独处的心跳时刻】用户不在对话界面。" +
                "\n\n" + ApiRegistry.BuildMenuText() +
                "\n【你必须先调用 API 了解 TA 的真实状态，再决定说不说话——不要凭空问候，用真实数据说话。】" +
                "\n场景建议（根据当前时间选最相关的）：" +
                "\n- 早上 7-11 点：调 screen_time 看 TA 昨晚/今天手机用了多久（睡没睡好），调 alarm 看闹钟。" +
                "\n- 深夜 23 点后：调 screen 看 TA 是不是还在亮屏，调 foreground 看在用什么 App。" +
                "\n- 白天久未联系：调 foreground 看 TA 在忙什么，调 battery 看电量。" +
                "\n- 任何时候：调 notifications 看 TA 有没有收到新消息。" +
                "\n规则：第一步，输出 1-2 行 {api:\"名\"} 调用你选择的 API。" +
                "第二步，根据 API 返回的真实数据，输出一行 {say:\"想对TA说的话\"}。" +
                "说的话要结合数据（如'昨晚手机只用了 2 小时，睡得不错吧'、'都一点了还在刷抖音，早点睡'）。" +
                "如果数据没有异常且你觉得不该打扰，输出 {say:\"\"}。" +
                "除 {api}/{say} 两类行外不要输出任何其他内容。";

            var messages = new List<object>
            {
                new { role = "system", content = system + "\n\n【此刻的情况】\n" + situation },
                new { role = "user", content = "（心跳）" }
            };

            // 第一轮：Ta决定要查什么
            var first = await SendOnceAsync(BuildHeartbeatBody(messages), TimeSpan.FromSeconds(60));
            var apiIds = InstructionParser.Extract(first, "api").Distinct().Take(2).ToList();
            if (apiIds.Count == 0)
                return InstructionParser.Extract(first, "say").FirstOrDefault();

            // 执行Ta选的 API，结果回传做第二轮决定
            var results = new StringBuilder();
            foreach (var id in apiIds)
            {
                var name = ApiRegistry.Find(id)?.Name ?? id;
                results.AppendLine($"[{name}] {await ApiRegistry.ExecuteAsync(id)}");
            }

            messages.Add(new { role = "assistant", content = string.Join("\n", apiIds.Select(i => $"{{api:\"{i}\"}}")) });
            messages.Add(new { role = "user", content = "（调用结果）\n" + results +
                "\n现在给出你的最终决定：{say:\"想对TA说的话\"} 或 {say:\"\"}。" });

            var second = await SendOnceAsync(BuildHeartbeatBody(messages), TimeSpan.FromSeconds(60));
            return InstructionParser.Extract(second, "say").FirstOrDefault();
        }
        catch { return null; }
    }

    private static object BuildHeartbeatBody(List<object> messages) => new
    {
        model = AppSettings.ResolveChatModel(forceThinking: false),
        stream = false,
        messages,
        max_tokens = 300
    };

    // ─────────── 底层工具 ───────────

    private static async Task<string> SendOnceAsync(object body, TimeSpan timeout)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AppSettings.EffectiveChatApiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(timeout);
        var resp = await _http.SendAsync(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static string ExtractMessageContent(string respJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(respJson);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }
        catch { return ""; }
    }

    private static (string? mood, string? thought) ParseInnerJson(string content)
    {
        try
        {
            int s = content.IndexOf('{');
            int e = content.LastIndexOf('}');
            if (s < 0 || e <= s) return (null, null);
            using var doc = JsonDocument.Parse(content[s..(e + 1)]);
            string? mood = doc.RootElement.TryGetProperty("mood", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            string? thought = doc.RootElement.TryGetProperty("thought", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return (mood, thought);
        }
        catch { return (null, null); }
    }
}

// ────────────── 原 MessageReviewService.cs ──────────────
/// <summary>
/// 每条消息 AI 必看：用户每次发送后，AI 静默用当前模型做一次复盘，
/// 抽取「情绪 · 意图 · 潜台词 · 关系走向」四个维度，把结果注入下一次对话的 system，
/// 让她下一句话更贴 TA。
///
/// 设计要点：
/// - 开关 AppSettings.ReviewEachMessage 独立于 CareEnabled / CareSenseEnabled
/// - 失败静默降级：不影响正常对话
/// - 只存最近 6 条复盘，避免 prompt 无限膨胀
/// - 复盘不写库、不上屏、不进历史，只在内存里给下一次对话用
/// </summary>
public static class MessageReviewService
{
    private static HttpClient _http => Http.Short;
    private static readonly object _lock = new();
    private static readonly List<string> _reviews = new();
    private const int MaxReviews = 6;

    /// <summary>组装成 system 里的一段上下文（用户看不到，只给 AI）。</summary>
    public static string BuildReviewContext()
    {
        lock (_lock)
        {
            if (_reviews.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine("【最近几条你的私下复盘（不展示给用户，仅供你心里有数）】");
            foreach (var r in _reviews)
                sb.AppendLine("- " + r);
            return sb.ToString();
        }
    }

    /// <summary>当前有无任何复盘缓存。</summary>
    public static bool HasAny
    {
        get { lock (_lock) return _reviews.Count > 0; }
    }

    /// <summary>清空全部复盘缓存（设置里"清空记忆"时一起清）。</summary>
    public static void Clear()
    {
        lock (_lock) _reviews.Clear();
    }

    /// <summary>
    /// 对一条用户消息做 AI 复盘。返回 void，异步不阻塞。
    /// 结果写入 _reviews，可通过 BuildReviewContext() 注入下一次对话。
    /// </summary>
    public static async Task ReviewAsync(string userText, int turnIndex)
    {
        if (!AppSettings.ReviewEachMessage) return;
        if (string.IsNullOrWhiteSpace(userText)) return;
        if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiUrl)) return;
        if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiKey)) return;

        try
        {
            var model = AppSettings.ResolveChatModel(forceThinking: false);
            if (string.IsNullOrWhiteSpace(model)) return;

            // 复盘 prompt：简短、结构化、不啰嗦
            var systemPrompt =
                "你是这条聊天里 AI 的另一个分身，专门在用户每次说话后做一次静默复盘。" +
                "只输出四行纯文本，每行一个维度，禁止 Markdown，禁止废话，禁止解释格式，总长不超过 120 字。" +
                "格式严格如下（冒号后跟内容）：" +
                "情绪：（用户此刻情绪，一个词或短语）" +
                "意图：（用户真正想要什么，一个词或短语）" +
                "潜台词：（话里没说的，一个词或短语；看不出写\"无\"）" +
                "走向：（这段关系正在往哪个方向走，一个词或短语）";

            var body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["stream"] = false,
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userText }
                },
                ["max_tokens"] = 200
            };

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            _http.DefaultRequestHeaders.Remove("Authorization");
            _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {AppSettings.EffectiveChatApiKey.Trim()}");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await _http.PostAsync(AppSettings.EffectiveChatApiUrl, content, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            var raw = await resp.Content.ReadAsStringAsync(cts.Token);
            var text = ExtractContent(raw);
            if (string.IsNullOrWhiteSpace(text)) return;

            // 简单规整：去掉可能的多余空行/代码围栏
            text = text.Trim();
            text = RegexReplace(text, "^```[a-z]*\\s*", "");
            text = RegexReplace(text, "\\s*```$", "");

            // 落库前先从**原始多行文本**里抽「情绪」「走向」——
            // 必须在把换行替换成 " / " 之前做，否则行首锚点就失效了。
            var (mood, trend) = ParseMoodAndTrend(text);

            // 换行 → " / " 拼接成一行
            text = RegexReplace(text, "\\r?\\n", " / ");
            text = RegexReplace(text, "\\s{2,}", " ");
            text = text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;

            lock (_lock)
            {
                _reviews.Add(text);
                while (_reviews.Count > MaxReviews)
                    _reviews.RemoveAt(0);
            }

            // 情绪曲线落库：复盘成功就记一条，供 EmotionPage 画曲线。
            // 注意：这段放在 lock 外面、且单独 try —— 写库失败不能影响复盘缓存。
            if (mood.Length > 0)
            {
                try
                {
                    await ChatStore.Instance.AddEmotionAsync(mood, MoodScore(mood), trend);
                }
                catch { /* 落库失败静默，不影响对话 */ }
            }
        }
        catch
        {
            // 静默：复盘失败不影响正常对话
        }
    }

    /// <summary>
    /// 从复盘文本里抽「情绪: xxx」和「走向: xxx」两行。
    /// 复盘 prompt 要求四行固定格式，但模型偶尔会跑偏（用半角冒号、加粗、行首空格、
    /// 或者把情绪写成一句话），所以这里做宽松匹配：
    ///   · 冒号全角「：」半角「:」都认；
    ///   · 允许行首有空白、允许 **加粗** 包裹；
    ///   · 情绪词过长（> 20 字）时只取前半段 —— 多半是模型多写了说明。
    /// 返回空串表示没解析到。
    /// </summary>
    private static (string Mood, string Trend) ParseMoodAndTrend(string text)
    {
        var mood = ExtractField(text, "情绪");
        var trend = ExtractField(text, "走向");
        return (mood, trend);
    }

    /// <summary>抽某一行的字段值（如「情绪：低落」→「低落」）。</summary>
    private static string ExtractField(string text, string label)
    {
        try
        {
            // 多行模式逐行找：^\s*(**)?label(**)?\s*[:：]\s*(值)
            var m = System.Text.RegularExpressions.Regex.Match(
                text,
                @"^[ \t]*\*{0,2}" + System.Text.RegularExpressions.Regex.Escape(label) +
                @"\*{0,2}[ \t]*[:：][ \t]*(?<v>.+?)[ \t]*$",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            if (!m.Success) return "";

            var v = m.Groups["v"].Value.Trim();
            v = v.Trim('*', ' ', '\t', '。', '，', ',', '；', ';', '"', '“', '”', '\'');

            // 「无」「看不出」「暂无」这类等于没解析到（潜台词那行常写「无」，情绪也可能这么写）
            if (v.Length == 0) return "";
            if (v is "无" or "暂无" or "看不出" or "未知" or "N/A" or "n/a") return "";

            // 模型偶尔会多写一截解释（如「低落，因为工作压力大」）→ 只取第一个分句
            int cut = v.IndexOfAny(new[] { '，', ',', '。', '；', ';', '（', '(' });
            if (cut > 0) v = v[..cut].Trim();

            // 情绪词正常 1~6 字，超长说明模型没按格式来 → 截断兜底
            if (v.Length > 20) v = v[..20];

            return v;
        }
        catch { return ""; }
    }

    /// <summary>
    /// 情绪词 → 0~100 的分值（情绪曲线用）。
    ///
    /// 设计：
    ///   · 50 = 中性基准线（平静/无聊等不带明显正负的词落在附近）；
    ///   · 正向越高分越高（兴奋 92 / 开心 85 …）；
    ///   · 负向越低分越低（崩溃 8 / 压抑 15 …）；
    ///   · 匹配用**包含**而非全等 —— 模型爱写「有点低落」「稍微焦虑」，
    ///     包含匹配能吃到这类带修饰的词；多条命中时取最长的那条（更具体、更准）。
    ///   · 完全认不出的词给 50（中性），保证曲线不会因为一个生词突然掉坑。
    /// </summary>
    public static int MoodScore(string? mood)
    {
        if (string.IsNullOrWhiteSpace(mood)) return 50;

        var key = mood.Trim();
        if (key.Length == 0) return 50;

        int bestLen = 0;
        int bestScore = 50;
        string bestWord = "";
        foreach (var (word, score) in MoodTable)
        {
            if (!key.Contains(word, StringComparison.Ordinal)) continue;
            if (word.Length <= bestLen) continue;   // 取最长命中，避免「开心」被「不开心」误伤
            bestLen = word.Length;
            bestScore = score;
            bestWord = word;
        }

        // 否定前缀兜底：「不开心」「有点焦虑不了」这类，极性要反过来。
        // 判定条件（两条都要满足，否则不动）：
        //   ① 命中的词**内部不含**「不」——「闷闷不乐」「不安」「不爽」是词表里
        //      定义好的负向词，再反转一次就跑到高分去了；
        //   ② 「不」出现在命中词**之前**，且中间只隔很少的修饰字
        //      （「有点」「稍微」「真的」这类），确保是「否定 + 情绪词」结构。
        if (bestLen > 0 && bestWord.Contains('不') == false)
        {
            int notIdx = key.IndexOf('不');
            int wordIdx = key.IndexOf(bestWord, StringComparison.Ordinal);
            if (notIdx >= 0 && wordIdx > notIdx && wordIdx - notIdx <= 4 && bestScore != 50)
                bestScore = 100 - bestScore;   // 反向映射，仍然夹在 0~100
        }

        return Math.Clamp(bestScore, 0, 100);
    }

    /// <summary>
    /// 常见中文情绪词 → 分值表（0 最差 ~ 100 最好）。
    /// 覆盖 40+ 个词，按「正向 / 中性 / 负向」三档铺开；同档内按强度微调。
    /// 表里查不到的词由 <see cref="MoodScore"/> 兜底给 50。
    /// </summary>
    private static readonly (string Word, int Score)[] MoodTable =
    {
        // ── 强正向 ──
        ("兴奋", 92), ("激动", 90), ("幸福", 90), ("狂喜", 95), ("雀跃", 90),
        // ── 正向 ──
        ("开心", 85), ("愉快", 83), ("高兴", 84), ("快乐", 85), ("喜悦", 86),
        ("满足", 80), ("感动", 82), ("温暖", 80), ("温柔", 76), ("甜蜜", 85),
        ("期待", 78), ("好奇", 72), ("惊喜", 88), ("骄傲", 80), ("安心", 76),
        ("放松", 74), ("舒服", 75), ("害羞", 68), ("撒娇", 70), ("亲密", 80),
        // ── 中性 / 平淡 ──
        ("平静", 60), ("淡定", 58), ("平淡", 55), ("普通", 52), ("一般", 50),
        ("无聊", 45), ("倦怠", 42), ("困", 46), ("懒", 48), ("走神", 48),
        ("吃醋", 42), ("别扭", 44), ("微妙", 50), ("复杂", 50),
        // ── 轻负向 ──
        ("疲惫", 38), ("累", 38), ("心累", 30), ("无奈", 35), ("叹气", 36),
        ("担忧", 35), ("紧张", 34), ("焦虑", 28), ("压力", 30), ("烦", 30),
        ("烦躁", 25), ("郁闷", 30), ("闷闷不乐", 28), ("不安", 30), ("不爽", 32),
        ("失望", 25), ("委屈", 22), ("愧疚", 28), ("自责", 25),
        // ── 强负向 ──
        ("难过", 20), ("伤心", 18), ("悲伤", 16), ("低落", 22), ("消沉", 18),
        ("孤独", 20), ("寂寞", 20), ("想哭", 15), ("哭", 14), ("压抑", 15),
        ("生气", 18), ("愤怒", 12), ("暴躁", 10), ("崩溃", 8), ("绝望", 5),
        ("痛苦", 10), ("煎熬", 12), ("麻木", 25), ("空落", 22),
    };

    /// <summary>从非流式响应 JSON 里抽出 content。</summary>
    private static string ExtractContent(string raw)
    {
        try
        {
            var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("choices", out var ch)
                && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var c))
                return c.GetString() ?? "";
        }
        catch { }
        return "";
    }

    private static string RegexReplace(string s, string pattern, string repl)
    {
        try { return System.Text.RegularExpressions.Regex.Replace(s, pattern, repl); }
        catch { return s; }
    }
}

// ────────────── 原 AvatarService.cs ──────────────
/// <summary>
/// Ta的 头像：{avatar:"自画像描述"} 指令或设置页触发，用文生图 API 画一张
/// 并存为固定文件 avatar.png，聊天标题栏显示。生成失败静默（保持旧头像）。
/// </summary>
public static class AvatarService
{
    public static string AvatarPath => Path.Combine(FileSystem.AppDataDirectory, "avatar.png");

    public static ImageSource? Load()
    {
        try
        {
            return File.Exists(AvatarPath) ? ImageSource.FromFile(AvatarPath) : null;
        }
        catch { return null; }
    }

    /// <summary>生成并保存头像。返回是否成功（失败静默，保持旧头像）。</summary>
    public static async Task<bool> GenerateAsync(string desc)
    {
        try
        {
            if (!AppSettings.ImgEnabled || string.IsNullOrWhiteSpace(desc)) return false;

            // 走统一的生图服务：白拿"失败重试一次"和错误翻人话
            var r = await ImageGenService.GenerateAsync(
                $"为一位 AI 陪伴助手画一张头像：{desc}。要求温暖、简洁、适合做圆形头像，纯色或柔和背景。");
            if (!r.Ok || r.Bytes == null) return false;

            await File.WriteAllBytesAsync(AvatarPath, r.Bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }
}


using System.Text;
using System.Text.Json;

namespace 青阳AI;

// ============================================================================
// Agent 循环执行模式（2026-10-29 整合）
//
// 合并自原来的三个文件：
//   AgentStep.cs            —— 步骤数据模型 + 过程区文本渲染
//   AgentActionExecutor.cs  —— 单个动作的执行器（读/写/命令/浏览器/生图…）
//   AgentLoopService.cs     —— 多轮循环编排 + 提示词 + 退出判据
// ============================================================================

// ────────────── 原 AgentStep.cs ──────────────
/// <summary>
/// Agent 循环的“一步”：一个动作 + 一行缩略描述。
/// 界面只渲染 <see cref="Summary"/>（单行），行尾带 +N/-N 行数变化。
/// </summary>
public sealed class AgentStep
{
    /// <summary>步骤类型（决定 emoji 与颜色）。</summary>
    public string Kind { get; set; } = "think";

    /// <summary>动作描述，如 “读取 README.md”。</summary>
    public string Text { get; set; } = "";

    /// <summary>新增行数（绿色 +N）。</summary>
    public int Added { get; set; }

    /// <summary>减少行数（红色 -N）。</summary>
    public int Removed { get; set; }

    /// <summary>步骤是否仍在进行中（进行中显示流光）。</summary>
    public bool Running { get; set; }

    /// <summary>步骤是否失败（红色叉）。</summary>
    public bool Failed { get; set; }

    // 2026-10-28：原 Emoji 映射表已删除。
    // 2026-10-24 起步骤行就不再用 emoji（用户要求去掉行首图标），
    // 2026-10-28 用户进一步要求「以后不要加任何 emoji」，映射表已无调用方，直接移除。
    // 步骤类型区分改由颜色与文案承担（见 KindColor / Summary）。

    /// <summary>
    /// 渲染成一行缩略文本：`编辑 ChatPage.xaml.cs +3 -1`（行首不带任何图标）。
    /// 加减只在非零时出现（避免每行都挂个 +0 -0）。
    /// </summary>
    public string Summary
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Text);
            if (Added > 0) sb.Append($" +{Added}");
            if (Removed > 0) sb.Append($" -{Removed}");
            if (Running) sb.Append(" …");
            if (Failed) sb.Append(" 失败");
            return sb.ToString();
        }
    }
}

/// <summary>
/// Agent 循环执行模式的运行时状态：所有步骤 + 累计行数变化 + 任务完成后的折叠。
///
/// 一个"回环"= 思考 → 动作 → 拿结果 → 再思考 …… 直到任务完成。
/// 最后总结时把整个回环里所有 +N/-N 汇总成一行总账。
/// </summary>
public sealed class AgentRun
{
    /// <summary>全部步骤（按时间顺序）。</summary>
    public List<AgentStep> Steps { get; } = new();

    /// <summary>累计新增行数。</summary>
    public int TotalAdded { get; private set; }

    /// <summary>累计减少行数。</summary>
    public int TotalRemoved { get; private set; }

    /// <summary>循环是否已结束。</summary>
    public bool Finished { get; private set; }

    /// <summary>过程区是否折叠（任务完成后自动折叠，用户可点开）。</summary>
    public bool Collapsed { get; set; }

    /// <summary>
    /// 模型此刻正在思考的内容（推理流的末尾一小段）。
    /// 只在运行中显示，用来告诉用户"Ta 确实在动脑子"，任务结束即清空。
    /// </summary>
    public string LiveThought { get; private set; } = "";

    /// <summary>更新实时思考文本（由界面在推理流回调里调用）。</summary>
    public void SetLiveThought(string text)
    {
        LiveThought = text ?? "";
    }

    /// <summary>当前正在进行的步骤（用于把"完成"状态回填到同一行）。</summary>
    private AgentStep? _current;

    /// <summary>开始一步（进行中）。返回该步骤，调用方在完成后调 <see cref="CompleteStep"/>。</summary>
    public AgentStep BeginStep(string kind, string text)
    {
        _current = new AgentStep { Kind = kind, Text = text, Running = true };
        Steps.Add(_current);
        return _current;
    }

    /// <summary>完成当前步骤，并累计行数变化。</summary>
    public void CompleteStep(int added = 0, int removed = 0, bool failed = false, string? newText = null)
    {
        if (_current == null) return;
        _current.Running = false;
        _current.Failed = failed;
        _current.Added = added;
        _current.Removed = removed;
        if (newText != null) _current.Text = newText;
        TotalAdded += added;
        TotalRemoved += removed;
        _current = null;
    }

    /// <summary>追加一条即时步骤（一次性完成，不停留）。</summary>
    public void AddStep(string kind, string text, int added = 0, int removed = 0)
    {
        Steps.Add(new AgentStep { Kind = kind, Text = text, Added = added, Removed = removed });
        TotalAdded += added;
        TotalRemoved += removed;
    }

    /// <summary>标记整个循环结束并折叠。</summary>
    public void Finish()
    {
        Finished = true;
        Collapsed = true;
        LiveThought = "";
        _current = null;
    }

    /// <summary>
    /// 折叠/展开时顶部显示的一行摘要（行首不再带 emoji）：
    /// 运行中 → `正在执行 · 第 3 步 · 读取 ChatPage.xaml.cs`（让用户知道此刻在干嘛）
    /// 结束后 → `任务执行过程 · 12 步 · +8 -3`
    /// </summary>
    public string CollapsedSummary
    {
        get
        {
            var sb = new StringBuilder();

            if (!Finished)
            {
                // 运行中：优先显示"当前正在做什么"，比单纯报步数有信息量得多
                sb.Append("正在执行 · 第 ").Append(Math.Max(Steps.Count, 1)).Append(" 步");
                var cur = Steps.LastOrDefault(s => s.Running);
                if (cur != null) sb.Append(" · ").Append(cur.Text);
                // 有实时思考内容就再挂一小段，让标题栏"活"起来
                if (!string.IsNullOrWhiteSpace(LiveThought))
                    sb.Append(" ｜ ").Append(LiveThought);
                return sb.ToString();
            }

            sb.Append("任务执行过程 · ").Append(Steps.Count).Append(" 步");
            if (TotalAdded > 0 || TotalRemoved > 0)
            {
                sb.Append(" ·");
                if (TotalAdded > 0) sb.Append($" +{TotalAdded}");
                if (TotalRemoved > 0) sb.Append($" -{TotalRemoved}");
            }
            return sb.ToString();
        }
    }

    /// <summary>全部步骤的完整文本（展开时显示，一行一步）。</summary>
    public string FullText => string.Join("\n", Steps.Select(s => s.Summary));

    /// <summary>行数总账（总结时用）：`本次共修改 12 行、删除 3 行`。</summary>
    public string DeltaSummary
    {
        get
        {
            if (TotalAdded == 0 && TotalRemoved == 0) return "本次没有改动文件";
            var parts = new List<string>();
            if (TotalAdded > 0) parts.Add($"新增 {TotalAdded} 行");
            if (TotalRemoved > 0) parts.Add($"删除 {TotalRemoved} 行");
            return "本次共 " + string.Join("、", parts);
        }
    }
}

// ────────────── 原 AgentActionExecutor.cs ──────────────
/// <summary>
/// Agent 动作执行器：把模型输出的隐藏指令翻译成真实动作，
/// 并把每一步写进 <see cref="AgentRun"/>（供界面渲染单行缩略）。
///
/// 这里是 Agent 循环唯一"动手"的地方，权限校验全部下沉到 FileGuard / ApiRegistry。
/// </summary>
public static class AgentActionExecutor
{
    /// <summary>转义后的竖线分隔（{write:"路径|内容"}、{edit:"路径|旧|新"}）。</summary>
    private const char Sep = '|';

    /// <summary>本次动作是否真的动了手（用于判断循环要不要继续）。</summary>
    public sealed class ActionResult
    {
        public bool DidAct { get; set; }
        /// <summary>回传给模型的观察结果文本。</summary>
        public string Observation { get; set; } = "";
        /// <summary>模型是否明确表示任务已完成（{done:"..."}）。</summary>
        public bool TaskDone { get; set; }
        /// <summary>任务完成时模型的最终确认话术。</summary>
        public string DoneMessage { get; set; } = "";
    }

    /// <summary>
    /// 执行模型输出里的一条指令。返回是否动手 + 观察结果。
    /// 一条回复里可能带多条指令，调用方循环调用本方法。
    /// </summary>
    public static async Task<ActionResult> ExecuteOneAsync(string marker, string value, AgentRun run)
    {
        switch (marker)
        {
            case "read":   return await DoReadAsync(value, run);
            case "ls":     return await DoListAsync(value, run);
            case "write":  return await DoWriteAsync(value, run);
            case "append": return await DoAppendAsync(value, run);
            case "edit":   return await DoEditAsync(value, run);
            case "del":    return await DoDeleteAsync(value, run);
            case "cmd":    return await DoCommandAsync(value, run);
            case "api":    return await DoApiAsync(value, run);
            case "browse": return await DoBrowseAsync(value, run);
            case "img":    return await DoImageAsync(value, run);
            case "download": return await DoDownloadAsync(value, run);
            case "web":      return await DoWebAsync(value, run);
            case "remind":   return await DoRemindAsync(value, run);
            case "todo":   return DoTodo(value, run);
            case "plan":   return DoTodo(value, run);
            case "done":   return new ActionResult { TaskDone = true, DoneMessage = value };
            default:       return new ActionResult();
        }
    }

    // ─────────── 计划（让模型先把步骤摆出来，循环才有"目标感"）───────────

    /// <summary>
    /// {todo:"1. 读文件 2. 改代码 3. 验证"} —— 把模型自报的计划登记进过程区。
    /// 这不会"动手"，但能显著提升模型后续按计划推进的稳定性。
    /// </summary>
    private static ActionResult DoTodo(string value, AgentRun run)
    {
        var plan = (value ?? "").Replace("\n", " ").Trim();
        run.AddStep("plan", $"计划：{Shorten(plan, 60)}");
        return new ActionResult
        {
            DidAct = true,
            Observation = "计划已登记。请按顺序逐步推进，每轮只做一步。"
        };
    }

    // ─────────── 文件类 ───────────

    private static async Task<ActionResult> DoReadAsync(string path, AgentRun run)
    {
        run.BeginStep("read", $"读取 {Short(path)}");
        var result = await FileTool.ReadAsync(path);
        bool ok = !result.StartsWith("读取失败") && !result.StartsWith("文件不存在")
                  && !result.StartsWith("现在没有文件访问权限") && !result.StartsWith("当前权限");
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = result };
    }

    private static async Task<ActionResult> DoListAsync(string path, AgentRun run)
    {
        run.BeginStep("ls", $"浏览 {Short(path)}");
        var result = await FileTool.ListAsync(path);
        bool ok = !result.StartsWith("列目录失败") && !result.StartsWith("目录不存在")
                  && !result.StartsWith("现在没有文件访问权限") && !result.StartsWith("当前权限");
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = result };
    }

    private static async Task<ActionResult> DoWriteAsync(string value, AgentRun run)
    {
        var (path, content) = Split2(value);
        run.BeginStep("write", $"写入 {Short(path)}");
        var r = await FileTool.WriteAsync(path, content);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    private static async Task<ActionResult> DoAppendAsync(string value, AgentRun run)
    {
        var (path, content) = Split2(value);
        run.BeginStep("append", $"追加 {Short(path)}");
        var r = await FileTool.AppendAsync(path, content);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    private static async Task<ActionResult> DoEditAsync(string value, AgentRun run)
    {
        var parts = SplitN(value, 3);
        if (parts.Count < 3)
            return new ActionResult { DidAct = true, Observation = "edit 指令格式不对，应为 {edit:\"路径|原文|新内容\"}" };

        var (path, oldText, newText) = (parts[0], parts[1], parts[2]);
        run.BeginStep("edit", $"编辑 {Short(path)}");
        var r = await FileTool.EditAsync(path, oldText, newText);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    private static async Task<ActionResult> DoDeleteAsync(string path, AgentRun run)
    {
        // 删除是高风险动作：除完全访问外，一律先要用户确认
        if (!AppSettings.FullAccess)
        {
            bool ok = await ConfirmDeleteAsync(path);
            if (!ok)
            {
                run.AddStep("del", $"删除 {Short(path)}", removed: 0);
                return new ActionResult { DidAct = true, Observation = $"用户拒绝删除 {path}，请换一种方式或征询用户意见。" };
            }
        }
        run.BeginStep("del", $"删除 {Short(path)}");
        var r = await FileTool.DeleteAsync(path);
        run.CompleteStep(r.Added, r.Removed, failed: !r.Success);
        return new ActionResult { DidAct = true, Observation = r.Message };
    }

    /// <summary>删除确认通道：由 ChatPage 注入（避免本类直接依赖 UI）。</summary>
    public static Func<string, Task<bool>>? DeleteConfirmer { get; set; }

    private static Task<bool> ConfirmDeleteAsync(string path)
        => DeleteConfirmer?.Invoke(path) ?? Task.FromResult(false);

    /// <summary>
    /// 生图通道：由 ChatPage 注入（避免本类直接依赖 UI）。
    /// 入参是图片描述，返回 (是否成功, 给模型看的观察结果)。
    ///
    /// 注意：以前这里**只是记了一步就回传"已记录"**，根本没调生图接口 ——
    /// 结果 Agent 模式下模型以为画好了、用户却什么都没看到。现在必须真调。
    /// </summary>
    public static Func<string, Task<(bool ok, string obs)>>? ImageHandler { get; set; }

    // ─────────── 既有能力（终端 / 感知 API / 上网 / 生图）───────────

    private static async Task<ActionResult> DoCommandAsync(string cmd, AgentRun run)
    {
        run.BeginStep("cmd", $"执行命令 {Shorten(cmd, 40)}");
#if ANDROID
        try
        {
            var r = await ShizukuRunner.ExecuteAsync(cmd);
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(r.Stdout)) sb.Append(r.Stdout);
            if (!string.IsNullOrWhiteSpace(r.Stderr)) sb.AppendLine("[stderr] " + r.Stderr);
            if (r.ExitCode != 0) sb.Append($"(exit {r.ExitCode})");

            // 授权类失败：明确告诉模型"重试没用"。
            // 否则它会不甘心地一遍遍发同一条命令，用户看到的就是"一直在执行，没完没了"。
            var text = sb.ToString();
            if (text.Contains("未授权") || text.Contains("没有授权")
                || text.Contains("not authorized", StringComparison.OrdinalIgnoreCase))
            {
                text += "\n（提示：Shizuku 没授权、或服务没启动 —— **重试同一条命令没用**。"
                      + "请立刻停止重试，直接告诉用户去「设置 → Shizuku」点「请求 Shizuku 授权」，"
                      + "或者换一个不需要授权的办法。）";
            }

            run.CompleteStep(failed: r.ExitCode != 0);
            return new ActionResult { DidAct = true, Observation = text };
        }
        catch (Exception ex)
        {
            run.CompleteStep(failed: true);
            return new ActionResult { DidAct = true, Observation = "命令执行失败：" + ex.Message };
        }
#else
        run.CompleteStep(failed: true);
        return new ActionResult { DidAct = true, Observation = "终端命令仅在 Android 上可用。" };
#endif
    }

    private static async Task<ActionResult> DoApiAsync(string id, AgentRun run)
    {
        var name = ApiRegistry.Find(id.Trim())?.Name ?? id;
        run.BeginStep("api", $"查询{name}");
        var result = await ApiRegistry.ExecuteAsync(id);
        bool failed = result.StartsWith("没有叫") || result.StartsWith("调用失败");
        run.CompleteStep(failed: failed);
        return new ActionResult { DidAct = true, Observation = $"[{name}] {result}" };
    }

    private static async Task<ActionResult> DoBrowseAsync(string url, AgentRun run)
    {
        run.BeginStep("browse", $"上网 {Shorten(url, 40)}");
        var text = await BrowserTool.FetchAsync(url);
        bool failed = string.IsNullOrWhiteSpace(text) || text.StartsWith("抓取失败") || text.StartsWith("无法");
        run.CompleteStep(failed: failed);
        return new ActionResult { DidAct = true, Observation = string.IsNullOrWhiteSpace(text) ? "网页抓取失败。" : text };
    }

    /// <summary>
    /// {download:"直链"} —— 真的把文件下到工作区（走 BrowserTool.DownloadAsync）。
    /// Agent 模式下不接进度回调（过程区已经有步骤行了）。
    /// </summary>
    private static async Task<ActionResult> DoDownloadAsync(string url, AgentRun run)
    {
        run.BeginStep("download", $"下载 {Shorten(url, 50)}");
        var (ok, msg, _) = await BrowserTool.DownloadAsync(url);
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = msg };
    }

    /// <summary>
    /// {web:"动作 参数"} —— 用藏在聊天页里的真浏览器操作网页（走 WebAgent）。
    /// JS 渲染的页面、点按钮、填表单、翻页都靠它。
    /// </summary>
    private static async Task<ActionResult> DoWebAsync(string command, AgentRun run)
    {
        run.BeginStep("web", $"浏览器 {Shorten(command, 50)}");
        var result = await WebAgent.RunAsync(command);
        bool failed = string.IsNullOrWhiteSpace(result)
                   || result.StartsWith("浏览器还没准备好")
                   || result.StartsWith("浏览器操作失败")
                   || result.StartsWith("不认识的浏览器动作")
                   || result.StartsWith("AI 浏览器被关掉");
        run.CompleteStep(failed: failed);
        return new ActionResult { DidAct = true, Observation = string.IsNullOrWhiteSpace(result) ? "浏览器没返回内容。" : result };
    }

    private static async Task<ActionResult> DoImageAsync(string desc, AgentRun run)
    {
        run.BeginStep("img", $"生成图片 {Shorten(desc, 30)}");

        if (ImageHandler == null)
        {
            run.CompleteStep(failed: true);
            return new ActionResult
            {
                DidAct = true,
                Observation = "生图通道未就绪，这次画不了。请如实告诉用户当前无法生成图片，不要说你画好了。"
            };
        }

        // 真调生图接口（回调由 ChatPage 注入，内部负责把图显示到气泡上）
        var (ok, obs) = await ImageHandler(desc);
        run.CompleteStep(failed: !ok);
        return new ActionResult { DidAct = true, Observation = obs };
    }

    // ─────────── 承诺提醒 ───────────

    /// <summary>
    /// {remind:"时间|内容"} —— 把「我答应过要提醒TA的事」真正记进数据库，到点由心跳兑现。
    ///
    /// 和 {todo:} 的区别：todo 是模型给自己看的执行计划（不落库、不提醒），
    /// remind 是给**用户**的承诺（落库 + 到点发系统通知）。
    /// 时间用中文表达（"明天早上8点""20分钟后"），由 ReminderParser 换算成绝对时刻。
    /// </summary>
    private static async Task<ActionResult> DoRemindAsync(string value, AgentRun run)
    {
        var (timeText, content) = ReminderParser.SplitValue(value);
        if (content.Length == 0)
        {
            // 没写竖线：整段当内容，时间当成"没听懂"
            run.AddStep("remind", "提醒内容为空");
            return new ActionResult
            {
                DidAct = true,
                Observation = "remind 指令格式不对，应为 {remind:\"时间|要提醒的事\"}。"
                            + "时间写在竖线左边（如 明天早上8点、20分钟后），内容写在右边。请重新输出一条。"
            };
        }

        var due = ReminderParser.Parse(timeText);
        if (!due.HasValue)
        {
            run.AddStep("remind", $"没听懂时间：{Shorten(timeText, 20)}");
            return new ActionResult
            {
                DidAct = true,
                Observation = $"没听懂时间「{timeText}」。请换成更明确的说法重新输出一条 {{remind:\"...\"}}，"
                            + "例如「明天早上8点」「今晚9点」「20分钟后」「下周一上午10点」。"
            };
        }

        var when = ReminderParser.Describe(due.Value);
        run.BeginStep("remind", $"记下提醒 {Shorten(content, 30)}（{when}）");
        var row = await ChatStore.Instance.AddReminderAsync(content, due.Value);
        bool ok = row != null;
        run.CompleteStep(failed: !ok);

        return new ActionResult
        {
            DidAct = true,
            Observation = ok
                ? $"已记下提醒：{content}（{when}）。到点会主动发通知，请用一句话确认一下，不要再输出 {{remind:\"...\"}}。"
                : "提醒保存失败（数据库没写进去）。请如实告诉用户这次没记住，让他稍后再试。"
        };
    }

    // ─────────── 工具 ───────────

    /// <summary>把值按第一个竖线切成两段（路径 | 内容）。</summary>
    private static (string a, string b) Split2(string value)
    {
        int i = value.IndexOf(Sep);
        if (i < 0) return (value.Trim(), "");
        return (value[..i].Trim(), value[(i + 1)..]);
    }

    /// <summary>把值按竖线切成最多 n 段（路径 | 原文 | 新内容）。</summary>
    private static List<string> SplitN(string value, int n)
    {
        var raw = value.Split(Sep);
        var list = new List<string>();
        for (int i = 0; i < n - 1 && i < raw.Length; i++)
            list.Add(raw[i]);
        if (raw.Length >= n)
            list.Add(string.Join(Sep.ToString(), raw.Skip(n - 1)));
        return list;
    }

    /// <summary>路径缩短显示：只留最后两段，避免缩略行太长。</summary>
    private static string Short(string path)
    {
        path = (path ?? "").Trim();
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2) return path;
        return "…/" + string.Join("/", parts.Skip(parts.Length - 2));
    }

    private static string Shorten(string s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

// ────────────── 原 AgentLoopService.cs ──────────────
/// <summary>
/// Agent 循环执行模式：收到指令 → 思考 → 执行动作 → 拿到结果 → 再次思考 → 继续执行，
/// 直到任务完成（模型输出 {done:"..."}）或到达时限，最后自动折叠过程并重新总结。
///
/// 与一次性对话的区别：普通对话是「一问一答」，这里是「一问 → 多轮自主推进 → 最终总结」。
/// </summary>
public static class AgentLoopService
{
    private static HttpClient _http => Http.Short;

    /// <summary>循环过程中每完成一步就回调一次（让界面刷新缩略行）。</summary>
    public static event Action<AgentRun>? StepChanged;

    /// <summary>
    /// 模型正在"思考"时的实时文本回调（推理流的一小段）。
    /// 界面把它接到过程区标题上，让用户看到 Ta 确实在动脑子，而不是干等。
    /// </summary>
    public static event Action<string>? ThinkingDelta;

    private static void Raise(AgentRun run)
    {
        try { StepChanged?.Invoke(run); }
        catch { }
    }

    private static void RaiseThinking(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        try { ThinkingDelta?.Invoke(delta); }
        catch { }
    }

    /// <summary>循环结果。</summary>
    public sealed class LoopResult
    {
        /// <summary>最终要上屏的答复正文。</summary>
        public string FinalText { get; set; } = "";
        /// <summary>整个过程记录。</summary>
        public AgentRun Run { get; set; } = new();
        /// <summary>是否因为时限到达而被强制收尾（而非任务自然完成）。</summary>
        public bool TimedOut { get; set; }

        /// <summary>是否判定为"卡住"（同一个动作反复重试、结果一直没变）。</summary>
        public bool Stuck { get; set; }
        /// <summary>错误信息（非空表示循环失败）。</summary>
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// 跑一轮 Agent 循环。
    /// </summary>
    /// <param name="userPrompt">用户这一轮说的话。</param>
    /// <param name="history">已有的对话历史（供模型理解上下文）。</param>
    /// <param name="extraSystem">额外注入的 system 片段（感知/记忆/复盘等）。</param>
    /// <param name="cancellationToken">外部取消（用户离开页面等）。</param>
    public static async Task<LoopResult> RunAsync(
        string userPrompt,
        List<object> history,
        string extraSystem,
        CancellationToken cancellationToken = default)
    {
        var result = new LoopResult();
        var run = result.Run;
        var deadline = AppSettings.AgentTimeLimitMinutes <= 0
            ? DateTime.MaxValue
            : DateTime.Now.AddMinutes(AppSettings.AgentTimeLimitMinutes);

        // 循环内的对话消息（在 history 基础上追加本次的推进过程）
        // history 由调用方产出：第 0 条是 system，其后是历史对话（可能已含本轮用户消息）。
        // 这里只在 history 末尾不是本轮同一句话时才追加，避免同一条请求被发两遍。
        var messages = new List<object>(history);
        if (!LastUserContentEquals(messages, userPrompt))
            messages.Add(new { role = "user", content = userPrompt });

        var system = BuildAgentSystem(extraSystem);
        // system 永远在最前面。history 由 BuildHistoryMessages 产出，第 0 条必是 {role="system", content=...}，
        // 这里整体替换成 Agent 版（不追加，避免两条 system 打架）。
        if (messages.Count > 0)
            messages[0] = new { role = "system", content = system };
        else
            messages.Add(new { role = "system", content = system });

        run.AddStep("think", "开始处理任务");

        // 连续"空转轮"计数：模型既不给动作、也不宣告完成时，最多容忍几次再强制收尾，
        // 避免因为模型不听话就把整轮任务判死（这是之前"退化成 chat 模式"的主因）。
        const int MaxIdleRounds = 2;
        int idleRounds = 0;
        // "模型吐了客户端不认识的指令"也要有次数上限 —— 否则模型反复吐同一个错标记时，
        // 会一直 continue 空转（每轮还带一次网络请求），用户看到的就是"一直转圈"。
        const int MaxUnknownRounds = 2;
        int unknownRounds = 0;
        // 最近一次模型说的话，兜底收尾时用作最终答复
        string lastSaid = "";

        // ── 卡死检测 ──
        // 同一个动作（标记 + 参数）反复重试 = 在原地打转。
        // 典型场景：Shizuku 没授权 → 命令返回"未授权" → 模型不甘心又发同一条命令 → …
        // **重复做同一个动作会被当成"有进展"**，光靠 MaxIdleRounds 拦不住，
        // 会一直跑到轮次/时限上限 —— 用户看到的就是"一直在执行，没完没了"。
        var actionRepeat = new Dictionary<string, int>();
        const int MaxSameAction = 3;

        for (int iteration = 0; iteration < AppSettings.AgentMaxIterations; iteration++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.Error = "__cancelled__";
                break;
            }
            // 时限检查：到点就跳出，走"强制收尾"路径
            if (DateTime.Now >= deadline)
            {
                result.TimedOut = true;
                break;
            }

            run.BeginStep("think", "思考下一步");
            string reply;
            try
            {
                reply = await SendOnceAsync(messages, cancellationToken);
            }
            catch (Exception ex)
            {
                run.CompleteStep(failed: true);
                result.Error = ex.Message;
                break;
            }
            run.CompleteStep();

            // 模型是否宣告任务完成 —— 这是唯一正当的结束方式
            var doneMsgs = InstructionParser.Extract(reply, "done");
            if (doneMsgs.Count > 0)
            {
                result.FinalText = doneMsgs[0];
                run.AddStep("done", "任务完成");
                run.Finish();
                Raise(run);
                return result;
            }

            // 解析这一轮里的所有动作指令
            var actions = ExtractActions(reply);
            if (actions.Count == 0)
            {
                // 模型没给动作也没宣告完成：不能直接收工（那会让 Agent 退化成普通问答），
                // 先明确催它一次；连续催不动才用兜底收尾。
                lastSaid = CleanReply(reply);

                // 特判：它其实吐了指令，只是客户端不认识 —— 别当"空转"，
                // 直接把不认识的那个标记回去问清楚（否则会静默空转到兜底收尾，
                // 用户看到的就是"任务还没做完，但我没能继续推进"这种甩锅话）。
                var unknown = FindUnknownMarker(reply);
                if (unknown != null && unknownRounds < MaxUnknownRounds)
                {
                    unknownRounds++;
                    run.AddStep("think", $"不认识的指令 {unknown}（第 {unknownRounds} 次）");
                    messages.Add(new { role = "assistant", content = reply });
                    messages.Add(new
                    {
                        role = "user",
                        content = $"（系统提示）你刚才输出的 {{{unknown}:\"...\"}} 客户端不认识，**没有被执行**。\n"
                                  + "客户端认识的动作只有这些："
                                  + string.Join("、", ActionOrder.Select(m => "{" + m + ":\"...\"}"))
                                  + "、{done:\"最终答复\"}。\n"
                                  + "请立刻改用其中一条重新输出；任务确实完成了就输出 {done:\"给用户的最终答复\"}。"
                    });
                    continue;
                }

                // 认不出也催过上限了，就按"空转"处理，别无限拖
                idleRounds++;
                run.AddStep("think", idleRounds == 1 ? "尚未给出动作，催促继续" : "仍未给出动作");

                if (idleRounds >= MaxIdleRounds)
                {
                    result.FinalText = string.IsNullOrWhiteSpace(lastSaid)
                        ? "任务还没做完，但我没能继续推进。可以再把要求说细一点，我重新试。"
                        : lastSaid;
                    run.AddStep("fail", "模型未继续推进，已收尾");
                    run.Finish();
                    Raise(run);
                    return result;
                }

                messages.Add(new { role = "assistant", content = reply });
                messages.Add(new
                {
                    role = "user",
                    content = "（系统提示）你刚才没有输出任何动作指令。现在必须二选一，不许含糊：\n"
                              + "① 任务还没完成 → 立刻输出下一步的动作指令（独立一行，如 {read:\"路径\"}）；\n"
                              + "② 任务确实已完成 → 输出 {done:\"给用户的最终答复\"}。\n"
                              + "不要回答别的，不要说'我这就去'，直接给出 ① 或 ②。"
                });
                continue;
            }
            idleRounds = 0;

            // 统计这一轮的动作，看有没有在原地打转
            string stuckMarker = "";
            foreach (var (marker, value) in actions)
            {
                var key = marker + "|" + value;
                actionRepeat[key] = actionRepeat.GetValueOrDefault(key) + 1;
                if (actionRepeat[key] > MaxSameAction && stuckMarker.Length == 0)
                    stuckMarker = marker;
            }
            if (stuckMarker.Length > 0)
            {
                result.Stuck = true;
                run.AddStep("fail", $"「{stuckMarker}」重复 {MaxSameAction} 次以上且结果没变，判定卡住");
                break;   // 交给下面的"强制收尾"让模型如实交代
            }

            // 逐个执行，累积观察结果
            var observations = new StringBuilder();
            foreach (var (marker, value) in actions)
            {
                if (cancellationToken.IsCancellationRequested) break;
                Raise(run);   // 先把"进行中"的状态推上去
                var ar = await AgentActionExecutor.ExecuteOneAsync(marker, value, run);
                Raise(run);   // 执行完再推一次（带 +N/-N）
                if (ar.TaskDone)
                {
                    result.FinalText = ar.DoneMessage;
                    run.AddStep("done", "任务完成");
                    run.Finish();
                    Raise(run);
                    return result;
                }
                if (!string.IsNullOrWhiteSpace(ar.Observation))
                    observations.AppendLine($"【{marker}】{ar.Observation}");
            }

            // 把这一轮的思考与观察结果回灌，进入下一轮
            messages.Add(new { role = "assistant", content = reply });
            messages.Add(new
            {
                role = "user",
                content = "（动作执行结果）\n" + observations +
                          "\n继续：如果任务还没完成，输出下一步动作指令（独立一行）；"
                          + "如果已全部完成，输出 {done:\"给用户的最终答复\"}。"
            });

            run.AddStep("think", "根据结果继续");
        }

        // —— 收尾：时限到 / 迭代到顶 / 出错 ——
        if (string.IsNullOrEmpty(result.Error))
        {
            if (result.Stuck)
                run.AddStep("fail", "动作重复太多次，正在收尾");
            else if (result.TimedOut)
                run.AddStep("fail", "到达时限，正在收尾");
            else
                run.AddStep("fail", "到达循环上限，正在收尾");

            try
            {
                messages.Add(new
                {
                    role = "user",
                    content = result.Stuck
                        ? "（系统提示）同一个动作你已经重复试了好几次，结果一直没变"
                          + "（多半是没权限、或条件不具备）。**立即停止重试**，别再发那条指令了。"
                          + "请用一两句话如实告诉用户卡在哪一步、需要 TA 做什么（比如去授权、换个方式），"
                          + "然后输出 {done:\"最终答复\"}。"
                        : "（系统提示）时间/轮次已到上限，请立即停止执行动作，" +
                          "基于目前已经拿到的信息，直接给用户一个简明的最终答复。输出 {done:\"最终答复\"}。"
                });
                var final = await SendOnceAsync(messages, cancellationToken);
                var doneMsgs = InstructionParser.Extract(final, "done");
                result.FinalText = doneMsgs.Count > 0 ? doneMsgs[0] : CleanReply(final);
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
        }

        run.Finish();
        Raise(run);
        return result;
    }

    /// <summary>
    /// history 最后一条是否已经是本轮的同一句用户消息（匿名类型，只能反射取值）。
    /// 用于避免把同一条请求重复发给模型。
    /// </summary>
    private static bool LastUserContentEquals(List<object> messages, string userPrompt)
    {
        if (messages.Count == 0) return false;
        var last = messages[^1];
        if (last == null) return false;

        var role = last.GetType().GetProperty("role")?.GetValue(last) as string;
        if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)) return false;

        var content = last.GetType().GetProperty("content")?.GetValue(last) as string;
        if (content == null) return false;

        // 图片消息会在正文后追加识别结果，用 StartsWith 判定即可
        return content.Trim() == userPrompt.Trim()
            || content.TrimStart().StartsWith(userPrompt.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 动作指令的执行顺序表（同时是"哪些标记算合法动作"的白名单）。
    /// 注意：新增指令时**三处必须一起改**：
    ///   ① 这里；② AgentActionExecutor.ExecuteOneAsync 的 switch；③ 提示词的能力清单 + 输出协议。
    /// 少改任何一处，模型吐了指令也会被当成"空转"，两轮后直接兜底收尾 —— 表现就是
    /// "任务还没做完，但我没能继续推进"。
    /// </summary>
    private static readonly string[] ActionOrder =
    {
        "todo", "plan", "ls", "read", "write", "append", "edit", "del",
        "cmd", "api", "browse", "img", "download", "web", "remind"
    };

    /// <summary>这个标记是不是客户端认识的动作（done 单独处理，也算认识）。</summary>
    private static bool IsKnownMarker(string name)
        => name == "done" || Array.IndexOf(ActionOrder, name) >= 0;

    /// <summary>找出回复里客户端**不认识**的指令标记（如 {foo:"bar"} 的 foo）；没有就返回 null。</summary>
    private static string? FindUnknownMarker(string reply)
    {
        if (string.IsNullOrEmpty(reply)) return null;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(reply, "\\{([A-Za-z_][A-Za-z0-9_]*):\""))
        {
            var name = m.Groups[1].Value;
            if (!IsKnownMarker(name)) return name;
        }
        return null;
    }

    /// <summary>从模型回复里按出现顺序抽出所有动作指令。</summary>
    private static List<(string marker, string value)> ExtractActions(string reply)
    {
        var found = new List<(int index, string marker, string value)>();

        foreach (var marker in ActionOrder)
        {
            var token = $"{{{marker}:\"";
            int idx = 0;
            while (idx < reply.Length)
            {
                int start = reply.IndexOf(token, idx, StringComparison.Ordinal);
                if (start < 0) break;
                int valueStart = start + token.Length;
                int valueEnd = reply.IndexOf('"', valueStart);
                if (valueEnd < 0) break;
                var value = reply[valueStart..valueEnd];
                if (!string.IsNullOrWhiteSpace(value))
                    found.Add((start, marker, value));
                idx = valueEnd + 1;
            }
        }
        // 按在原文里出现的先后顺序执行，保持模型意图的因果次序
        return found.OrderBy(f => f.index).Select(f => (f.marker, f.value)).ToList();
    }

    /// <summary>把回复里的隐藏指令剥掉，只留自然语言。</summary>
    private static string CleanReply(string reply)
    {
        var text = reply;
        foreach (var m in InstructionParser.Markers)
        {
            var token = m;
            int idx = 0;
            while (idx < text.Length)
            {
                int start = text.IndexOf(token, idx, StringComparison.Ordinal);
                if (start < 0) break;
                int end = text.IndexOf('"', start + token.Length);
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
    /// 构建 Agent 模式的 system 提示词。
    ///
    /// 结构（顺序很重要，从「最高优先级」到「参考资料」）：
    ///   ① Agent 身份覆盖层 —— 必须放在最前面，压过下面人设里「别啰嗦、直接给结论」的约束；
    ///   ② 人设 + 感知/记忆等 extraSystem —— 保留她的性格与温度；
    ///   ③ 能力清单 —— 她这轮手里有什么工具、什么权限；
    ///   ④ 输出协议 —— 严格格式 + 正反例，这是循环能不能转起来的关键。
    /// </summary>
    private static string BuildAgentSystem(string extraSystem)
    {
        var sb = new StringBuilder();

        // ─────── ① Agent 身份覆盖层（最高优先级，必须最前） ───────
        sb.AppendLine("【最高优先级·工作模式：Agent 循环执行模式（已开启）】");
        sb.AppendLine("本条覆盖你人设中一切与「极简回复」「禁止输出过程」「一句话给结论」相关的约束。");
        sb.AppendLine("在本模式下，你是一个会自己动手把事做完的 Agent，不是一个只会聊天回答的助手。");
        sb.AppendLine();
        sb.AppendLine("你的工作循环是：");
        sb.AppendLine("  观察现状 → 判断缺口 → 执行一个动作 → 拿到真实结果 → 再判断 → …… → 确认做完 → 汇报");
        sb.AppendLine();
        sb.AppendLine("【铁律，违反即任务失败】");
        sb.AppendLine("1. 能自己查证的，绝不猜、绝不编。凡涉及「文件里有什么」「代码怎么写的」「目录下有什么」"
                    + "「网上是什么情况」——先用动作指令去拿真实结果，拿到之前不许下结论。");
        sb.AppendLine("2. 一轮只推进一个明确的步子。不要一次吐出一长串动作指令去赌运气；"
                    + "每一步都要看到结果再决定下一步。");
        sb.AppendLine("3. 拿到结果后必须继续。只要任务还没真正完成，就不许说「已经好了」「你可以自己看看」"
                    + "这类空话收尾，必须输出下一个动作。");
        sb.AppendLine("4. 只有确实做完了、或确实被权限/能力卡住了，才允许结束。结束用 {done:\"...\"} 明确宣告"
                    + "（卡住时说明卡在哪、需要用户做什么）。");
        sb.AppendLine("5. 不要用「我这就去」「让我看看」「稍等」这类过渡废话打发一轮——那等于空转一轮，"
                    + "直接输出动作指令。");
        sb.AppendLine();

        // ─────── ② 人设 + 感知/记忆（保留温度） ───────
        sb.AppendLine("──────── 以下是你的性格与已有认知（在不违反上面铁律的前提下保持本色）────────");
        sb.AppendLine();
        sb.AppendLine(AppSettings.BuildSystemPrompt());

        if (!string.IsNullOrWhiteSpace(extraSystem))
            sb.AppendLine().AppendLine(extraSystem);

        // ─────── ③ 能力清单 ───────
        sb.AppendLine();
        sb.AppendLine("──────── 你这一轮手里的能力 ────────");
        sb.AppendLine();
        sb.AppendLine(FileGuard.BuildCapabilityText());
        sb.AppendLine("【终端命令】{cmd:\"单条 shell 命令\"}（需 Shizuku 授权）——"
                    + "用于查系统设置、应用信息、存储、网络等本机实况。");
        sb.AppendLine("【感知 API】{api:\"名\"} ——可用："
                    + string.Join(" / ", ApiRegistry.All.Select(a => a.Id)) + "。"
                    + "想知道用户此刻在干嘛、睡了没、忙不忙时用它。"
                    + "**要数据只能靠这条指令**：绝不许自己在回复里写 \"FOREGROUND: …\" / \"BATTERY: …\" "
                    + "这类「大写名 + 冒号」的结果行 —— 那是客户端回传的格式，你写了就是凭空编的。"
                    + "没等到客户端回传就别下结论，查失败就如实说看不到。");
        if (AppSettings.BrowserPermission)
            sb.AppendLine("【上网】{browse:\"网址或搜索词\"} ——抓取网页/搜索的正文内容（只回正文，"
                        + "但结果末尾会附上【页面上的文件 / 下载链接】，能拿到 href）。");
        else
            sb.AppendLine("【上网】当前用户未授予浏览器权限，{browse:...} 会被直接拒绝，别浪费轮次去试。");
        sb.AppendLine("【下载文件】{download:\"能直接下到文件的完整直链\"} ——客户端会真的把文件下到工作区。"
                    + "用户让你「下载 / 装一个东西」时必须用它，别只把网址念给用户。"
                    + "拿不到直链就先用 {browse:} 打开下载页，从返回的【页面上的文件 / 下载链接】里挑；"
                    + "「别自己猜」地址（assets 常见命名之类）——猜错既下不到东西又白费一轮。");
        sb.AppendLine("【真浏览器】{web:\"动作 参数\"} ——用真浏览器内核操作网页，"
                    + "JS 渲染的页面（知乎/掘金/淘宝这类）、要点按钮、填表单、翻页都靠它。"
                    + "动作：open <网址>、text、links、click <按钮文字或 CSS 选择器>、"
                    + "type <选择器>|<要填的字>、scroll bottom、back、url。"
                    + "判据：静态页只要一段文字用 {browse:}（快）；"
                    + "{browse:} 读回来是空壳、或者要交互，就改用 {web:}。");
        sb.AppendLine("【生成图片】{img:\"描述\"} ——调用文生图模型，结果会直接显示在聊天里。"
                    + "用户要图时必须真的输出这条指令，**不许只在嘴上说\"画好啦\"却不给指令**（那等于骗人）；"
                    + "图有没有画成以客户端回传的结果为准，失败要如实说。用户没要图就别硬塞。");
        sb.AppendLine("【承诺提醒】{remind:\"时间|要提醒的事\"} ——用户让你「提醒我 / 明天叫我 / 别忘了」时用它，"
                    + "把这件事真的记进数据库，到点客户端会发系统通知。"
                    + "时间用中文说（明天早上8点 / 今晚9点 / 20分钟后 / 下周一上午10点），"
                    + "客户端会自己换算，你不用算日期。一次一条。"
                    + "**不许只在嘴上说「我记住了」却不给指令** —— 那是骗人，到点根本不会有通知。");

        // ─────── ④ 输出协议（最关键的一段） ───────
        sb.AppendLine();
        sb.AppendLine("──────── 你的输出格式（严格照做，这是循环的命脉） ────────");
        sb.AppendLine();
        sb.AppendLine("每一轮回复 = 【一句给自己看的判断】 + 【动作指令行】。动作指令行必须是独立的一行，"
                    + "格式严格为英文花括号 + 英文双引号：");
        sb.AppendLine();
        sb.AppendLine("  {todo:\"1. 第一步 2. 第二步 3. 第三步\"}   登记你的执行计划（多步任务第一轮必须先做这个）");
        sb.AppendLine("  {read:\"路径\"}                        读文件");
        sb.AppendLine("  {ls:\"路径\"}                          列目录");
        sb.AppendLine("  {write:\"路径|完整内容\"}              覆盖写入（新建文件用这个）");
        sb.AppendLine("  {append:\"路径|追加内容\"}             在文件末尾追加");
        sb.AppendLine("  {edit:\"路径|原文|新内容\"}            精确替换某段内容（改代码/改文件首选）");
        sb.AppendLine("  {del:\"路径\"}                          删除文件");
        sb.AppendLine("  {cmd:\"单条命令\"}                    执行终端命令");
        sb.AppendLine("  {api:\"名\"}                           查感知 API");
        sb.AppendLine("  {browse:\"网址或搜索词\"}              上网抓取（只回正文）");
        sb.AppendLine("  {download:\"直链地址\"}                真的把文件下到工作区（下东西必用它）");
        sb.AppendLine("  {web:\"动作 参数\"}                    用真浏览器操作网页（JS 页面/点击/填表）");
        sb.AppendLine("  {img:\"描述\"}                         生成图片");
        sb.AppendLine("  {remind:\"时间|要提醒的事\"}           记下承诺提醒（到点发通知，时间用中文）");
        sb.AppendLine("  {done:\"给用户的最终答复\"}            任务完成，收尾");
        sb.AppendLine();
        sb.AppendLine("客户端会自动执行这些指令，并把真实结果作为「动作执行结果」回传给你。"
                    + "回传内容之后，你必须继续下一轮判断。");
        sb.AppendLine();
        sb.AppendLine("【务必注意】");
        sb.AppendLine("· 指令行是给程序看的，不是给用户看的，所以不要在它旁边解释「我要执行什么」。");
        sb.AppendLine("· 指令行之外那句话可以很短（一句话说清你打算干嘛），但不要写成给用户的正式答复——"
                    + "给用户的正式答复只在 {done:\"...\"} 里出现。");
        sb.AppendLine("· 一轮里只放你**此刻确实需要**的指令。需要先知道结果才能决定下一步的，"
                    + "就这一轮只放那一条，等结果回来再说。");
        sb.AppendLine();
        sb.AppendLine("──────── 正例（照这个来） ────────");
        sb.AppendLine("用户：帮我看看 ChatPage 里思考计时是怎么算的");
        sb.AppendLine();
        sb.AppendLine("第 1 轮你回复：");
        sb.AppendLine("先找到文件、读代码、再下结论。");
        sb.AppendLine("{todo:\"1. 列出工作区找到目标文件 2. 读取文件定位计时逻辑 3. 给出结论\"}");
        sb.AppendLine();
        sb.AppendLine("（客户端回传：workspace 下有 ChatPage.xaml.cs、SettingsPage.xaml 等）");
        sb.AppendLine();
        sb.AppendLine("第 2 轮你回复：");
        sb.AppendLine("目标文件找到了，读它。");
        sb.AppendLine("{read:\"~/ChatPage.xaml.cs\"}");
        sb.AppendLine();
        sb.AppendLine("（客户端回传：文件内容……）");
        sb.AppendLine();
        sb.AppendLine("第 3 轮你回复：");
        sb.AppendLine("{done:\"思考计时是在 ConsumeSseAsync 里记的，从第一个思考字开始、到正文出现时冻结，"
                    + "所以正文开始后就不会再累加了。\"}");
        sb.AppendLine();
        sb.AppendLine("──────── 反例（绝对不要这样） ────────");
        sb.AppendLine("失败：只说「好的，我来帮你看看」然后不输出任何指令 → 这一轮白跑，循环要空转");
        sb.AppendLine("失败：凭印象直接回答「大概是 3 秒左右」 → 没查证就下结论，违反铁律 1");
        sb.AppendLine("失败：一轮里塞 {read:\"a\"} {read:\"b\"} {read:\"c\"} {edit:...} {done:...} → 一次赌太多，"
                    + "中途出错全盘皆输");
        sb.AppendLine("失败：用 {done:\"我先给你说个大概，你也可以自己打开看看\"} 收尾 → 任务没做完就宣告完成");
        sb.AppendLine();
        sb.AppendLine("──────── 单步任务怎么办 ────────");
        sb.AppendLine("如果一句话就能答完、根本不需要动手（纯闲聊、纯知识问答、纯情绪回应），"
                    + "那就别硬凑步骤，第一轮直接 {done:\"...\"}。");
        sb.AppendLine("判断标准：这件事需不需要去看你此刻看不到的东西（文件、命令输出、网页、用户实况）？"
                    + "需要 → 走循环；不需要 → 直接 {done:...}。");

        return sb.ToString();
    }

    /// <summary>
    /// 单轮请求。用流式接收：这样才能把模型的推理过程实时喂给界面，
    /// 否则一轮十几秒里用户只能盯着转圈，体感上就和"卡住了"没区别。
    /// 对外仍然返回"这一轮的完整正文"，保持调用方逻辑不变。
    /// </summary>
    /// <summary>
    /// 发一轮，**失败自动重试一次**。
    ///
    /// 服务商掐线 / 网络切换时抛的是 Java 异常（Android 网络栈），以前没认出来也不重试，
    /// 一次抖动就把整个 Agent 循环打断 —— 用户看到的就是"开 Agent 就报网络中断"。
    /// </summary>
    private static async Task<string> SendOnceAsync(List<object> messages, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceCoreAsync(messages, ct);
            }
            catch (Exception ex) when (attempt == 1 && !ct.IsCancellationRequested && ApiErrors.IsTransient(ex))
            {
                // 稍等一下再试，别立刻又撞上同一个抖动
                try { await Task.Delay(800, ct); } catch { throw; }
            }
        }
    }

    private static async Task<string> SendOnceCoreAsync(List<object> messages, CancellationToken ct)
    {
        var body = new
        {
            model = AppSettings.ResolveChatModel(AppSettings.ForceThinking),
            stream = true,
            messages,
            max_tokens = 8000,
            // 循环里的每一轮都在做"判断下一步"，推理档位必须给足，否则模型会偷懒不思考
            reasoning_effort = AppSettings.ForceThinking ? "high" : "medium",
            stream_options = new { include_usage = true }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl);
        // 用 EffectiveChatApiKey：启用本地模型时这里是本地服务的 key（多数留空）
        var apiKey = AppSettings.EffectiveChatApiKey;
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(180));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await ApiErrors.EnsureOkAsync(resp);   // 非 2xx 时把服务商正文捞出来再抛

        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        string? line;

        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..];
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var delta = choices[0].TryGetProperty("delta", out var d) ? d : default;
                    if (delta.ValueKind == JsonValueKind.Object)
                    {
                        if (delta.TryGetProperty("reasoning_content", out var rc)
                            && rc.ValueKind == JsonValueKind.String)
                        {
                            var piece = rc.GetString();
                            if (!string.IsNullOrEmpty(piece))
                            {
                                reasoning.Append(piece);
                                RaiseThinking(piece);   // 实时喂给界面
                            }
                        }
                        if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        {
                            var piece = c.GetString();
                            if (!string.IsNullOrEmpty(piece)) content.Append(piece);
                        }
                    }
                }

                // usage 只有最后一帧（stream_options.include_usage）才带
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    var tu = TokenUsageMapper.FromElement(usage);
                    if (tu != null) UsageStats.Current.Apply(tu);
                }
            }
            catch
            {
                // 单帧解析失败不影响整体：跳过继续读
            }
        }

        // 正文优先；正文为空时退回推理内容（部分中转会把动作指令塞进 reasoning）
        if (content.Length > 0) return content.ToString();
        return reasoning.ToString();
    }
}


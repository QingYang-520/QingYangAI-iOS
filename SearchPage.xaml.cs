using Microsoft.Maui.Controls;

namespace 青阳AI;

/// <summary>
/// 搜索结果的一条（视图模型）。
/// 单独做一层是因为列表要显示「谁说的 / 时间 / 关键字高亮后的富文本」，
/// 直接拿 ChatMsg 绑不出来（高亮要用 FormattedString）。
/// </summary>
public class SearchHit
{
    /// <summary>消息主键（点结果时拿它回聊天页定位）。</summary>
    public int MsgId { get; init; }

    /// <summary>原始正文（复制 / 收藏判断用）。</summary>
    public string Content { get; init; } = "";

    /// <summary>说话的人：你 / Ta。</summary>
    public string Who { get; init; } = "";

    /// <summary>说话人那行小字的颜色（用户偏暖、AI 偏冷，跟聊天页气质一致）。</summary>
    public Color WhoColor { get; init; } = Colors.White;

    /// <summary>用户消息靠右、AI 消息靠左。</summary>
    public LayoutOptions Align { get; init; } = LayoutOptions.Start;

    /// <summary>时间（年-月-日 时:分）。</summary>
    public string TimeText { get; init; } = "";

    /// <summary>是否已收藏。</summary>
    public bool IsFavorite { get; init; }

    /// <summary>关键字高亮后的富文本。</summary>
    public FormattedString Highlighted { get; init; } = new();
}

/// <summary>
/// 聊天记录搜索页：输入关键字查库，结果列表点一条回聊天页定位。
/// 数据层走 ChatStore.SearchMessagesAsync（LIKE 匹配，最多 200 条）。
/// </summary>
public partial class SearchPage : ContentPage
{
    /// <summary>输入防抖时长：停止输入这么久才真正查库，避免每敲一个字都扫一遍 SQLite。</summary>
    private const int DebounceMs = 300;

    /// <summary>摘要最多显示多少字（超出就截断，免得一条长回复占满整屏）。</summary>
    private const int SnippetMax = 120;

    /// <summary>防抖用的取消令牌源：每次输入都换一个新的，旧的直接取消。</summary>
    private CancellationTokenSource? _debounceCts;

    /// <summary>最近一次查询的关键字（结果回调里判竞态用）。</summary>
    private string _lastKeyword = "";

    public SearchPage()
    {
        InitializeComponent();
        ThemeManager.Apply(this);   // 应用当前主题色
        Loaded += (_, _) => JellyAnim.EnterPage(Content);   // 页面进入动效
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // 进来直接弹键盘，省一次点击
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(220), () =>
        {
            try { searchBar.Focus(); } catch { }
        });
    }

    /// <summary>自绘返回按钮（本页隐藏了系统导航栏，保持和聊天页一致的顶部样式）。</summary>
    private async void OnBackTapped(object? sender, EventArgs e)
    {
        try { await Navigation.PopAsync(); } catch { }
    }

    /// <summary>输入变化：300ms 防抖后查库。</summary>
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var keyword = (e.NewTextValue ?? "").Trim();

        // 换关键字就掐掉上一轮（只 Cancel 不 Dispose：上一轮的续体可能还在跑，
        // Dispose 掉会让它访问 token 时报 ObjectDisposedException）
        _debounceCts?.Cancel();
        _debounceCts = new CancellationTokenSource();
        var token = _debounceCts.Token;

        if (keyword.Length == 0)
        {
            _lastKeyword = "";
            resultList.ItemsSource = null;
            lblCount.Text = "";
            lblHint.Text = "输入关键字，搜索你们聊过的话";
            lblHint.IsVisible = true;
            return;
        }

        _lastKeyword = keyword;   // 结果回调靠它判竞态，必须先记下当前关键字
        _ = DebouncedSearchAsync(keyword, token);
    }

    /// <summary>等 300ms 没新输入再查；token 被取消就安静退出。</summary>
    private async Task DebouncedSearchAsync(string keyword, CancellationToken token)
    {
        try
        {
            await Task.Delay(DebounceMs, token);
            if (token.IsCancellationRequested) return;
            await SearchAsync(keyword, token);
        }
        catch (OperationCanceledException) { /* 正常：用户还在打字 */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchPage] {ex.Message}");
        }
    }

    /// <summary>真正查库并把结果转成可绑定的 SearchHit。</summary>
    private async Task SearchAsync(string keyword, CancellationToken token)
    {
        List<ChatMsg> hits;
        try
        {
            hits = await ChatStore.Instance.SearchMessagesAsync(keyword);
        }
        catch (Exception ex)
        {
            lblHint.Text = $"搜索出错了：{ex.Message}";
            lblHint.IsVisible = true;
            return;
        }

        // 查库是异步的，回来时关键字可能已经又变了 —— 过期结果直接丢掉
        if (token.IsCancellationRequested || keyword != _lastKeyword)
            return;

        var list = new List<SearchHit>(hits.Count);
        foreach (var m in hits)
            list.Add(ToHit(m, keyword));

        // 新的在前，更符合"找最近说过的话"的直觉
        list.Reverse();

        resultList.ItemsSource = list;
        lblCount.Text = list.Count > 0 ? $"{list.Count} 条" : "";
        lblHint.Text = list.Count > 0 ? "" : $"没有找到包含「{keyword}」的消息";
        lblHint.IsVisible = list.Count == 0;

        if (list.Count > 0) JellyAnim.PopIn(resultList);   // 结果回填：轻微果冻弹入
    }

    /// <summary>ChatMsg → SearchHit：算对齐、摘要、高亮片段。</summary>
    private static SearchHit ToHit(ChatMsg m, string keyword)
    {
        var content = m.Content ?? "";
        return new SearchHit
        {
            MsgId = m.Id,
            Content = content,
            Who = m.IsUser ? "你" : "Ta",
            WhoColor = m.IsUser ? Color.FromArgb("#FBB5B2") : Color.FromArgb("#B9D5C4"),
            Align = m.IsUser ? LayoutOptions.End : LayoutOptions.Start,
            TimeText = m.Timestamp.ToString("yyyy-MM-dd HH:mm"),
            IsFavorite = m.IsFavorite,
            Highlighted = BuildHighlighted(content, keyword)
        };
    }

    /// <summary>
    /// 生成带关键字高亮的 FormattedString。
    ///
    /// 正文太长时只截「命中点周围」的一段（前留 12 字、后留到 SnippetMax），
    /// 因为用户想看的是关键字所在那句，而不是一条长回复的开头。
    /// 所有出现位置都高亮，不只第一处。
    /// </summary>
    private static FormattedString BuildHighlighted(string content, string keyword)
    {
        var fs = new FormattedString();
        var body = content ?? "";

        // 图片消息没有文字 → 给个占位，不然列表里是个空卡片
        if (body.Trim().Length == 0)
        {
            fs.Spans.Add(MakeSpan("[图片或语音消息]", "#9E8D8C", false));
            return fs;
        }

        // 摘要窗口：围绕首个命中点裁剪
        int hitAt = keyword.Length == 0
            ? 0
            : body.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        int start = 0;
        string prefixEllipsis = "";
        string suffixEllipsis = "";
        if (body.Length > SnippetMax)
        {
            if (hitAt > 12)
            {
                start = hitAt - 12;
                prefixEllipsis = "…";
            }
            int len = Math.Min(SnippetMax, body.Length - start);
            // 别把关键字切掉
            if (hitAt >= start && hitAt + keyword.Length > start + len)
                len = Math.Min(body.Length - start, hitAt + keyword.Length - start + 8);
            body = body.Substring(start, len);
            if (start + len < content.Length) suffixEllipsis = "…";
        }

        if (prefixEllipsis.Length > 0) fs.Spans.Add(MakeSpan(prefixEllipsis, "#9E8D8C", false));

        if (keyword.Length == 0)
        {
            fs.Spans.Add(MakeSpan(body, "#EDEDED", false));
        }
        else
        {
            int i = 0;
            while (i < body.Length)
            {
                int at = body.IndexOf(keyword, i, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    fs.Spans.Add(MakeSpan(body.Substring(i), "#EDEDED", false));
                    break;
                }
                if (at > i) fs.Spans.Add(MakeSpan(body.Substring(i, at - i), "#EDEDED", false));
                fs.Spans.Add(MakeSpan(body.Substring(at, keyword.Length), "#FBB5B2", true));
                i = at + keyword.Length;
            }
        }

        if (suffixEllipsis.Length > 0) fs.Spans.Add(MakeSpan(suffixEllipsis, "#9E8D8C", false));
        return fs;
    }

    /// <summary>造一个高亮用的 Span（命中处加粗上色，其余保持正文灰白）。</summary>
    private static Microsoft.Maui.Controls.Span MakeSpan(string text, string colorHex, bool bold) => new()
    {
        Text = text,
        TextColor = Color.FromArgb(colorHex),
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None
    };

    /// <summary>
    /// 点结果：回聊天页并定位到这条消息。
    ///
    /// 优先走 ChatJump 静态回调（ChatPage 打开本页时挂上的），
    /// 回调没挂上就退而求其次从导航栈里找 ChatPage 实例 ——
    /// 两条路都走不通就只是退页，不会崩。
    /// </summary>
    private async void OnResultTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (e.Parameter is not SearchHit hit) return;

            bool hasCallback = ChatJump.ScrollRequested != null;
            var nav = Navigation;

            // 先退页：ChatPage 的 OnAppearing 会同步数据库，定位在那边还会等一小会儿
            await nav.PopAsync();

            if (hasCallback)
            {
                ChatJump.Request(hit.MsgId);
                return;
            }

            // 兜底：直接从导航栈里找聊天页（本页一定是从聊天页推出来的）
            foreach (var page in nav.NavigationStack)
            {
                if (page is ChatPage chat)
                {
                    chat.ScrollToMessageId(hit.MsgId);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SearchPage OnResultTapped] {ex.Message}");
        }
    }
}

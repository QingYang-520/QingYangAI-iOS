using Microsoft.Maui.Controls;

namespace 青阳AI;

/// <summary>
/// 记忆面板：展示Ta记住的每一条关于用户的事实，可单独删除。
///
/// 2026-10-26 接入遗忘曲线：
///   · 已归档（长期没被召回而「淡忘」）的记忆灰显，带「已淡忘」标签，点「恢复」可重新激活；
///   · 顶部显示「已淡忘 N 条」，让用户知道有多少记忆暂时沉底了；
///   · 展示用 <see cref="MemoryItem"/> 包装 MemoryRow，把颜色/文案的派生逻辑放在 VM 里，
///     XAML 只管绑定（避免在模板里堆转换器）。
/// </summary>
public partial class MemoryListPage : ContentPage
{
    /// <summary>当前列表数据缓存：换主题色时要按新颜色重新映射一遍（不重新查库）。</summary>
    private List<MemoryItem> _items = new();

    public MemoryListPage()
    {
        InitializeComponent();
        ThemeManager.Apply(this);   // 应用当前主题色
        Loaded += (_, _) =>
        {
            JellyAnim.EnterPage(Content);   // 页面进入动效
            // 活跃记忆的左侧竖条取的是主题色（VM 里算好的），换主题要重新映射一次
            ThemeManager.ThemeChanged -= OnThemeChanged;   // 防重复订阅（Loaded 可能多次触发）
            ThemeManager.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        _ = LoadAsync();
    }

    /// <summary>换主题色 → 用缓存重算颜色并重绑（不查库，很快）。</summary>
    private void OnThemeChanged()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_items.Count == 0) return;
                memoryList.ItemsSource = _items.Select(i => new MemoryItem(i.Row)).ToList();
            });
        }
        catch { }
    }

    private async Task LoadAsync()
    {
        try
        {
            var rows = await ChatStore.Instance.GetMemoriesAsync();

            // 已淡忘的排后面：用户更关心还活着的记忆
            _items = rows
                .OrderBy(r => r.Archived)
                .Select(r => new MemoryItem(r))
                .ToList();
            memoryList.ItemsSource = _items;

            // 顶部「已淡忘 N 条」提示
            int archived = _items.Count(i => i.IsArchived);
            lblArchived.IsVisible = archived > 0;
            lblArchived.Text = archived > 0
                ? $"已淡忘 {archived} 条 —— Ta 暂时想不起来了，点「恢复」能让 Ta 重新记起。"
                : "";

            JellyAnim.PopIn(memoryList);   // 列表回填：轻微果冻弹入
        }
        catch { }
    }

    private async void OnDeleteMemoryClicked(object? sender, EventArgs e)
    {
        try
        {
            if (sender is not Button btn || btn.BindingContext is not MemoryItem item) return;
            var answer = await DisplayAlertAsync("删除记忆", $"确定忘掉这条吗？\n\n{item.Content}", "忘掉", "算了");
            if (!answer) return;

            await ChatStore.Instance.DeleteMemoryAsync(item.Id);
            // 该条记忆变化 → 让记忆注入上下文与设置页计数同步刷新
            MemoryService.NotifyMemoriesChanged();
            await LoadAsync();
        }
        catch { }
    }

    /// <summary>恢复一条已淡忘的记忆：重新变回活跃，下次对话就可能被想起。</summary>
    private async void OnRestoreMemoryClicked(object? sender, EventArgs e)
    {
        try
        {
            if (sender is not Button btn || btn.BindingContext is not MemoryItem item) return;

            await ChatStore.Instance.UnarchiveMemoryAsync(item.Id);
            MemoryService.NotifyMemoriesChanged();   // 注入上下文立刻刷新
            await LoadAsync();
        }
        catch { }
    }

    /// <summary>
    /// 列表项包装：把 MemoryRow 的展示态（颜色 / 时间文本 / 是否归档）算好，
    /// 模板里只做纯绑定，XAML 不用写转换器。
    /// </summary>
    private sealed class MemoryItem
    {
        /// <summary>活跃记忆的卡片底色（与其它页面一致）。</summary>
        private static readonly Color LiveCard = Color.FromArgb("#241E1E");
        /// <summary>已淡忘记忆的卡片底色：压暗，视觉上「沉下去」。</summary>
        private static readonly Color FadedCard = Color.FromArgb("#1E1A1A");
        /// <summary>活跃记忆的正文字色。</summary>
        private static readonly Color LiveText = Color.FromArgb("#EDEDED");
        /// <summary>已淡忘记忆的正文字色：灰掉。</summary>
        private static readonly Color FadedText = Color.FromArgb("#7A6E6D");
        /// <summary>已淡忘记忆左侧竖条的颜色（暗灰，不跟主题色）。</summary>
        private static readonly Color FadedBar = Color.FromArgb("#4A4140");

        private readonly MemoryRow _row;

        public MemoryItem(MemoryRow row) => _row = row;

        /// <summary>原始行（换主题色时用来重新构造展示项）。</summary>
        public MemoryRow Row => _row;

        public int Id => _row.Id;

        public string Content => _row.Content ?? "";

        /// <summary>创建时间（HH:mm 也保留，方便用户对上是哪次聊的）。</summary>
        public string CreatedText => _row.CreatedAt.ToString("yyyy-MM-dd HH:mm");

        /// <summary>是否已淡忘（归档）。</summary>
        public bool IsArchived => _row.Archived;

        /// <summary>是否活跃（未归档）——控制「删」按钮显示。</summary>
        public bool IsLive => !_row.Archived;

        public Color CardColor => IsArchived ? FadedCard : LiveCard;

        public Color TextColor => IsArchived ? FadedText : LiveText;

        /// <summary>左侧竖条：活跃走主题色（由 ThemeManager 按 StyleId 刷），归档走暗灰。</summary>
        public Color BarColor => IsArchived ? FadedBar : AppSettings.ThemeColor;
    }
}

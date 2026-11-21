using Microsoft.Maui.Controls;

namespace 青阳AI;

/// <summary>
/// 「这段时间」页：把复盘落库的情绪数据画成曲线。
///
/// 数据来源：<see cref="ChatStore.GetEmotionDailyAsync"/> —— 每条消息复盘时
/// MessageReviewService 会写一条 EmotionRow（情绪词 + 0~100 分 + 关系走向），
/// 这里按天聚合成平均分再画。
///
/// 曲线用 GraphicsView + 手写 IDrawable 绘制（不引第三方图表库）：
///   · X 轴 = 日期（MM-dd），Y 轴 = 0~100；
///   · 折线下方填充半透明主题色，线本身走主题色，换主题色会跟着变；
///   · 跨度自适应：数据少时至少铺开 7 天，避免几个点全挤在右边。
///
/// 注意：Drawable 用**独立的 Painter 类**而不是让页面自己实现 IDrawable：
///    GraphicsView 本身已实现 IDrawable，页面再实现会撞方法隐藏（CS0108），
///    这也是 DotMotionLoader 里踩过的坑。
/// </summary>
public partial class EmotionPage : ContentPage
{
    /// <summary>曲线看多少天。</summary>
    private const int Days = 30;

    private readonly ChartPainter _painter = new();

    /// <summary>最近一次加载到的按天数据（换主题色时用它重绑列表，不重新查库）。</summary>
    private List<(DateTime Day, int AvgScore, string Moods)> _daily = new();

    public EmotionPage()
    {
        InitializeComponent();
        ThemeManager.Apply(this);   // 应用当前主题色
        chartView.Drawable = _painter;

        Loaded += (_, _) =>
        {
            JellyAnim.EnterPage(Content);   // 页面进入动效
            // 换主题色时重绘曲线 + 重绑明细（分数颜色取自主题色）
            ThemeManager.ThemeChanged -= OnThemeChanged;   // 防重复订阅（Loaded 可能多次触发）
            ThemeManager.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

        _ = LoadAsync();
    }

    /// <summary>主题色变更 → 曲线重绘一次 + 明细列表按新颜色重绑。</summary>
    private void OnThemeChanged()
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                chartView.Invalidate();
                if (_daily.Count > 0) BindDaily(_daily);
            });
        }
        catch { }
    }

    /// <summary>把按天数据绑到明细列表（最近的一天排最前）。</summary>
    private void BindDaily(List<(DateTime Day, int AvgScore, string Moods)> daily)
    {
        var items = daily
            .OrderByDescending(d => d.Day)
            .Select(d => new DailyItem(d.Day, d.AvgScore, d.Moods))
            .ToList();
        BindableLayout.SetItemsSource(dailyList, items);
    }

    private async Task LoadAsync()
    {
        try
        {
            var daily = await ChatStore.Instance.GetEmotionDailyAsync(Days);
            _daily = daily;

            if (daily.Count == 0)
            {
                // 空态：曲线区让位给提示语，明细列表也留一句
                chartView.IsVisible = false;
                lblEmpty.IsVisible = true;
                lblSummary.Text = $"最近 {Days} 天还没有心情记录";
                BindableLayout.SetItemsSource(dailyList, null);
                lblDailyEmpty.IsVisible = true;
                lblDailyEmpty.Text = "等Ta多听你说几句，这里就会慢慢长出曲线。";
                return;
            }

            chartView.IsVisible = true;
            lblEmpty.IsVisible = false;
            lblDailyEmpty.IsVisible = false;

            // ── 概览：天数 / 平均分 / 最高 / 最低 ──
            int avg = (int)Math.Round(daily.Average(d => d.AvgScore));
            int max = daily.Max(d => d.AvgScore);
            int min = daily.Min(d => d.AvgScore);
            lblSummary.Text = $"最近 {Days} 天 · 记录 {daily.Count} 天 · 平均 {avg} 分（最高 {max} · 最低 {min}）";

            // ── 曲线 ──
            _painter.Points = daily.Select(d => (d.Day, d.AvgScore)).ToList();
            _painter.Today = DateTime.Today;
            chartView.Invalidate();

            // ── 每天明细：最近的一天排最前 ──
            BindDaily(daily);

            JellyAnim.PopIn(dailyList);   // 明细回填：轻微果冻弹入
        }
        catch
        {
            // 读取失败就保持空态，不弹错误
            _daily = new();
            chartView.IsVisible = false;
            lblEmpty.IsVisible = true;
            lblEmpty.Text = "读取心情记录失败，稍后再试试。";
        }
    }

    private async void OnRefreshTapped(object? sender, TappedEventArgs e)
    {
        try { await LoadAsync(); } catch { }
    }

    // ══════════════════════ 每天明细项 ══════════════════════

    /// <summary>明细列表的一行：日期 + 平均分 + 当天出现过的情绪词。</summary>
    private sealed class DailyItem
    {
        // 分数分档取色：好 → 主题色，中 → 灰，差 → 冷蓝灰（视觉上"沉下去"）
        private static readonly Color MidColor = Color.FromArgb("#B7A9A8");
        private static readonly Color LowColor = Color.FromArgb("#7E8C9E");

        public DailyItem(DateTime day, int score, string moods)
        {
            DateText = day.ToString("MM-dd") + "  " + WeekName(day.DayOfWeek);
            ScoreText = score.ToString();
            ScoreColor = score >= 70 ? AppSettings.ThemeColor : (score >= 45 ? MidColor : LowColor);
            MoodsText = string.IsNullOrWhiteSpace(moods) ? "（没记下具体的词）" : moods;
        }

        public string DateText { get; }
        public string ScoreText { get; }
        public Color ScoreColor { get; }
        public string MoodsText { get; }

        private static string WeekName(DayOfWeek w) => w switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日"
        };
    }

    // ══════════════════════ 曲线绘制 ══════════════════════

    /// <summary>
    /// 手绘情绪折线：Y 轴 0~100，X 轴日期，折线下方填充半透明主题色。
    /// 纯 ICanvas 绘制，不依赖任何图表库。
    /// </summary>
    private sealed class ChartPainter : IDrawable
    {
        /// <summary>按天聚合后的点（日期, 平均分）。</summary>
        public List<(DateTime Day, int Score)> Points { get; set; } = new();

        /// <summary>「今天」，用于算 X 轴跨度。</summary>
        public DateTime Today { get; set; } = DateTime.Today;

        // 内边距：左侧留给 Y 轴刻度，底部留给日期
        private const float PadLeft = 30f;
        private const float PadRight = 14f;
        private const float PadTop = 12f;
        private const float PadBottom = 24f;

        // 配色（与全局页面风格一致）
        private static readonly Color GridColor = Color.FromArgb("#2B2524");
        private static readonly Color AxisTextColor = Color.FromArgb("#7A6E6D");
        private static readonly Color DotHoleColor = Color.FromArgb("#1F1A19");

        public void Draw(ICanvas canvas, RectF rect)
        {
            try { DrawCore(canvas, rect); }
            catch { /* 绘制异常不能把页面搞崩 */ }
        }

        private void DrawCore(ICanvas canvas, RectF rect)
        {
            float w = rect.Width - PadLeft - PadRight;
            float h = rect.Height - PadTop - PadBottom;
            if (w <= 12 || h <= 12) return;

            // 主题色（每帧读一次，换主题立刻生效）
            Color primary;
            try { primary = AppSettings.ThemeColor; }
            catch { primary = Color.FromArgb("#B388FF"); }

            // ── X 轴跨度：至少铺开 7 天，数据多则按实际最早日期算 ──
            int span = 6;
            if (Points.Count > 0)
            {
                var earliest = Points.Min(p => p.Day.Date);
                span = Math.Max((Today.Date - earliest).Days, 6);
            }
            var startDay = Today.Date.AddDays(-span);

            // 日期 → X 坐标；分数 → Y 坐标（0 在底、100 在顶）
            float X(DateTime d) => PadLeft + (float)((d.Date - startDay).TotalDays / (double)span) * w;
            float Y(int score) => PadTop + h - (float)(Math.Clamp(score, 0, 100) / 100.0) * h;
            float bottomY = PadTop + h;

            canvas.Font = Microsoft.Maui.Graphics.Font.Default;
            canvas.FontSize = 9f;

            // ── 横向网格线 + Y 轴刻度（0 / 25 / 50 / 75 / 100）──
            foreach (var tick in new[] { 0, 25, 50, 75, 100 })
            {
                float y = Y(tick);
                canvas.StrokeColor = GridColor;
                canvas.StrokeSize = tick == 50 ? 1.2f : 0.8f;   // 中线稍明显，当"基准线"
                canvas.StrokeDashPattern = tick == 50 ? null : new float[] { 3, 4 };
                canvas.DrawLine(PadLeft, y, PadLeft + w, y);

                canvas.FillColor = AxisTextColor;
                canvas.DrawString(tick.ToString(), 0, y - 7f, PadLeft - 6f, 14f,
                    HorizontalAlignment.Right, VerticalAlignment.Center);
            }
            canvas.StrokeDashPattern = null;

            // ── X 轴日期标签（约 5 个等间隔刻度，避免 30 个标签糊成一片）──
            const int labelCount = 5;
            for (int i = 0; i <= labelCount; i++)
            {
                var d = startDay.AddDays((int)Math.Round(span * i / (double)labelCount));
                float x = X(d);
                canvas.FillColor = AxisTextColor;
                canvas.DrawString(d.ToString("MM-dd"), x - 22f, bottomY + 6f, 44f, 14f,
                    HorizontalAlignment.Center, VerticalAlignment.Top);
            }

            if (Points.Count == 0) return;

            // ── 折线下方的半透明填充 ──
            var fill = new PathF();
            fill.MoveTo(X(Points[0].Day), Y(Points[0].Score));
            for (int i = 1; i < Points.Count; i++)
                fill.LineTo(X(Points[i].Day), Y(Points[i].Score));
            fill.LineTo(X(Points[^1].Day), bottomY);
            fill.LineTo(X(Points[0].Day), bottomY);
            fill.Close();

            canvas.FillColor = primary.WithAlpha(0.18f);
            canvas.FillPath(fill);

            // ── 折线本体 ──
            if (Points.Count > 1)
            {
                var line = new PathF();
                line.MoveTo(X(Points[0].Day), Y(Points[0].Score));
                for (int i = 1; i < Points.Count; i++)
                    line.LineTo(X(Points[i].Day), Y(Points[i].Score));

                canvas.StrokeColor = primary;
                canvas.StrokeSize = 2.2f;
                canvas.StrokeLineCap = LineCap.Round;
                canvas.StrokeLineJoin = LineJoin.Round;
                canvas.DrawPath(line);
            }

            // ── 每个数据点画个小圆（内填卡片底色 + 主题色描边，像一颗颗小珠子）──
            foreach (var p in Points)
            {
                float px = X(p.Day);
                float py = Y(p.Score);
                canvas.FillColor = DotHoleColor;
                canvas.FillCircle(px, py, 3.4f);
                canvas.StrokeColor = primary;
                canvas.StrokeSize = 1.6f;
                canvas.DrawCircle(px, py, 3.4f);
            }
        }
    }
}

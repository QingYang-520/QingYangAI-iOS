namespace 青阳AI;

/// <summary>
/// 全项目公共 UI 工具（2026-10-29 整合新建）。
///
/// 原来这套「主题色应用 / 页面进入动效 / 确认弹窗 / 颜色工具」散在 15 个页面的
/// OnAppearing 与构造函数里各写一遍，现在统一收在这里。
///
/// 分三块：
///   · <see cref="InitPage"/>     页面初始化三连（主题色 + 果冻动效）
///   · 颜色工具                    <see cref="Hex"/> / <see cref="Lighten"/> / <see cref="ContrastOn"/>
///   · 弹窗辅助                    <see cref="ConfirmAsync"/> / <see cref="AlertAsync"/> / <see cref="ToastAsync"/>
/// </summary>
public static class UiKit
{
    // ────────────────────────── 页面初始化 ──────────────────────────

    /// <summary>
    /// 页面初始化三连：应用主题色 + 挂页面进入动效。
    ///
    /// 用法（每个 ContentPage 构造函数里，InitializeComponent() 之后调一次）：
    /// <code>UiKit.InitPage(this);</code>
    ///
    /// 等价于原来手写的：
    /// <code>
    /// ThemeManager.Apply(this);
    /// Loaded += (_, _) => JellyAnim.EnterPage(Content);
    /// </code>
    /// </summary>
    public static void InitPage(ContentPage page, bool jelly = true)
    {
        if (page is null) return;
        try { ThemeManager.Apply(page); } catch { }
        if (jelly)
        {
            page.Loaded += (_, _) =>
            {
                try { JellyAnim.EnterPage(page.Content); } catch { }
            };
        }
    }

    /// <summary>
    /// 带主题色刷新的页面初始化：除了 InitPage，还在页面出现/销毁时
    /// 自动订阅/退订 ThemeManager.ThemeChanged（给那些换色需要重绘的页面用，
    /// 如情绪曲线、记忆列表）。回调会在主线程上执行。
    /// </summary>
    public static void InitPageWithThemeRefresh(ContentPage page, Action onThemeChanged, bool jelly = true)
    {
        if (page is null) return;
        InitPage(page, jelly);

        page.Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= onThemeChanged;   // 防重复订阅
            ThemeManager.ThemeChanged += onThemeChanged;
        };
        page.Unloaded += (_, _) => ThemeManager.ThemeChanged -= onThemeChanged;
    }

    // ────────────────────────── 颜色工具 ──────────────────────────

    /// <summary>把 "#RRGGBB" 转成 Color，失败返回 fallback。</summary>
    public static Color Hex(string hex, Color? fallback = null)
    {
        try { return Color.FromArgb(hex); }
        catch { return fallback ?? Colors.White; }
    }

    /// <summary>把颜色向白色插值（t 越大越白，0 = 原色，1 = 纯白）。</summary>
    public static Color Lighten(Color c, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color(
            (float)(c.Red + (1 - c.Red) * t),
            (float)(c.Green + (1 - c.Green) * t),
            (float)(c.Blue + (1 - c.Blue) * t),
            c.Alpha);
    }

    /// <summary>把颜色向黑色插值（t 越大越暗）。</summary>
    public static Color Darken(Color c, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color(
            (float)(c.Red * (1 - t)),
            (float)(c.Green * (1 - t)),
            (float)(c.Blue * (1 - t)),
            c.Alpha);
    }

    /// <summary>按背景亮度自动选一个读起来清楚的文字色（亮底深字 / 暗底浅字）。</summary>
    public static Color ContrastOn(Color bg)
    {
        double lum = 0.299 * bg.Red + 0.587 * bg.Green + 0.114 * bg.Blue;
        return lum > 0.6 ? Color.FromArgb("#1A1A1A") : Color.FromArgb("#FFFFFF");
    }

    /// <summary>给颜色加透明度（0~1）。</summary>
    public static Color WithAlpha(Color c, float alpha) => c.WithAlpha(alpha);

    // ────────────────────────── 弹窗辅助 ──────────────────────────

    /// <summary>
    /// 确认弹窗。返回 true = 用户点了确认。
    /// 页面已销毁/弹窗失败时返回 false（安全默认，不会误执行破坏性操作）。
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        Page? page, string title, string message,
        string accept = "确定", string cancel = "取消")
    {
        if (page is null) return false;
        try { return await page.DisplayAlertAsync(title, message, accept, cancel); }
        catch { return false; }
    }

    /// <summary>单按钮提示弹窗。</summary>
    public static async Task AlertAsync(Page? page, string title, string message, string ok = "确定")
    {
        if (page is null) return;
        try { await page.DisplayAlertAsync(title, message, ok); } catch { }
    }

    /// <summary>
    /// 底部操作表。cancel 为 null 时不显示取消按钮（Android 返回键仍可关闭）。
    /// 用户取消/失败返回 null。
    /// </summary>
    public static async Task<string?> SheetAsync(
        Page? page, string title, string? cancel, params string[] options)
    {
        if (page is null) return null;
        try { return await page.DisplayActionSheetAsync(title, cancel, null, options); }
        catch { return null; }
    }

    // ────────────────────────── 常用尺寸/颜色常量 ──────────────────────────

    /// <summary>页面背景色（全项目统一）。</summary>
    public static readonly Color PageBg = Color.FromArgb("#181211");
    /// <summary>卡片背景色（比页面略亮，形成视觉边界）。</summary>
    public static readonly Color CardBg = Color.FromArgb("#1F1A19");
    /// <summary>卡片描边色。</summary>
    public static readonly Color CardStroke = Color.FromArgb("#2B2524");
    /// <summary>次要文字色。</summary>
    public static readonly Color TextMuted = Color.FromArgb("#9E8D8C");
    /// <summary>更弱的文字色（提示/单位）。</summary>
    public static readonly Color TextFaint = Color.FromArgb("#8A7B7A");

    // ────────────────────────── 软键盘 ──────────────────────────

    /// <summary>隐藏软键盘（Android 专用，其它平台无操作）。</summary>
    public static void HideKeyboard()
    {
#if ANDROID
        try
        {
            var window = Platform.CurrentActivity?.Window;
            if (window is null) return;
            var imm = Platform.CurrentActivity?.GetSystemService("input_method")
                      as Android.Views.InputMethods.InputMethodManager;
            var view = window.DecorView.RootView;
            imm?.HideSoftInputFromWindow(view.WindowToken, 0);
        }
        catch { }
#endif
    }
}

// ============================================================================
// 全局轻量动效（原 JellyAnim.cs，2026-10-29 并入本文件）
// ============================================================================

/// <summary>
/// 全局轻量动效：页面切换 + 内容回填的「果冻」弹性。
///
/// 设计约束（2026-10-11 用户要求「不要太明显」）：
///   · 位移 ≤ 14px、缩放 ≥ 0.98 —— 只做"动了一下"的暗示，不做表演；
///   · 只有**缩放**走 SpringOut（轻微过冲回弹 = 果冻感的来源），
///     位移与淡入走 CubicOut（平滑，不会出现"弹过头"的怪异感）；
///   · 时长 260~340ms，贴近系统标准转场，不拖沓；
///   · 全部 fire-and-forget，且 catch 里强制复位 —— 动画异常绝不把页面留在透明态。
/// </summary>
internal static class JellyAnim
{
    /// <summary>页面进入：轻微上浮 + 淡入 + 极轻回弹缩放。</summary>
    public static void EnterPage(VisualElement? el) => Run(el, fromY: 14, fromScale: 0.982, ms: 330);

    /// <summary>内容回填：从略低、略小处弹回原位。delayMs 用于级联错峰。</summary>
    public static void PopIn(VisualElement? el, int delayMs = 0) => Run(el, fromY: 10, fromScale: 0.972, ms: 280, delayMs);

    /// <summary>
    /// 级联回填：一组元素依次弹入（每条错开 stepMs）。
    /// 只建议用在**元素数量少且已实例化**的列表上（如记忆页、日记页）；
    /// CollectionView 这类虚拟化列表拿不到全部子项，别用。
    /// </summary>
    public static void Cascade(IEnumerable<VisualElement?> items, int stepMs = 40, int maxItems = 12)
    {
        int i = 0;
        foreach (var it in items)
        {
            if (i >= maxItems) break;
            PopIn(it, i * stepMs);
            i++;
        }
    }

    /// <summary>核心执行体。</summary>
    private static async void Run(VisualElement? el, double fromY, double fromScale, uint ms, int delayMs = 0)
    {
        if (el is null) return;
        try
        {
            // 还没挂到平台视图（Handler 为空）时不做动画，否则会白设一遍属性
            if (el.Handler is null) return;

            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
                if (el.Handler is null) return;   // 等待期间页面可能已销毁
            }

            el.Opacity = 0;
            el.TranslationY = fromY;
            el.Scale = fromScale;

            _ = el.FadeToAsync(1, (uint)(ms * 0.7), Easing.CubicOut);
            _ = el.TranslateToAsync(0, 0, ms, Easing.CubicOut);
            await el.ScaleToAsync(1, ms, Easing.SpringOut);   // 果冻感：轻微过冲后归位
        }
        catch
        {
            // 兜底复位：任何异常都不能让内容停在"透明 / 偏移"状态
            try { el.Opacity = 1; el.TranslationY = 0; el.Scale = 1; } catch { }
        }
    }
}
// ============================================================================
// 全局主题色管理器（原 ThemeManager.cs，2026-10-29 并入本文件）
// ============================================================================

/// <summary>
/// 全局主题色管理器（单例静态类，替代原先散落各页的「遍历可视树换色」重复实现）。
///
/// 有**两套**机制，按控件所在位置选：
///
/// ① **可视树标记 + Walk**（普通页面级控件）
///      StyleId="theme"         按控件类型自动推断该改哪个颜色属性
///      StyleId="theme-bg"      明确只改背景色（Border 等）
///      StyleId="theme-stroke"  明确只改描边色（Border.Stroke）
///      StyleId="theme-bubble-meta"  语义色标记（时间戳/思考/终端绿字…）：占位，不换色
///    只处理带 theme* 标记的元素；没打标记的自动免疫。
///
/// ② **DynamicResource 资源推送**（列表 / 模板里的控件）
///      ChatPage 的聊天气泡在 CollectionView.ItemTemplate 里，item 视图由虚拟化容器生成，
///      不在 Children 中，Walk **永远遍历不到** → 只能靠 PushBubbleResources 改
///      Application.Resources 里的 BubbleUserColor / BubbleTextColor / BubbleTailColor 来驱动；
///      AI 气泡用固定的 BubbleAIColor / BubbleAITextColor（不跟主题色变）。
///
/// 内存：登记的根元素用 WeakReference 持有，页面被回收后自动失效并在下次刷新时清理。
/// </summary>
internal static class ThemeManager
{
    /// <summary>主题色变更通知（页面若需额外自定义处理可订阅；用完记得退订）。</summary>
    public static event Action? ThemeChanged;

    /// <summary>已登记、需要在主题色变化时刷新的根元素（弱引用，不阻止页面回收）。</summary>
    private static readonly List<WeakReference<VisualElement>> Roots = new();

    /// <summary>
    /// 把当前主题色应用到以 root 为根的子树，并把 root 登记为「主题变化时自动刷新」。
    /// 页面在 OnAppearing 里调一次即可；重复调用安全（内部去重）。
    /// </summary>
    public static void Apply(VisualElement? root)
    {
        if (root is null) return;
        var color = AppSettings.ThemeColor;
        // 冷启动时也要把气泡资源刷成已保存的主题色，否则会用 App.xaml 里写死的初值
        PushBubbleResources(color);
        Walk(root, color);
        Register(root);
    }

    /// <summary>用户切换主题色后调用：先按标记刷新所有已登记页面，再广播给需要自定义处理的页面。</summary>
    public static void NotifyChanged()
    {
        var color = AppSettings.ThemeColor;

        // 先推聊天气泡的颜色资源（气泡在 CollectionView 的 DataTemplate 里，Walk 遍历不到，
        // 只能靠 DynamicResource 刷新 —— 见 PushBubbleResources 的注释）
        PushBubbleResources(color);

        // 再按 StyleId 标记统一刷（就地清理已被 GC 回收的项）
        Roots.RemoveAll(r =>
        {
            if (!r.TryGetTarget(out var root)) return true;  // 已回收 → 移除
            try { Walk(root, color); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThemeManager.Walk] {ex.Message}"); }
            return false;
        });

        // 最后广播：给那些靠代码动态上色、没打标记的控件一次刷新机会
        try { ThemeChanged?.Invoke(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThemeManager.ThemeChanged] {ex.Message}"); }
    }

    /// <summary>
    /// 把「气泡底色 / 气泡文字色 / 尖角色」写进 Application.Resources。
    ///
    /// 为什么气泡必须走资源而不能走 StyleId 遍历：
    /// 消息气泡在 ChatPage 的 CollectionView.ItemTemplate 里，item 视图由虚拟化容器动态生成，
    /// 不在 CollectionView.Children 中，Walk 的 switch 也不处理 ItemsView —— 标记根本命中不了。
    /// DynamicResource 则是元素级订阅：改资源值会刷新**所有已引用该 key 的元素**，
    /// 包括已经渲染的、以及回收后重新绑定的 item。
    ///
    /// 颜色规则（按用户要求 2026-10-04 调整）：
    ///   · **只有「自己」（用户）的气泡**跟着主题色走 —— 底色 = 主题色提亮 0.35，文字色按底色亮度自动取黑/白；
    ///   · **AI 气泡保持固定**（不跟主题色变）—— 沿用原来的深色底 + 浅色字，避免"两边都变色"看着糊成一片。
    /// 注意：必须用 Resources[key] = value 逐键赋值；整个替换 MergedDictionaries 实例不会触发刷新。
    /// </summary>
    private static void PushBubbleResources(Color theme)
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res is null) return;

            // 用户气泡：跟着主题色（提亮 0.35，视觉上"比主题色白一点"），文字色按亮度自动选黑/白
            var userBubble = Lighten(theme, 0.35);
            res["BubbleUserColor"] = userBubble;
            res["BubbleTextColor"] = ContrastOn(userBubble);   // 用户气泡文字色
            res["BubbleTailColor"] = userBubble;

            // AI 气泡：**固定不动**（用户要求只改自己的气泡）。
            // 值来自 App.xaml 里的初值，这里显式写回，保证换主题时不会被上面的规则带偏。
            res["BubbleAIColor"] = AiBubbleColor;
            res["BubbleAITextColor"] = AiBubbleTextColor;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ThemeManager.PushBubble] {ex.Message}"); }
    }

    /// <summary>AI 气泡固定底色（不随主题色变，用户要求只改自己的气泡）。</summary>
    private static readonly Color AiBubbleColor = Color.FromArgb("#241E1E");
    /// <summary>AI 气泡固定文字色（深底配浅字）。</summary>
    private static readonly Color AiBubbleTextColor = Color.FromArgb("#EDE7E6");

    /// <summary>把颜色向白色插值（t 越大越白，0 = 原色，1 = 纯白）。</summary>
    private static Color Lighten(Color c, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Color(
            (float)(c.Red   + (1 - c.Red)   * t),
            (float)(c.Green + (1 - c.Green) * t),
            (float)(c.Blue  + (1 - c.Blue)  * t),
            c.Alpha);
    }

    /// <summary>页面销毁时注销（可选：弱引用已能兜底，显式注销更早释放）。</summary>
    public static void Unregister(VisualElement? root)
    {
        if (root is null) return;
        Roots.RemoveAll(r => r.TryGetTarget(out var t) && ReferenceEquals(t, root));
    }

    /// <summary>登记根元素（去重）。</summary>
    private static void Register(VisualElement root)
    {
        Roots.RemoveAll(r => !r.TryGetTarget(out _));   // 顺手清理死引用
        foreach (var r in Roots)
            if (r.TryGetTarget(out var t) && ReferenceEquals(t, root)) return;  // 已登记
        Roots.Add(new WeakReference<VisualElement>(root));
    }

    /// <summary>递归遍历，按 StyleId 标记给控件上色。没标记的元素原样保留。</summary>
    private static void Walk(VisualElement el, Color color)
    {
        var tag = el.StyleId ?? "";
        if (tag.StartsWith("theme", StringComparison.Ordinal))
            Paint(el, tag, color);

        switch (el)
        {
            case Layout layout:
                foreach (var child in layout.Children)
                    if (child is VisualElement ve) Walk(ve, color);
                break;
            // 注意：Border 不继承 ContentView（两者都只 implements IContentView），
            //    必须单独处理，否则 Border 里的控件全部遍历不到。
            case Border border when border.Content is VisualElement bc:
                Walk(bc, color);
                break;
            case ContentView cv when cv.Content is VisualElement cvc:
                Walk(cvc, color);
                break;
            case ScrollView sv when sv.Content is VisualElement sc:
                Walk(sc, color);
                break;
            case ContentPage page when page.Content is VisualElement pc:
                Walk(pc, color);
                break;
        }
    }

    /// <summary>按控件类型 + 标记语义，把主题色写进正确的颜色属性。</summary>
    private static void Paint(VisualElement el, string tag, Color color)
    {
        // 注意：聊天气泡**不再是**靠 StyleId 换色的 —— 它在 CollectionView.ItemTemplate 里，
        //    Walk 根本遍历不到（虚拟化容器，item 视图不在 Children 中）。
        //    气泡已改为 DynamicResource（用户气泡 BubbleUserColor/BubbleTextColor/BubbleTailColor；
        //    AI 气泡固定的 BubbleAIColor/BubbleAITextColor），见 PushBubbleResources。
        //    这里只保留下面的通用标记处理。

        // 气泡内的「语义色」元素（时间戳/思考/终端绿字/错误行…）：
        // 这个标记只是用来**标记它们是语义色、不要被当成普通主题元素刷掉**，本身不换色。
        // 目前这些元素都写死了颜色且不在气泡资源覆盖范围内，所以原样返回即可。
        if (tag == "theme-bubble-meta") return;

        var bgOnly = tag == "theme-bg";
        var strokeOnly = tag == "theme-stroke";
        var auto = !bgOnly && !strokeOnly;

        switch (el)
        {
            case Label l:
                if (auto) l.TextColor = color;
                break;
            case Button b:
                if (!strokeOnly) b.BackgroundColor = color;
                break;
            case CheckBox c:
                if (auto) c.Color = color;
                break;
            case Switch s:
                if (!strokeOnly) s.OnColor = color;
                break;
            case ActivityIndicator ai:
                if (auto) ai.Color = color;
                break;
            case BoxView bv:
                if (!strokeOnly) bv.Color = color;
                break;
            case MorphIcon mi:
                if (auto) mi.IconColor = color;
                break;
            case Slider sl:
                if (!strokeOnly) sl.MinimumTrackColor = color;
                break;
            case Border bd:
                if (strokeOnly) bd.Stroke = new SolidColorBrush(color);
                else if (bgOnly) bd.BackgroundColor = color;
                else if (bd.BackgroundColor is null) bd.Stroke = new SolidColorBrush(color);
                else bd.BackgroundColor = color;
                break;
        }
    }

    /// <summary>
    /// 给定底色，返回一个在它上面读起来清楚的文字色：
    /// 亮底用深色字、暗底用浅色字（按相对亮度阈值 0.6 切）。
    /// 用于推**用户气泡**的 BubbleTextColor —— 主题色可能是深蓝、也可能是浅金，得自动选字色。
    /// </summary>
    private static Color ContrastOn(Color bg)
    {
        double lum = 0.299 * bg.Red + 0.587 * bg.Green + 0.114 * bg.Blue;
        return lum > 0.6 ? Color.FromArgb("#1A1A1A") : Color.FromArgb("#FFFFFF");
    }
}

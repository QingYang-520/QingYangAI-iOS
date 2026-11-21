using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls.Xaml;
using Microsoft.Maui.Controls.Internals;

namespace 青阳AI;

public partial class ChatPage : ContentPage
{
    // 超时放宽到 5 分钟：流式请求的 Timeout 覆盖整个响应体读取，
    // 用默认 100 秒的话，长回复（尤其带深度思考的）会被中途掐断。
    private static readonly HttpClient _httpClient = CreateHttpClient();

    /// <summary>流式读取时，多久没收到任何数据就判定"卡死"并主动断掉（秒）。</summary>
    private const int SseStallSeconds = 60;

    /// <summary>
    /// 本地模型专用的看门狗时长（秒）。
    /// 本地服务**首次请求要先把模型加载进内存**，7B/8B 在手机或低配电脑上可能要 1~3 分钟，
    /// 用 60 秒会把"正在加载"误判成"卡死"。这里放宽到 5 分钟。
    /// </summary>
    private const int SseStallSecondsLocal = 300;

    /// <summary>当前生效的看门狗时长：本地模型放宽，云端保持 60 秒。</summary>
    private static int EffectiveStallSeconds =>
        AppSettings.UseLocalModel && AppSettings.LocalModelConfigured
            ? SseStallSecondsLocal
            : SseStallSeconds;

    /// <summary>
    /// 建聊天用的 HttpClient。
    /// 默认走平台原生栈；设置里打开「流式兼容模式」就换纯托管 SocketsHttpHandler ——
    /// 有些 ROM / 网络栈会偷偷把响应缓冲起来，那样流式就废了（表现：整段一下子蹦出来）。
    /// </summary>
    private static HttpClient CreateHttpClient()
    {
        // 超时必须**大于**看门狗时长，否则 HttpClient 会先抛超时，
        // 我们就没机会给出"服务器没响应"这种更友好的提示了。
        // 看门狗本地模式最长 5 分钟 → HttpClient 给 10 分钟。
        var timeout = TimeSpan.FromMinutes(10);
        try
        {
            if (AppSettings.UseManagedHttpStack)
                return new HttpClient(new System.Net.Http.SocketsHttpHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(30)
                })
                { Timeout = timeout };
        }
        catch { }
        return new HttpClient { Timeout = timeout };
    }

    /// <summary>
    /// 注意：曾经在这里给流式请求加 `Accept: text/event-stream` + `Cache-Control: no-cache`，
    /// 想的是"有些网关靠 Accept 决定边收边发"。**实测没帮助，反而疑似让服务端行为异常
    /// （表现为一直转圈等不到响应），已撤掉。**
    /// 流式真正的开关是 `SendAsync(..., HttpCompletionOption.ResponseHeadersRead)`，
    /// 那个已经到位了，别再往请求头上加猜测性的东西。
    /// </summary>
    private void ApplyStreamingHeaders()
    {
        // 故意留空：不再设置任何猜测性的请求头
    }
    public ObservableCollection<ChatMsg> Messages { get; set; }
    private DateTime _pressTime;
    private ChatMsg? _pressedMsg;
    private const double LongPressMs = 500;
    private readonly SemaphoreSlim _compressLock = new(1, 1);
    private bool _followBottom = true; // 是否跟随到底部（用户手动上滑后停止跟随）
    private int _lastCacheHit;
    private int _lastCacheMiss;

    public ChatPage()
    {
InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, false);
        Messages = new ObservableCollection<ChatMsg>();
        BindingContext = this;
        msgList.Scrolled += OnMsgListScrolled;
        msgList.Loaded += OnMsgListLoaded;
        btnSend.Clicked += SendClick;
        imgAvatar.Source = AvatarService.Load();
        ApplyApiKey();
        _ = InitAsync();
    }

    /// <summary>是否已完成首次历史加载（冷启动时 OnAppearing 早于加载完成，用此标志防竞态）。</summary>
    private bool _historyLoaded;

    /// <summary>本次进入是否已检查过「字元礼」活动（避免切页反复触发）。</summary>
    private bool _giftChecked;

    /// <summary>本次进入是否已检查过 JIT（避免切页反复触发）。</summary>
    private bool _jitChecked;

    /// <summary>空状态语句（每次打开应用随机抽一句）。</summary>
    private static readonly string[] EmptyMottos =
        { "解码万象玄理", "奔赴智海遐方", "叩问数域洪流", "洞悉未知奥义" };
    private string _motto = "";

    /// <summary>更新空状态：无消息时屏幕正中央显示随机语句，有消息时隐藏。</summary>
    public void UpdateEmptyState()
    {
        bool empty = Messages.Count == 0;
        emptyState.IsVisible = empty;
        if (empty) emptyMotto.Text = _motto;
    }

    /// <summary>字号自适应：六个字至少占屏幕宽度的 3/4。</summary>
    #if ANDROID
    private void OnMsgListScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
        // 用户手动上滑后停止自动跟随底部
        if (e.LastVisibleItemIndex < Messages.Count - 2)
            _followBottom = false;
    }
#else
    private void OnMsgListScrolled(object? sender, EventArgs e)
    {
        // Windows 平台不需要滚动事件处理
    }
#endif

    /// <summary>消息长按开始：记录时间和目标消息。</summary>
    private void OnMsgPointerPressed(object? sender, PointerEventArgs e)
    {
        if (sender is BindableObject bo && bo.BindingContext is ChatMsg msg)
        {
            _pressTime = DateTime.Now;
            _pressedMsg = msg;
        }
    }

    /// <summary>消息长按结束：超过阈值则弹出操作菜单。</summary>
    private async void OnMsgPointerReleased(object? sender, PointerEventArgs e)
    {
        if (_pressedMsg == null) return;
        var elapsed = (DateTime.Now - _pressTime).TotalMilliseconds;
        var msg = _pressedMsg;
        _pressedMsg = null;

        if (elapsed < LongPressMs) return;

        // 长按：收藏 / 复制 / 删除
        // 收藏项文案随当前状态变（已收藏 → 显示「取消收藏」）
        var favLabel = msg.IsFavorite ? "取消收藏" : "收藏";
        var action = await DisplayActionSheetAsync("消息操作", "取消", "删除", favLabel, "复制文字");

        if (action == favLabel)
            await ToggleFavoriteAsync(msg);
        else if (action == "复制文字" && !string.IsNullOrEmpty(msg.Content))
            await Clipboard.Default.SetTextAsync(msg.Content);
        else if (action == "删除")
        {
            Messages.Remove(msg);
            await ChatStore.Instance.DeleteMessagesAsync(new[] { msg.Id });
        }
    }

    /// <summary>
    /// 切换某条消息的收藏状态（长按菜单用）。
    ///
    /// 注意：还没入库的消息（Id == 0，比如刚发出、保存还没回写主键）不能收藏 ——
    /// 库里没有对应行，UPDATE 会影响 0 行，界面却以为成功了。
    /// 所以这里先拦住并提示，让用户等一下再长按。
    /// </summary>
    private async Task ToggleFavoriteAsync(ChatMsg msg)
    {
        if (msg.Id <= 0)
        {
            await DisplayAlertAsync("还不能收藏", "这条消息还在保存中，稍等一下再长按试试。", "知道了");
            return;
        }

        try
        {
            bool want = !msg.IsFavorite;
            await ChatStore.Instance.SetFavoriteAsync(msg.Id, want);
            msg.IsFavorite = want;   // 同步内存对象，长按菜单下次就显示正确的项
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("收藏失败", ex.Message, "知道了");
        }
    }

    /// <summary>隐藏软键盘（Android 专用）。</summary>
#if ANDROID
    private void HideKeyboard()
    {
        var window = Platform.CurrentActivity?.Window;
        if (window != null)
        {
            var imm = Platform.CurrentActivity?.GetSystemService("input_method") as Android.Views.InputMethods.InputMethodManager;
            var view = window.DecorView.RootView;
            imm?.HideSoftInputFromWindow(view.WindowToken, 0);
        }
    }
#else
    private void HideKeyboard() { }
#endif

    /// <summary>滚动到底部（如果 _followBottom=true）。</summary>
    private void ScrollToBottom()
    {
        if (_followBottom && Messages.Count > 0)
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
    }

    /// <summary>CollectionView 首次布局完成后再定位底部；Loaded 后渲染已就绪，滚动一次即到位。</summary>
    private void OnMsgListLoaded(object? sender, EventArgs e)
    {
        if (Messages.Count == 0) return;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(100), () =>
        {
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        });
    }

    /// <summary>必要时跟随到底部（防消息发送时被用户手动上滑干扰）。</summary>
    private void FollowBottomIfNeeded()
    {
        if (_followBottom) ScrollToBottom();
    }

    // ══════════════ 打字机（显示侧逐字上屏）══════════════
    //
    // 网络侧现在是全速收流（块来得很快），显示侧以前是"块到就整块刷"，
    // 看起来就是一次蹦出一大串。这里加一层显示缓冲：Content 照常全速累积（存库/给模型都用它），
    // 界面绑的 DisplayContent 由计时器逐字揭开。

    private ChatMsg? _typingBubble;
    private IDispatcherTimer? _typeTimer;
    private int _typeTicks;
    private bool _streamDone;

    /// <summary>开始给这个气泡打字。</summary>
    private void BeginTyping(ChatMsg bubble)
    {
        try
        {
            // 上一个还没打完就直接放全 —— 别让它永远卡在半截
            _typingBubble?.FlushReveal();
            _typingBubble = bubble;
            _typeTicks = 0;
            _streamDone = false;
            bubble.BeginReveal();
            _typeTimer ??= CreateTypeTimer();
            if (!_typeTimer.IsRunning) _typeTimer.Start();
        }
        catch { }
    }

    /// <summary>
    /// 流收完了。**注意：这里不"一次全放出来"** —— 那样末尾会跳一下。
    /// 只是标记一下，让计时器用更猛的步长把剩下的字排空，尾巴依然是逐字出的。
    ///
    /// 注意：兜底：计时器有可能因为任何意外已停（例如气泡被换掉后 _typingBubble 变 null），
    /// 这里确保它还在跑，否则剩下的字永远揭不开，界面会停在半句话上。
    /// </summary>
    private void EndTyping()
    {
        _streamDone = true;
        try
        {
            if (_typingBubble != null)
            {
                _typeTimer ??= CreateTypeTimer();
                if (!_typeTimer.IsRunning) _typeTimer.Start();
            }
        }
        catch { }
    }

    private IDispatcherTimer CreateTypeTimer()
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(25);   // 40 帧/秒
        t.Tick += (_, _) =>
        {
            try
            {
                var b = _typingBubble;
                if (b == null) { _typeTimer?.Stop(); return; }

                if (!b.RevealTick(_streamDone))
                {
                    // 追平了：交回给 Content 直接驱动，计时器停掉省电
                    b.FlushReveal();
                    _typingBubble = null;
                    _streamDone = false;
                    _typeTimer?.Stop();
                }

                // 滚动别每帧都做 —— 40 次/秒的重排会卡
                if (++_typeTicks % 4 == 0) FollowBottomIfNeeded();
            }
            catch { }
        };
        return t;
    }

    /// <summary>首次加载（异步，不阻塞构造）。</summary>
    private async Task InitAsync()
    {
        // 列表立即显示（XAML 初始 IsVisible=False，这里第一时间打开，避免整页空白）
        msgList.IsVisible = true;
        try
        {
            await LoadHistoryAsync();
#if ANDROID
            _deviceContext = await DeviceContextService.BuildAsync();
            _memoryContext = await MemoryService.GetMemoryContextAsync();
            _diaryContext = await DiaryService.GetLatestDiaryContextAsync();
            _innerContext = InnerLifeService.BuildInnerContext();
            _observedContext = CareWatch.BuildObservedContext();
#endif
            _reviewContext = MessageReviewService.BuildReviewContext();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Init] {ex.Message}"); }
        _historyLoaded = true;
        UpdateEmptyState();
        // 兜底：列表显示后再异步滚一次（Loaded 事件也可能触发，重复也无害，无动画）
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250), () =>
        {
            if (Messages.Count > 0)
                msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        });
        UpdateMoodUi();
        UpdateReasoningChip();
        ThemeManager.Apply(this);
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (height <= 0) return;

        ApplyListInsets(width, height);
        ApplyPanelInsets(height);
    }

    /// <summary>缓存上一次算过的边距，避免每帧都改 Margin 触发重排。</summary>
    private Thickness _lastListMargin = new Thickness(0, 0, 0, -1);

    /// <summary>
    /// 按标题栏 / 输入栏的**实测高度**给消息列表设上下避让边距。
    /// 两个栏都是 VerticalOptions="Start"/"End" 的自适应高度元素，测量值要等布局跑完才有，
    /// 取不到时回退到保守估计，绝不退化成 0（0 就会盖住内容）。
    /// </summary>
    private void ApplyListInsets(double width, double height)
    {
        try
        {
            // 标题栏：实测高度；未测量出来（-1 / 0）时按 52 估
            double topH = titleBar.Height;
            if (topH <= 0 || double.IsNaN(topH)) topH = 52;

            // 输入栏：实测高度 + 它的下边距（Margin="12,0,12,14" 里的 14）
            double barH = inputBar.Height;
            if (barH <= 0 || double.IsNaN(barH)) barH = 56;
            double bottomH = barH + inputBar.Margin.Bottom;

            // 各留一点呼吸空间，别让气泡紧贴栏边
            var top = topH + 6;
            var bottom = bottomH + 6;

            var want = new Thickness(0, top, 0, bottom);
            if (Math.Abs(_lastListMargin.Top - want.Top) < 0.5
                && Math.Abs(_lastListMargin.Bottom - want.Bottom) < 0.5) return;
            _lastListMargin = want;
            msgList.Margin = want;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ApplyListInsets] {ex.Message}"); }
    }

    /// <summary>
    /// 三条杠菜单的定位：底边距 = 输入栏实测高 + 其下边距 + 间隙，让菜单刚好坐在输入栏上沿。
    /// （权限面板已并入菜单主气泡，不再有独立的浮动面板需要定位。）
    /// </summary>
    private void ApplyPanelInsets(double height)
    {
        try
        {
            double barH = inputBar.Height;
            if (barH <= 0 || double.IsNaN(barH)) barH = 56;

            var bottomMargin = barH + inputBar.Margin.Bottom + 8;

            if (menuRoot is not null)
            {
                var wantMenu = new Thickness(12, 0, 12, bottomMargin);
                if (Math.Abs(menuRoot.Margin.Bottom - wantMenu.Bottom) > 0.5)
                    menuRoot.Margin = wantMenu;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ApplyPanelInsets] {ex.Message}"); }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        // 回到页面时收起残留的菜单/权限面板（从设置页回来不该还开着）
        try { CloseMenu(); } catch { }
        JellyAnim.EnterPage(Content);   // 页面进入：轻微上浮 + 淡入 + 极轻果冻回弹
        // 把页面里的隐藏 WebView 交给 AI 当浏览器用（顺带把 UA / 图片开关打进去）
        try { WebAgent.Attach(agentWeb); } catch { }
        try
        {
            // 无论 InitAsync 是否完成，列表都保持可见（首次由 InitAsync 负责加载历史）
            msgList.IsVisible = true;
            if (_historyLoaded)
            {
#if ANDROID
                _deviceContext = await DeviceContextService.BuildAsync();
                _observedContext = CareWatch.BuildObservedContext();
#endif
                await SyncNewFromDbAsync();
            }
            UpdateContextLabel();
            UpdateCacheHitLabel();
            UpdateMoodUi();
            UpdateReasoningChip();
            ThemeManager.Apply(this);

            // 兜底再算一次避让边距：OnSizeAllocated 首次可能早于两栏测量完成，
            // 这里布局已经稳定，按实测值补正（内部有变化才改，不会抖动）。
            ApplyListInsets(Width, Height);
            ApplyPanelInsets(Height);

            // 从系统设置回来后重新校验存储权限（用户可能刚授予、也可能收回了）
            await RevalidateStoragePermissionAsync();
            // 权限面板现在长在菜单主气泡里：菜单开着且停在权限分组时，回来顺手刷新一次
            if (menuLayer.IsVisible && _menuPane == "perm") RefreshPermPanel();

            // iOS：自动获取 JIT（失败弹窗提示 AltJIT）；非 iOS 目标静默返回
            if (!_jitChecked)
            {
                _jitChecked = true;
                _ = Task.Run(async () =>
                {
                    try { await JitService.EnsureJitAsync(this); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[JitService] {ex.Message}"); }
                });
            }

            // 程序员节「字元礼」：后台静默查活动，仅当天开放且未领过时弹窗一次（非活动日静默）
            if (!_giftChecked)
            {
                _giftChecked = true;
                _ = Task.Run(async () =>
                {
                    try { await GiftFlow.TryShowOnLaunchAsync(this); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[GiftFlow] {ex.Message}"); }
                });
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OnAppearing] {ex.Message}"); }
    }

    /// <summary>
    /// 每次回到页面时校验系统存储权限，防止出现"APP 里显示已授权、系统里其实没有"的假放开。
    /// - 刚授予：把沙盒里的旧工作区文件搬到公共存储，避免"一授权文件就消失"；
    /// - 被收回：自动降级文件权限级别。
    /// </summary>
    private async Task RevalidateStoragePermissionAsync()
    {
        try
        {
            StorageAccess.InvalidateProbe();   // 刚回页面，重新探测一次真实写入能力
            bool granted = StorageAccess.IsAllFilesGranted();
            bool wasGranted = AppSettings.LastStorageGranted;

            if (granted && !wasGranted)
            {
                AppSettings.LastStorageGranted = true;
                var (ok, msg) = await StorageAccess.MigrateSandboxToPublicAsync();
                if (ok)
                    await DisplayAlertAsync("工作区已迁移",
                        msg + "\n\n以后 Ta 生成的文件会直接放在这里，你在文件管理器里能直接看到。", "好");
            }
            else if (granted != wasGranted)
            {
                AppSettings.LastStorageGranted = granted;
            }

            if (granted) return;

            // —— 权限不存在（或已被收回）：降级，避免"假放开" ——
            bool changed = false;
            if (AppSettings.FileAccessLevel >= 3)
            {
                AppSettings.FileAccessLevel = 2;   // 3/4 都依赖系统权限，降为「仅修改工作区」
                changed = true;
            }
            if (AppSettings.FullAccess)
            {
                AppSettings.FullAccess = false;
                changed = true;
            }
            if (changed)
                System.Diagnostics.Debug.WriteLine("[Storage] 系统权限已失效，文件权限自动降级");
        }
        catch { }
    }

    /// <summary>并发闸：同一时刻只允许一次数据库同步（OnAppearing 可能被连续触发多次）。</summary>
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    /// <summary>消息内容指纹：同一侧 + 同正文 + 同图片 = 同一条消息。</summary>
    private static string MessageFingerprint(ChatMsg m) =>
        $"{(m.IsUser ? "U" : "A")}\u0001{m.Content}\u0001{m.ImageUrl}";

    /// <summary>
    /// 增量同步数据库中的新消息（后台主动消息等）。
    ///
    /// 注意：这里是「打开设置再关闭就多一份消息」的现场，逻辑要抠清楚：
    ///
    /// 存库是 `_ = SaveMessageAsync(...)` 发了不管的，而回写 Id 要等 SQLite 异步插入完成。
    /// 于是存在一个窗口期：**库里有这行，内存对象 m.Id 还是 0**。
    /// 旧代码只按 `Id > 0` 建 existingIds，这类消息进不了集合 → 被当成新消息又 Add 一遍。
    ///
    /// 现在的做法分三步：
    ///   ① 先按 Id 认（快捷路径，已回填 Id 的就是它）；
    ///   ② 认不出来的行，再看看内存里有没有「Id==0 且同侧同内容」的待回填对象 ——
    ///      有就把库里拿到的 Id **回填**给内存对象（不新增），顺手把库里的权威内容覆盖回去，
    ///      这样流式中途存的中途快照会被完整内容修正；
    ///   ③ 全都对不上的才是真·新消息，Add 进去，并把它的指纹与 Id 立刻并入集合，
    ///      保证同一次循环里后续行能继续正确判重（旧代码漏了这步）。
    /// </summary>
    private async Task SyncNewFromDbAsync()
    {
        // 已有同步在跑就直接跳过：两次并发各加一遍 = 界面上出现两份
        if (!await _syncLock.WaitAsync(0)) return;
        try
        {
            var all = await ChatStore.Instance.LoadMessagesAsync();

            // 已落库且已回填 Id 的消息：按 Id 去重
            var existingIds = new HashSet<int>(Messages.Where(m => m.Id > 0).Select(m => m.Id));
            // 尚未落库的消息（Id 还是 0）：按内容指纹建索引，值为该内存对象（用于回填 Id）
            var pendingByFp = new Dictionary<string, ChatMsg>(StringComparer.Ordinal);
            foreach (var m in Messages)
            {
                if (m.Id > 0) continue;
                var fp = MessageFingerprint(m);
                if (!pendingByFp.ContainsKey(fp)) pendingByFp[fp] = m;
            }

            bool added = false;
            foreach (var msg in all)
            {
                // ① 已按 Id 认得
                if (msg.Id > 0 && existingIds.Contains(msg.Id)) continue;

                var fp = MessageFingerprint(msg);

                // ② 可能是「已落库但内存 Id 还是 0」的那条 → 回填 Id，不新增
                if (msg.Id > 0 && pendingByFp.Remove(fp, out var pending))
                {
                    pending.Id = msg.Id;
                    // 库里的是权威完整内容：把流式中途的快照修正过来，
                    // 否则界面显示的正文与库里长期不一致（下次冷启动就"变样"了）
                    if (!string.IsNullOrEmpty(msg.Content)) pending.Content = msg.Content;
                    existingIds.Add(msg.Id);
                    continue;
                }

                // ③ 真·新消息（后台主动消息、通知快捷回复等）
                if (existingIds.Contains(msg.Id)) continue;
                if (pendingByFp.ContainsKey(fp)) continue;   // 同内容的待回填消息已在列表里
                Messages.Add(msg);
                if (msg.Id > 0) existingIds.Add(msg.Id);
                added = true;
            }

            if (added && Messages.Count > 0) ScrollToBottom();
        }
        catch { }
        finally { _syncLock.Release(); }
    }

    /// <summary>加载历史消息（SQLite，异步）。</summary>
    private async Task LoadHistoryAsync()
    {
        var loaded = await ChatStore.Instance.LoadMessagesAsync();
        foreach (var msg in loaded) Messages.Add(msg);
        #if ANDROID
        // 初始化感知上下文（异步不阻塞）
        _ = Task.Run(async () =>
        {
            _deviceContext = await DeviceContextService.BuildAsync();
            _memoryContext = await MemoryService.GetMemoryContextAsync();
            _diaryContext = await DiaryService.GetLatestDiaryContextAsync();
            _innerContext = InnerLifeService.BuildInnerContext();
            _observedContext = CareWatch.BuildObservedContext();
            UpdateMoodUi();
            UpdateReasoningChip();
            // 主题色刷新必须回 UI 线程（这里是后台线程）
            Dispatcher.Dispatch(() => ThemeManager.Apply(this));
        });
#endif
    }

    /// <summary>刷新心情显示（直接读取 InnerLifeService.Mood）。</summary>
    private void UpdateMoodUi()
    {
        try
        {
            var mood = InnerLifeService.Mood;
            if (string.IsNullOrWhiteSpace(mood))
            {
                lblMood.Text = "";
                moodDot.IsVisible = false;
                return;
            }

            // 心情用「主题色系小圆点 + 文字」表示，不使用 emoji 表情
            lblMood.Text = " " + mood;
            moodDot.IsVisible = true;
            moodDot.BackgroundColor = Color.FromArgb(InnerLifeService.MoodColor(mood));
        }
        catch
        {
            lblMood.Text = "";
            try { moodDot.IsVisible = false; } catch { }
        }
    }

    /// <summary>刷新推理等级芯片：模型支持推理或开启强制思考时显示。</summary>
    private void UpdateReasoningChip()
    {
        try
        {
            bool show = AppSettings.ReasoningSupported || AppSettings.ForceThinking;
            reasonChip.IsVisible = show;
            if (show)
            {
                var options = AppSettings.GetReasoningOptions();
                var choice = AppSettings.ReasoningChoice;
                reasonLbl.Text = AppSettings.ForceThinking && options.Count == 0
                    ? "思考:开"
                    : (string.IsNullOrEmpty(choice) ? "思考:关" : "思考:" + choice);
            }

            // 三条杠菜单里的「推理强度」次气泡也跟着这个能力走
            if (menuLayer.IsVisible) RefreshMenu();
        }
        catch { reasonChip.IsVisible = false; }
    }

    /// <summary>点击推理芯片：弹出档位选择器。</summary>
    private async void OnReasoningChipTapped(object? sender, EventArgs e)
    {
        var options = AppSettings.GetReasoningOptions();
        if (options.Count == 0)
        {
            AppSettings.ForceThinking = !AppSettings.ForceThinking;
            UpdateReasoningChip();
            return;
        }
        var current = AppSettings.ReasoningChoice;
        var allOptions = new List<string> { "关", current };
        foreach (var o in options) if (!allOptions.Contains(o)) allOptions.Add(o);
        var selected = await DisplayActionSheetAsync("选择推理等级", null, null, allOptions.ToArray());
        if (selected != null)
        {
            if (selected == "关") AppSettings.ReasoningChoice = "";
            else AppSettings.ReasoningChoice = selected;
            UpdateReasoningChip();
        }
    }

    /// <summary>刷新标题栏上下文用量显示：上下文:352K/1M·35.2%（模型返回的 prompt token 占用 / 上下文窗口 · 占比）。</summary>
    private void UpdateContextLabel()
    {
        try
        {
            var stats = UsageStats.Current;
            int used = stats.LastPromptTokens;          // 模型返回 usage 的 prompt 总数（本次占用）
            if (used <= 0)
            {
                // 模型未返回 usage（部分中转会摘掉）：用本地估算兜底，保证有数
                used = Messages.Sum(m => EstimateTokens(m.Content));
            }
            int cap = AppSettings.EffectiveMaxTokens;   // 模型上下文窗口（从 /models 的 context_length 抓取）
            if (cap <= 0) cap = AppSettings.ContextFallbackLimit;
            double pct = (double)used / cap * 100.0;
            lblCtx.Text = $"上下文:{FormatK(used)}/{FormatK(cap)}·{pct:F1}%";
        }
        catch { lblCtx.Text = ""; }
    }

    /// <summary>刷新缓存命中率显示：缓存命中:98%（模型返回 cached/prompt 的占比）。</summary>
    private void UpdateCacheHitLabel()
    {
        try
        {
            var stats = UsageStats.Current;
            double rate = stats.LastHitRate;
            lblCacheHit.Text = $"缓存命中:{rate:F0}%";
        }
        catch { lblCacheHit.Text = ""; }
    }

    /// <summary>数字转 K/M 人性化格式（352K / 1M）。</summary>
    private static string FormatK(int value)
    {
        if (value >= 1_000_000) return (value / 1_000_000.0).ToString("0.#") + "M";
        if (value >= 1_000) return (value / 1_000.0).ToString("0.#") + "K";
        return value.ToString();
    }

    private void ApplyApiKey()
    {
        ApplyStreamingHeaders();
        // 注意：刻意不写 _httpClient.DefaultRequestHeaders：
        //    _httpClient 是 static 共享实例，而心跳/复盘/生图等后台请求随时可能并发跑。
        //    在共享实例上 Remove/Add Authorization 会和并发请求打架
        //    （轻则 InvalidOperationException，重则用到别的配置套的 Key）。
        //    Authorization 改为每个 HttpRequestMessage 自己带，见 AttachAuth()。
    }

    /// <summary>
    /// 给单个请求挂上 Authorization（不再改共享 HttpClient 的默认头）。
    /// 用 EffectiveChatApiKey：启用本地模型时这里是本地服务的 key（多数留空）。
    /// </summary>
    private static void AttachAuth(HttpRequestMessage req, string? keyOverride = null)
    {
        try
        {
            var key = (keyOverride ?? AppSettings.EffectiveChatApiKey)?.Trim() ?? "";
            if (key.Length == 0) return;
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");
        }
        catch { }
    }

    private async void OnSettingsClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new SettingsPage());
        // 返回后刷新名字、Key、URL
        ApplyApiKey();
    }

    /// <summary>打开日记页。</summary>
    private async void OnDiaryClicked(object? sender, EventArgs e)
    {
        await Navigation.PushAsync(new DiaryPage());
    //这行很特殊，你也一样
    }

    /// <summary>
    /// 打开搜索页（标题栏放大镜）。
    ///
    /// 进去之前把本页的定位回调挂到 <see cref="ChatJump"/> 上，
    /// 搜索页点某条结果时会顺着这个回调叫回来滚到那条消息。
    /// 回调常驻不会有副作用：ScrollToMessageId 对找不到的 Id 静默忽略，
    /// 而 Request 只在用户主动点结果时才被调。
    /// </summary>
    private async void OnSearchClicked(object? sender, EventArgs e)
    {
        ChatJump.ScrollRequested = ScrollToMessageId;
        try
        {
            await Navigation.PushAsync(new SearchPage());
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("打不开搜索", ex.Message, "好");
        }
    }

    /// <summary>
    /// 滚动定位到指定主键的消息（SearchPage 跳回来时调）。
    ///
    /// 两条注意：
    ///  · 弹回本页时 OnAppearing 里的数据库同步可能还没跑完，
    ///    目标消息一时半会儿还不在 Messages 里 —— 所以重试几次，别一次找不到就放弃；
    ///  · 定位成功后关掉「跟随底部」，否则下一条新消息会把用户从结果处拽走。
    /// </summary>
    public void ScrollToMessageId(int id)
    {
        if (id <= 0) return;
        _followBottom = false;
        TryScrollToMessageId(id, attempt: 0);
    }

    /// <summary>定位最多重试几次（每次间隔 250ms，合计约 1.5 秒，够数据库同步跑完）。</summary>
    private const int ScrollRetryMax = 6;

    private void TryScrollToMessageId(int id, int attempt)
    {
        try
        {
            int index = -1;
            for (int i = 0; i < Messages.Count; i++)
            {
                if (Messages[i].Id == id) { index = i; break; }
            }

            if (index < 0)
            {
                // 还没同步进来：再等等（回页时的数据库同步是异步的）
                if (attempt < ScrollRetryMax)
                {
                    Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(250),
                        () => TryScrollToMessageId(id, attempt + 1));
                }
                return;
            }

            msgList.ScrollTo(index, position: ScrollToPosition.Center, animate: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScrollToMessageId] {ex.Message}");
        }
    }

    /// <summary>Entry 回车发送。</summary>
    private void OnInputCompleted(object? sender, EventArgs e)
    {
        SendClick(this, EventArgs.Empty);
    }

    /// <summary>点击输入栏：聚焦输入框。</summary>
    private void OnInputBarTapped(object? sender, EventArgs e)
    {
        txtInput.Focus();
    }

    /// <summary>点击语音按钮：语音识别后发送。</summary>
    private async void OnVoiceClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await RecognizeVoiceAsync();
            if (!string.IsNullOrWhiteSpace(result))
            {
                txtInput.Text = result;
                SendClick(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("语音识别", $"识别失败：{ex.Message}", "确定");
        }
    }

    // ─────────────── 麦克风按钮：短按语音输入 / 长按语音通话 ───────────────

    /// <summary>长按判定阈值（毫秒）——和消息长按保持一致。</summary>
    private const double VoiceLongPressMs = 500;

    /// <summary>麦克风按钮按下的时刻。</summary>
    private DateTime _voicePressTime;

    /// <summary>本次按压是否已经当成「长按」处理过（防重复进页面）。</summary>
    private bool _voiceLongPressHandled;

    /// <summary>
    /// 麦克风按钮按下：记时间，并起一个计时器，到点还没松手就进语音通话页。
    ///
    /// 为什么用 Pointer 而不是 TapGestureRecognizer：
    /// Tap 在抬手时必触发，如果同时挂 Tap + 长按判断，长按松手会「既进通话页又跑一次识别」。
    /// 全部走 Pointer 按下/抬起，由代码自己分辨短按与长按，两边的行为才互不干扰。
    /// </summary>
    private void OnVoicePointerPressed(object? sender, PointerEventArgs e)
    {
        _voicePressTime = DateTime.Now;
        _voiceLongPressHandled = false;

        // 到点还没松手 → 判定长按，进语音通话页（长按当场就有反应，不等松手）
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)VoiceLongPressMs);
                if (_voiceLongPressHandled) return;
                if ((DateTime.Now - _voicePressTime).TotalMilliseconds < VoiceLongPressMs) return;

                _voiceLongPressHandled = true;

                // 长按震动一下，给用户「我收到了」的反馈
                try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); } catch { }

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try
                    {
                        // 进通话前先把可能正在播的播报停掉，避免两路声音打架
                        try { TtsPlayer.Stop(); } catch { }
                        await Navigation.PushAsync(new VoiceCallPage());
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlertAsync("语音通话", "打不开通话页：" + ex.Message, "确定");
                    }
                });
            }
            catch { }
        });
    }

    /// <summary>麦克风按钮抬起：没被当成长按 → 按原来的短按逻辑走语音输入。</summary>
    private void OnVoicePointerReleased(object? sender, PointerEventArgs e)
    {
        if (_voiceLongPressHandled) return;   // 已经进通话页了，别再识别一次

        var elapsed = (DateTime.Now - _voicePressTime).TotalMilliseconds;
        _voicePressTime = DateTime.MinValue;

        // 按得极短（< 80ms）当误触，忽略
        if (elapsed < 80) return;

        OnVoiceClicked(this, EventArgs.Empty);
    }

    /// <summary>手指移出按钮：取消本次按压（既不算长按也不算短按）。</summary>
    private void OnVoicePointerExited(object? sender, PointerEventArgs e)
    {
        _voicePressTime = DateTime.MinValue;
    }

    private async void SendClick(object? sender, EventArgs e)
    {
        var text = txtInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        _followBottom = true; // 发送消息：恢复跟随底部
        _lastCacheHit = 0;
        _lastCacheMiss = 0;
        UpdateCacheHitLabel();
        var userMsg = new ChatMsg { Content = text, IsUser = true };
        Messages.Add(userMsg);
        UpdateEmptyState();
        txtInput.Text = "";
        HideKeyboard();
        btnSend.IsEnabled = false;
        ScrollToBottom();
        _ = ChatStore.Instance.SaveMessageAsync(userMsg);

        var aiMsg = new ChatMsg { Content = "", IsUser = false, IsWaiting = true };
        Messages.Add(aiMsg);
        ScrollToBottom();

        try
        {
            if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiUrl))
            {
                aiMsg.Content = AppSettings.UseLocalModel
                    ? "请先在「设置 → 本地模型」里填好本地服务地址和模型名"
                    : "请先在「设置」中填写 API URL";
                return;
            }
            // 发送前刷新感知上下文，保证"此刻"信息准确（时间/前台应用/通知等）
            _deviceContext = await DeviceContextService.BuildAsync();
            // 刷新上一条消息的复盘上下文（如果开关开着且已生成）
            _reviewContext = MessageReviewService.BuildReviewContext();

            if (AppSettings.AgentLoopEnabled)
            {
                // Agent 循环执行模式：多轮自主推进，边跑边出过程，完成后折叠并重新总结
                await RunAgentLoopAsync(text, aiMsg);
                return;
            }

            try
            {
                await StreamRequest(text, aiMsg);
            }
            catch (Exception ex) when (IsTransientNetworkError(ex))
            {
                // 切后台被系统冻结、服务端掐线等导致的连接中断：自动重试一次
                aiMsg.Content = "";
                aiMsg.ErrorText = "";   // 重试就把上次的错误行清掉
                aiMsg.Thinking = "";
                aiMsg.ThinkingExpanded = false;
                await StreamRequest(text, aiMsg);
            }
        }
        catch (Exception ex)
        {
            ShowErrorOn(aiMsg, FriendlyNetworkError(ex));
        }
        finally
        {
            btnSend.IsEnabled = true;
            aiMsg.IsWaiting = false;
            _ = ChatStore.Instance.SaveMessageAsync(aiMsg);
            UpdateContextLabel();
            UpdateCacheHitLabel();
            // 记忆提取 + 内心反思（异步，不阻塞发送流程）
            MemoryService.OnExchangeCompleted(Messages);
            _ = InnerLifeService.ReflectAsync(Messages);
            // 每条消息 AI 必看（开关独立，异步不阻塞）
            _ = MessageReviewService.ReviewAsync(text, turnIndex: Messages.Count);
            // 自动压缩检查（异步，不阻塞发送流程）
            _ = CheckAndAutoCompressAsync();
        }
    }

    /// <summary>
    /// Agent 循环执行：把局面交给 AgentLoopService，
    /// 过程中把每一步的缩略行实时刷进气泡（{emoji 动作 +N -N}），
    /// 完成后过程区自动折叠，正文显示重新总结过的最终答复。
    /// </summary>
    private async Task RunAgentLoopAsync(string text, ChatMsg aiBubble)
    {
        var run = new AgentRun();
        aiBubble.AgentRun = run;
        aiBubble.IsWaiting = true;

        // 推理流实时显示：让用户看到 Ta 正在想什么（限流刷新，避免每来一个字就重排界面）
        var thinkingBuf = new StringBuilder();
        var lastFlush = DateTime.Now;
        void OnThinking(string delta)
        {
            thinkingBuf.Append(delta);
            // 每 300ms 刷一次，或缓冲区已攒够一截就刷
            if ((DateTime.Now - lastFlush).TotalMilliseconds < 300 && thinkingBuf.Length < 120) return;
            lastFlush = DateTime.Now;
            var tail = thinkingBuf.ToString();
            // 只保留末尾一小段（流光是对整串文字做逐帧 MeasureText，太长会拖慢渲染）
            if (tail.Length > 48) tail = "…" + tail[^48..];
            tail = tail.Replace('\n', ' ').Replace('\r', ' ').Trim();
            run.SetLiveThought(tail);
            aiBubble.NotifyAgentChanged();
        }
        AgentLoopService.ThinkingDelta += OnThinking;

        // 每完成一步刷新界面；同时保证滚动跟着走
        void OnStep(AgentRun r)
        {
            // 进入新一轮思考时先把上一轮的残留思考文字清掉，避免误导用户
            if (r.Steps.Count > 0 && r.Steps[^1].Kind == "think")
            {
                thinkingBuf.Clear();
                r.SetLiveThought("");
            }
            aiBubble.NotifyAgentChanged();
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        }
        AgentLoopService.StepChanged += OnStep;

        // 删除确认通道直接接到当前页面（AgentActionExecutor 不依赖 UI）
        AgentActionExecutor.DeleteConfirmer = async path =>
        {
            try { return await DisplayAlertAsync("确认删除", $"Ta 想删除文件：\n{path}\n\n确定允许吗？", "删除", "拒绝"); }
            catch { return false; }
        };

        // 生图通道：Agent 模式下 {img:"…"} 真的去调生图接口，并把图显示在这个气泡上。
        // 以前这条路是假的（只记一步就说"已记录"），模型以为画好了、用户却什么都没看到。
        AgentActionExecutor.ImageHandler = desc => GenerateImageForBubbleAsync(desc, aiBubble);

        try
        {
            // 注意：历史里已经包含本轮用户消息（BuildHistoryMessages 会带上），
            // RunAsync 内部会做去重，这里不需要额外处理。
            var history = BuildHistoryMessages(aiBubble);
            var extra = new StringBuilder();
            if (!string.IsNullOrEmpty(_deviceContext)) extra.AppendLine("【你此刻能看到的用户状态】\n" + _deviceContext);
            if (!string.IsNullOrEmpty(_innerContext)) extra.AppendLine(_innerContext);
            if (!string.IsNullOrEmpty(_observedContext)) extra.AppendLine(_observedContext);
            if (!string.IsNullOrEmpty(_reviewContext)) extra.AppendLine(_reviewContext);
            if (!string.IsNullOrEmpty(_memoryContext)) extra.AppendLine(_memoryContext);
            if (!string.IsNullOrEmpty(_diaryContext)) extra.AppendLine(_diaryContext);

            var loopResult = await AgentLoopService.RunAsync(text, history, extra.ToString());

            // 时限到 / 出错：给个交代而不是静默
            if (!string.IsNullOrEmpty(loopResult.Error) && loopResult.Error != "__cancelled__")
                ShowErrorOn(aiBubble, FriendlyNetworkError(new Exception(loopResult.Error)));
            else
            {
                var head = loopResult.TimedOut
                    ? $"（已到 {AppSettings.AgentTimeLimitText} 时限，先做到这里）\n\n"
                    : "";
                aiBubble.Content = head + loopResult.FinalText;
            }

            // 任务收尾：把整轮回环的行数总账附在最后
            if (run.TotalAdded > 0 || run.TotalRemoved > 0)
                aiBubble.Content += "\n\n（" + run.DeltaSummary + "）";
        }
        catch (Exception ex)
        {
            ShowErrorOn(aiBubble, FriendlyNetworkError(ex));
        }
        finally
        {
            AgentLoopService.StepChanged -= OnStep;
            AgentLoopService.ThinkingDelta -= OnThinking;
            AgentActionExecutor.DeleteConfirmer = null;
            AgentActionExecutor.ImageHandler = null;
            run.Finish();
            // 把过程区存成文本 —— 这样重启后还能看到 Ta 当时做了什么
            aiBubble.SavedAgentSummary = run.CollapsedSummary;
            aiBubble.SavedAgentLog = run.FullText;
            aiBubble.NotifyAgentChanged();
            aiBubble.IsWaiting = false;
            msgList.ScrollTo(Messages.Count - 1, position: ScrollToPosition.End, animate: false);
        }
    }

    /// <summary>缓存的长期记忆上下文（每次进入页面/记忆更新后刷新）。</summary>
    private string _memoryContext = "";

    /// <summary>缓存的最近日记摘要上下文。</summary>
    private string _diaryContext = "";

    /// <summary>缓存的设备/生活感知上下文（每次发送前刷新，保证"此刻"准确）。</summary>
    private string _deviceContext = "";

    /// <summary>缓存的内心状态上下文（心情/心里话，用户看不到）。</summary>
    private string _innerContext = "";

    /// <summary>缓存的"她默默观察到的用户近况"（后台静默了解，用户看不到）。</summary>
    private string _observedContext = "";

    /// <summary>缓存的"每条消息 AI 必看的复盘上下文"（用户看不到，仅下一次对话用）。</summary>
    private string _reviewContext = "";

    /// <summary>
    /// 构建完整请求消息列表：system(底层+用户人设+感知+内心+记忆+日记+默默观察) + 全部聊天历史。
    /// 跳过正在生成的 aiBubble 与空内容消息。
    /// </summary>
    private List<object> BuildHistoryMessages(ChatMsg skipBubble)
    {
        var systemPrompt = AppSettings.BuildSystemPrompt();
        if (!string.IsNullOrEmpty(_deviceContext))
            systemPrompt += "\n\n【你此刻能看到的用户状态】\n" + _deviceContext;
        if (!string.IsNullOrEmpty(_innerContext))
            systemPrompt += "\n\n" + _innerContext;
        if (!string.IsNullOrEmpty(_observedContext))
            systemPrompt += "\n\n" + _observedContext;
        if (!string.IsNullOrEmpty(_reviewContext))
            systemPrompt += "\n\n" + _reviewContext;
        if (!string.IsNullOrEmpty(_memoryContext))
            systemPrompt += "\n\n" + _memoryContext;
        if (!string.IsNullOrEmpty(_diaryContext))
            systemPrompt += "\n\n" + _diaryContext;

        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };
        foreach (var m in Messages)
        {
            if (ReferenceEquals(m, skipBubble)) continue;
            if (string.IsNullOrWhiteSpace(m.Content) && string.IsNullOrWhiteSpace(m.TerminalExecLog)
                && string.IsNullOrWhiteSpace(m.VisionResult)) continue;

            if (m.IsUser)
            {
                // 用户消息：正文 + 图片识别文字（如有，图片内容进入历史供后续对话引用）
                var uc = m.Content ?? "";
                if (!string.IsNullOrWhiteSpace(m.VisionResult))
                    uc += "\n\n（图片识别结果）\n" + m.VisionResult;
                messages.Add(new { role = "user", content = uc });
            }
            else
            {
                // AI 消息：正文 + 若有终端执行记录则附带（命令与输出结果），帮助模型保持上下文
                var c = m.Content ?? "";
                if (!string.IsNullOrWhiteSpace(m.TerminalExecLog))
                    c += "\n\n（此前终端执行记录）\n" + m.TerminalExecLog;
                messages.Add(new { role = "assistant", content = c });
            }
        }
        return messages;
    }

    /// <summary>友好的网络错误提示。</summary>
    /// <summary>把异常翻成用户能看懂的话。实现见 <see cref="ApiErrors.Friendly"/>（Agent 那边也用同一套）。</summary>
    private string FriendlyNetworkError(Exception ex) => ApiErrors.Friendly(ex);

    /// <summary>
    /// 出错时把错误**单独放到气泡的错误行**里 —— 正文一个字都不动。
    ///
    /// 历史坑：最早是 `Content = 错误文案`，把用户刚看到的字**瞬间顶掉**；
    /// 后来改成把错误拼在正文后面，那也是"以香盖臭"——错误混在回复里，看着像 Ta 说的话。
    /// 正确做法：正文归正文，错误归错误，各占一块。
    /// </summary>
    private static void ShowErrorOn(ChatMsg bubble, string friendlyError)
    {
        bubble.ErrorText = friendlyError;
    }

    /// <summary>非 2xx 时先捞服务商正文再抛。实现见 <see cref="ApiErrors.EnsureOkAsync"/>。</summary>
    private static Task EnsureOkAsync(HttpResponseMessage resp) => ApiErrors.EnsureOkAsync(resp);

    /// <summary>是否为可重试的网络错误（连接中断/超时）。</summary>
    /// <summary>是否为可重试的网络错误。实现见 <see cref="ApiErrors.IsTransient"/>。</summary>
    private bool IsTransientNetworkError(Exception ex) => ApiErrors.IsTransient(ex);

    /// <summary>流式请求：发送用户输入 → 流式接收 AI 回复。</summary>
    private async Task StreamRequest(string text, ChatMsg aiBubble)
    {
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        var messages = BuildHistoryMessages(aiBubble);
        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = content };
        AttachAuth(req);
                var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        await EnsureOkAsync(resp);

        // 流式处理：实时显示，同时检测指令（{cmd:"..."} / {api:"..."} / {img:"..."} / {browse:"..."}）
        var sseResult = await ConsumeSseAsync(resp, aiBubble);
        if (sseResult.Stalled)
        {
            // 看门狗叫停：服务器长时间没吐数据，别再让用户干等
            if (string.IsNullOrWhiteSpace(aiBubble.Content))
                aiBubble.Content = $"服务器 {EffectiveStallSeconds} 秒没有任何响应，已经断开。\n\n"
                                 + "常见原因：接口地址填错、网络不通、或者服务商那边卡住了。"
                                 + "过一会儿再试；一直这样就先去设置里确认 API URL。";
            _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
            return;
        }
        if (sseResult.CancelledForCommand)
        {
            // 检测到 Shizuku 指令：拉取终端命令列表并执行
            await RunCommandPipeline(text, aiBubble);
        }
        else if (sseResult.CancelledForApi)
        {
            // 检测到 API 指令：调用感知 API 并回传结果
            await RunApiPipeline(text, aiBubble.Content, aiBubble);
        }
        else if (sseResult.CancelledForImage)
        {
            // 检测到文生图指令：真的去调生图接口（失败会续一轮让 Ta 如实告诉用户）
            await GenerateImagePipelineAsync(text, aiBubble.Content, aiBubble);
        }
        else if (sseResult.CancelledForBrowse)
        {
            // 检测到上网指令：调用浏览器插件
            var url = ExtractBrowseCommand(aiBubble.Content);
            if (!string.IsNullOrEmpty(url))
            {
                var browseResult = await BrowserTool.FetchAsync(url);
                // 把网页内容作为额外上下文继续对话
                var continueMessages = new List<object>
                {
                    new { role = "system", content = AppSettings.BuildSystemPrompt() },
                    new { role = "user", content = text },
                    new { role = "assistant", content = aiBubble.Content },
                    new { role = "user", content = $"请根据以下网页内容回答：\n\n{browseResult}" }
                };
                var continueReq = BuildChatBody(model, continueMessages, stream: true);
                var continueJson = JsonSerializer.Serialize(continueReq);
                var continueContent = new StringContent(continueJson, Encoding.UTF8, "application/json");
                var continueHttpReq = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = continueContent };
                AttachAuth(continueHttpReq);
                var continueResp = await _httpClient.SendAsync(continueHttpReq, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
                await EnsureOkAsync(continueResp);
                await ConsumeSseAsync(continueResp, aiBubble, freshRound: true);
            }
        }
        else if (sseResult.CancelledForDownload)
        {
            // 检测到下载指令：真的把文件下到工作区
            await RunDownloadPipelineAsync(text, aiBubble);
        }
        else if (sseResult.CancelledForWeb)
        {
            // 检测到真浏览器指令：交给隐藏 WebView 执行（跑 JS / 点击 / 填表）
            await RunWebPipelineAsync(text, aiBubble);
        }
        else if (sseResult.CancelledForReminder)
        {
            // 检测到承诺提醒指令：解析中文时间 → 落库 → 到点由 CareReceiver 心跳兑现
            await RunReminderPipelineAsync(text, aiBubble);
        }
        // 管线都跑完了，指令已经没用了：从气泡里整段抹掉，别让用户看到 {download:"…"} 这种东西
        aiBubble.Content = InstructionParser.RemoveInstructions(aiBubble.Content);
        _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>提取下载指令（{download:"..."}）。</summary>
    private string ExtractDownloadCommand(string text)
    {
        var start = text.IndexOf("{download:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 11);
        if (end < start) return "";
        return text.Substring(start + 11, end - start - 11).Trim();
    }

    /// <summary>提取真浏览器指令（{web:"动作 参数"}）。</summary>
    private string ExtractWebCommand(string text)
    {
        var start = text.IndexOf("{web:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 6);
        if (end < start) return "";
        return text.Substring(start + 6, end - start - 6).Trim();
    }

    /// <summary>
    /// 真浏览器管线：{web:"动作 参数"} → 藏在聊天页里的隐藏 WebView 执行 → 结果回传。
    /// 这是 HttpClient 做不到的那部分：JS 渲染的页面、点按钮、翻页、填表单、登录。
    /// </summary>
    private async Task RunWebPipelineAsync(string userText, ChatMsg aiBubble)
    {
        var cmd = ExtractWebCommand(aiBubble.Content);
        if (string.IsNullOrEmpty(cmd))
        {
            aiBubble.Content = "（没拿到有效的浏览器指令。）";
            return;
        }

        var result = await WebAgent.RunAsync(cmd);

        // 失败要显式标记出来，别让模型把"打不开"包装成"我查到了"
        bool failed = result.StartsWith("打开失败", StringComparison.Ordinal)
                   || result.StartsWith("打开超时", StringComparison.Ordinal)
                   || result.StartsWith("当前没有正常打开的页面", StringComparison.Ordinal)
                   || result.StartsWith("当前页面没有正常加载", StringComparison.Ordinal)
                   || result.StartsWith("浏览器还没准备好", StringComparison.Ordinal)
                   || result.StartsWith("浏览器操作失败", StringComparison.Ordinal)
                   || result.Contains("**这个页面没有打开成功");

        var hint = "【浏览器执行结果】\n" + result + "\n\n"
                 + (failed
                    ? "注意：这次浏览**失败了**，你没有看到任何真实页面内容。\n"
                    + "必须如实告诉用户「没打开 / 打不开」，并给出可能原因（网址错、域名不存在、网络不通）。\n"
                    + "**严禁编造任何搜索结果、参数、规格、数据** —— 你手上什么资料都没有。\n"
                    + "也不要再说「我翻了遍」「查了一圈」这种话。"
                    : "请用自然的话把结果告诉用户（像你自己看到的一样），不要说'根据网页'。\n"
                    + "只说你实际读到的内容，读不到的部分不要猜。")
                 + "\n不要再输出 {web:...}。";

        var continueMessages = new List<object>
        {
            new { role = "system", content = AppSettings.BuildSystemPrompt() },
            new { role = "user", content = userText },
            new { role = "assistant", content = InstructionParser.RemoveInstructions(aiBubble.Content) },
            new { role = "user", content = hint }
        };
        try
        {
            var body = BuildChatBody(AppSettings.ResolveChatModel(AppSettings.ForceThinking), continueMessages, stream: true);
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = content };
            AttachAuth(req);
                var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            await EnsureOkAsync(resp);
            await ConsumeSseAsync(resp, aiBubble, freshRound: true);
        }
        catch
        {
            aiBubble.Content = result;   // 模型这轮挂了，至少把浏览器结果摆出来
        }
    }

    /// <summary>
    /// 下载管线：{download:"url"} → 真的把文件下到工作区 → 结果回传给模型继续说话。
    ///
    /// 为什么要有这条：Ta 原先只有 {browse:} 一种联网能力，而 browse 只把网页正文读成文字，
    /// 遇到 apk / zip / 图片这类二进制它什么也拿不到 —— 于是只能把网址念给用户听。
    /// 这条管线把"手"接上。
    /// </summary>
    private async Task RunDownloadPipelineAsync(string userText, ChatMsg aiBubble)
    {
        if (!AppSettings.BrowserPermission)
        {
            aiBubble.Content = "（联网能力已关闭，没法下载。需要的话去设置页打开「允许 AI 联网」。）";
            return;
        }

        var url = ExtractDownloadCommand(aiBubble.Content);
        if (string.IsNullOrEmpty(url))
        {
            aiBubble.Content = "（没拿到有效的下载地址。）";
            return;
        }

        // 借终端那套三行状态显示下载进度，别再让用户对着空白等
        aiBubble.HasTerminalHeader = true;
        aiBubble.TerminalTitle = "浏览器 — 下载文件";
        aiBubble.TerminalStep = "正在连接…";
        aiBubble.TerminalOutput = url;

        var prog = new Progress<(long Got, long Total, double Kbps)>(p =>
        {
            try
            {
                var got = BrowserTool.HumanSize(p.Got);
                if (p.Total > 0)
                {
                    var pct = (int)Math.Min(100, p.Got * 100 / p.Total);
                    aiBubble.TerminalStep = $"正在下载… {pct}%";
                    aiBubble.TerminalOutput = $"{got} / {BrowserTool.HumanSize(p.Total)} · {p.Kbps:0} KB/s";
                }
                else
                {
                    aiBubble.TerminalStep = "正在下载…";
                    aiBubble.TerminalOutput = $"{got} · {p.Kbps:0} KB/s";
                }
            }
            catch { }
        });

        var (ok, msg, _) = await BrowserTool.DownloadAsync(url, prog);
        aiBubble.TerminalStep = ok ? "完成" : "失败";
        aiBubble.TerminalOutput = msg;

        var hint = ok
            ? $"【下载结果】{msg}\n\n请用一两句话告诉用户：文件已经下好了、放在哪个路径、下一步怎么打开或安装（apk 的话提示他去文件管理器点一下安装）。不要再输出 {{download:...}}。"
            : $"【下载失败】{msg}\n\n请用一两句话如实告诉用户失败了，猜一下原因（网络不通 / 地址失效 / 没有存储权限 / 文件太大），并给个替代办法。不要再输出 {{download:...}}。";

        var continueMessages = new List<object>
        {
            new { role = "system", content = AppSettings.BuildSystemPrompt() },
            new { role = "user", content = userText },
            new { role = "assistant", content = InstructionParser.RemoveInstructions(aiBubble.Content) },
            new { role = "user", content = hint }
        };
        try
        {
            var body = BuildChatBody(AppSettings.ResolveChatModel(AppSettings.ForceThinking), continueMessages, stream: true);
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = content };
            AttachAuth(req);
                var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            await EnsureOkAsync(resp);
            await ConsumeSseAsync(resp, aiBubble, freshRound: true);
        }
        catch (Exception ex)
        {
            aiBubble.Content = ok ? "文件下好了：" + msg : "下载失败：" + msg;
            _ = ex;
        }
    }

    /// <summary>
    /// 承诺提醒管线：{remind:"时间|内容"} → 解析中文时间 → 落库 → 回传结果让她自然应答。
    ///
    /// 为什么值得单独一条管线：用户说「明天八点叫我」时，她要做的不是"聊一句好听的"，
    /// 而是**真的记下来**。指令是唯一的"记忆动作"，客户端负责把中文时间算成绝对时刻，
    /// 到点由 CareReceiver 的心跳兑现（见 DeliverDueRemindersAsync）。
    ///
    /// 解析失败不硬塞：告诉模型"没听懂时间"，让它换成更明确的说法重来一轮 ——
    /// 猜一个错的时间比不提醒更糟（半夜三点把人吵醒）。
    /// </summary>
    private async Task RunReminderPipelineAsync(string userText, ChatMsg aiBubble)
    {
        var raw = InstructionParser.Extract(aiBubble.Content, "remind").FirstOrDefault() ?? "";
        var (timeText, content) = ReminderParser.SplitValue(raw);

        // 工具气泡：复用三行终端样式，让用户看见"她真的记了"
        aiBubble.HasTerminalHeader = true;
        aiBubble.TerminalTitle = "承诺提醒";
        aiBubble.TerminalStep = "记录提醒";
        aiBubble.TerminalOutput = raw;
        ScrollToBottom();
        FollowBottomIfNeeded();

        if (content.Length == 0)
        {
            aiBubble.TerminalStep = "格式不对";
            aiBubble.TerminalOutput = "没拿到「时间|内容」";
            await ContinueAfterReminderAsync(userText, aiBubble,
                "【提醒失败】指令格式不对，应为 {remind:\"时间|要提醒的事\"}。\n"
                + "请用一句话如实告诉用户这次没记成，然后**重新输出一条格式正确的** {remind:...}。");
            return;
        }

        var due = ReminderParser.Parse(timeText);
        if (!due.HasValue)
        {
            // 没听懂时间：别猜，让她换个说法重来
            aiBubble.TerminalStep = "没听懂时间";
            aiBubble.TerminalOutput = $"「{timeText}」解析不出来";
            await ContinueAfterReminderAsync(userText, aiBubble,
                $"【提醒失败】没听懂时间「{timeText}」。\n"
                + "请把时间换成更明确的说法，**重新输出一条** {remind:\"时间|内容\"}；"
                + "可用说法：明天早上8点 / 今晚9点 / 20分钟后 / 3小时后 / 明天中午12点半 / 下周一上午10点。\n"
                + "如果用户压根没说清时间，就先用一句话问他几点，这轮不要输出指令。");
            return;
        }

        var when = ReminderParser.Describe(due.Value);
        var row = await ChatStore.Instance.AddReminderAsync(content, due.Value);

        if (row == null)
        {
            aiBubble.TerminalStep = "保存失败";
            aiBubble.TerminalOutput = "数据库没写进去";
            await ContinueAfterReminderAsync(userText, aiBubble,
                "【提醒失败】数据库没写进去，这次真的没记住。\n"
                + "请如实告诉用户这次没记住、让他稍后再说一遍，**不要说「我记住了」**。");
            return;
        }

        aiBubble.TerminalStep = "完成";
        aiBubble.TerminalOutput = $"已记下：{content}（{when}）";

        await ContinueAfterReminderAsync(userText, aiBubble,
            $"【提醒已记录】{content} —— 到点时间 {when}（{due.Value:yyyy-MM-dd HH:mm}）。\n"
            + "到点客户端会自动发通知提醒用户，你**不需要**再输出任何指令。\n"
            + "请用一两句话自然地把这件事应下来（顺口提一下具体时间），不要再输出 {remind:...}。");
    }

    /// <summary>
    /// 提醒管线之后的续聊：把结果回传模型，让她说人话（而不是把工具气泡丢在那儿没人收尾）。
    /// </summary>
    private async Task ContinueAfterReminderAsync(string userText, ChatMsg aiBubble, string hint)
    {
        var continueMessages = new List<object>
        {
            new { role = "system", content = AppSettings.BuildSystemPrompt() },
            new { role = "user", content = userText },
            new { role = "assistant", content = InstructionParser.RemoveInstructions(aiBubble.Content) },
            new { role = "user", content = hint }
        };
        try
        {
            var body = BuildChatBody(AppSettings.ResolveChatModel(AppSettings.ForceThinking), continueMessages, stream: true);
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = content };
            AttachAuth(req);
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            await EnsureOkAsync(resp);
            await ConsumeSseAsync(resp, aiBubble, freshRound: true);
        }
        catch
        {
            // 模型这轮挂了：至少把结果摆出来，别让气泡空着
            aiBubble.Content = aiBubble.TerminalOutput;
        }
    }

    /// <summary>
    /// 构建请求体（支持推理开关）。
    ///
    /// 注意：本地模型（Ollama / LM Studio / llama.cpp server）走的是**最小兼容集**：
    /// 只发 `model` / `messages` / `stream`。原因：
    ///  - `reasoning_effort` 不是 OpenAI 标准字段，DeepSeek 认，但 Ollama 会直接 400；
    ///  - `stream_options.include_usage` 只有 OpenAI / DeepSeek 支持，本地服务常报
    ///    "unknown field" 或干脆忽略后不吐 usage；
    ///  - `max_tokens` 各实现默认值不同，本地服务对超长值也容易报错（8B 模型上下文可能只有 4K~8K）。
    /// 所以本地模式一律走最小集，把兼容性风险降到最低。
    /// </summary>
    private object BuildChatBody(string model, List<object> messages, bool stream)
    {
        // 本地模型：最小兼容集，避免非标准字段把服务打 400
        if (AppSettings.UseLocalModel && AppSettings.LocalModelConfigured)
        {
            return new
            {
                model = AppSettings.LocalModelName,
                messages,
                stream,
            };
        }

        var body = new
        {
            model,
            messages,
            stream,
            max_tokens = 4000,
            reasoning_effort = AppSettings.ForceThinking ? "high" : "none",
            stream_options = stream ? new { include_usage = true } : null
        };
        return body;
    }

    /// <summary>
    /// 消费 SSE 流，返回解析结果。
    ///
    /// <paramref name="freshRound"/> = true 时表示这是**同一个气泡的后续轮次**
    /// （工具/浏览器/下载管线的第二轮总结）。此时必须先把上一轮的正文与思考清空，
    /// 否则第二轮会 `Content +=` 接在第一轮后面，打字机也会从整个字符串开头重新揭开，
    /// 结果就是一坨拼在一起的碎片（真机实测过的 bug）。
    /// </summary>
    private async Task<SseConsumeResult> ConsumeSseAsync(HttpResponseMessage resp, ChatMsg aiBubble, bool freshRound = false)
    {
        if (freshRound)
        {
            aiBubble.Content = "";
            aiBubble.Thinking = "";
            aiBubble.ThinkingExpanded = false;
            aiBubble.ErrorText = "";
        }
        var result = new SseConsumeResult();
        var swDiag = System.Diagnostics.Stopwatch.StartNew();   // 诊断：量首块延迟和总时长
        BeginTyping(aiBubble);   // 显示侧打字机：正文由计时器逐字揭开，不再是整块蹦
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // ── 卡死看门狗 ──
        // 服务器不响应时，HttpClient.Timeout 要等好几分钟，用户只能一直盯着转圈。
        // 这里盯着"多久没收到任何数据"，超时就主动断掉并给出明确提示。
        using var stallCts = new CancellationTokenSource();
        long lastDataTicks = DateTime.UtcNow.Ticks;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stallCts.IsCancellationRequested)
                {
                    await Task.Delay(2000, stallCts.Token);
                    var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastDataTicks).TotalSeconds;
                    if (idle >= EffectiveStallSeconds)
                    {
                        result.Stalled = true;
                        stallCts.Cancel();
                        return;
                    }
                }
            }
            catch { }
        });

        string? line;
        while (true)
        {
            try { line = await reader.ReadLineAsync(stallCts.Token); }
            catch (OperationCanceledException) { break; }   // 看门狗叫停
            catch (Exception) when (stallCts.IsCancellationRequested)
            {
                // 看门狗叫停时，底层（Android 网络栈）抛的**可能是 Java 异常**而不是
                // OperationCanceledException —— 那就被这里兜住，别把"我主动断的"
                // 显示成吓人的报错。下面 result.Stalled 会给出人话提示。
                break;
            }
            if (line == null) break;
            lastDataTicks = DateTime.UtcNow.Ticks;
            // SSE 规范允许 "data:" 后带 0 或 1 个空格。原来写死 6 字符（"data: "）
            // 会把只发 "data:{...}" 的实现整条跳过 → 用户看到空回复。
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line.Substring(5).TrimStart();
            if (data.Length == 0) continue;                 // 心跳空行
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta;
                if (delta != null)
                {
                    // 推理内容（独立字段）
                    if (!string.IsNullOrEmpty(delta.reasoning_content))
                    {
                        // 首个思考字：开始计时；此后每个思考字都在刷新"思考结束时刻"
                        if (!result.ThinkingStart.HasValue)
                        {
                            result.ThinkingStart = DateTime.Now;
                            // 思考一开始就展开 —— 让用户看到模型在推理，而不是盯着一个收起的标题行
                            aiBubble.ThinkingExpanded = true;
                        }
                        result.ThinkingEnd = DateTime.Now;
                        aiBubble.Thinking += delta.reasoning_content;
                        if (result.FirstDeltaMs < 0) result.FirstDeltaMs = (int)swDiag.ElapsedMilliseconds;
                        result.Chunks++;
                        aiBubble.IsThinkingActive = true;
                        result.ThinkingActive = true;
                        // 实时刷新用时（只算思考过程，正文不计入）
                        aiBubble.ThinkingSeconds = (result.ThinkingEnd.Value - result.ThinkingStart.Value).TotalSeconds;
                    }
                    // 普通内容
                    if (!string.IsNullOrEmpty(delta.content))
                    {
                        // 正文开始 = 思考结束 → 自动折叠思考区，把版面让给正文
                        //   只在"从无到有"这一刻做一次，避免每块 delta 都触发属性通知
                        if (!result.ContentStarted && aiBubble.HasThinking)
                            aiBubble.ThinkingExpanded = false;

                        aiBubble.Content += delta.content;
                        if (result.FirstDeltaMs < 0) result.FirstDeltaMs = (int)swDiag.ElapsedMilliseconds;
                        result.Chunks++;
                        result.ContentStarted = true;
                        // 正文开始 = 思考过程结束：冻结用时（此后不再累加）
                        aiBubble.IsThinkingActive = false;
                        result.ThinkingFrozen = true;
                        // 检测指令：{cmd:} / {api:} / {img:} / {browse:} / {download:} / {web:}
                        // 指令检测：**只在指令完整（带闭合）时才断流**。
                        // 以前是"单个 delta 里出现 {img: 就断"，而取值要求 {img:"…"} 闭合 ——
                        // SSE 把标记拆到多个 delta 时，断流了却抠不出值，管线只能静默早退
                        // （表现：用户看到一句"画好啦"，却没有图）。
                        var acc = aiBubble.Content;
                        if (acc.IndexOf('{') >= 0)
                        {
                            // 提前断流，交给后续管线处理
                            if (InstructionParser.Extract(acc, "cmd").Count > 0) result.CancelledForCommand = true;
                            else if (InstructionParser.Extract(acc, "api").Count > 0) result.CancelledForApi = true;
                            else if (InstructionParser.Extract(acc, "img").Count > 0) result.CancelledForImage = true;
                            else if (InstructionParser.Extract(acc, "browse").Count > 0) result.CancelledForBrowse = true;
                            else if (InstructionParser.Extract(acc, "download").Count > 0) result.CancelledForDownload = true;
                            else if (InstructionParser.Extract(acc, "web").Count > 0) result.CancelledForWeb = true;
                            else if (InstructionParser.Extract(acc, "remind").Count > 0) result.CancelledForReminder = true;

                            if (result.CancelledForCommand || result.CancelledForApi || result.CancelledForImage
                                || result.CancelledForBrowse || result.CancelledForDownload || result.CancelledForWeb
                                || result.CancelledForReminder)
                                break;
                        }
                    }
                }
                // 处理 usage 统计（缓存命中率 + 上下文用量）
                if (chunk?.usage != null && chunk.usage.Value.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var tu = TokenUsageMapper.FromElement(chunk.usage);
                        if (tu != null)
                        {
                            UsageStats.Current.Apply(tu);
                            _lastCacheHit = tu.CachedTokens;
                            _lastCacheMiss = tu.PromptTokens - tu.CachedTokens;
                            UpdateCacheHitLabel();
                            UpdateContextLabel();
                        }
                    }
                    catch { }
                }
            }
            catch { }
            FollowBottomIfNeeded();
        }
        // 收尾补判：指令可能正好落在流末尾（最后一拍才凑齐闭合），这里再查一次
        if (!result.CancelledForCommand && !result.CancelledForApi && !result.CancelledForImage
            && !result.CancelledForBrowse && !result.CancelledForDownload && !result.CancelledForWeb
            && !result.CancelledForReminder)
        {
            var acc2 = aiBubble.Content;
            if (InstructionParser.Extract(acc2, "cmd").Count > 0) result.CancelledForCommand = true;
            else if (InstructionParser.Extract(acc2, "api").Count > 0) result.CancelledForApi = true;
            else if (InstructionParser.Extract(acc2, "img").Count > 0) result.CancelledForImage = true;
            else if (InstructionParser.Extract(acc2, "browse").Count > 0) result.CancelledForBrowse = true;
            else if (InstructionParser.Extract(acc2, "download").Count > 0) result.CancelledForDownload = true;
            else if (InstructionParser.Extract(acc2, "web").Count > 0) result.CancelledForWeb = true;
            else if (InstructionParser.Extract(acc2, "remind").Count > 0) result.CancelledForReminder = true;
        }

        result.ReadDone = true;
        result.TotalMs = (int)swDiag.ElapsedMilliseconds;
        // 摆出来给人看：首块延迟≈总时长 → 卡在网络/服务端；首块很快但界面不动 → 卡在渲染
        AppSettings.LastStreamDiag =
            (result.Stalled ? $"注意：卡死（{EffectiveStallSeconds} 秒无数据）· " : "")
            + $"首块 {result.FirstDeltaMs}ms · 共 {result.Chunks} 块 · 总 {result.TotalMs}ms"
            + (result.Chunks <= 1 ? "（只收到一块 = 响应被整段缓冲了）" : "");
        aiBubble.IsThinkingActive = false;
        // 用时只算"首个思考字 → 最后一个思考字"；若思考后直接结束（无正文）也在此冻结
        if (result.ThinkingStart.HasValue && result.ThinkingEnd.HasValue)
            aiBubble.ThinkingSeconds = (result.ThinkingEnd.Value - result.ThinkingStart.Value).TotalSeconds;

        // 收尾兜底：只有思考、没有正文时，思考区保持展开（否则用户什么也看不到）；
        //   有正文的话，正文开始时已经自动折叠过了。
        if (aiBubble.HasThinking && string.IsNullOrEmpty(aiBubble.Content))
            aiBubble.ThinkingExpanded = true;

        EndTyping();   // 流收完了：剩下的字一次放出来，别让用户盯着半句话等
        return result;
    }

    /// <summary>提取 Shizuku 指令（{cmd:"..."}）。</summary>
    private List<string> ExtractCommands(string text)
    {
        var cmds = new List<string>();
        var start = text.IndexOf("{cmd:\"");
        while (start >= 0)
        {
            var end = text.IndexOf("\"}", start + 6);
            if (end < start) break;
            var cmd = text.Substring(start + 6, end - start - 6).Trim();
            if (!string.IsNullOrEmpty(cmd)) cmds.Add(cmd);
            start = text.IndexOf("{cmd:\"", end + 2);
        }
        return cmds;
    }

    /// <summary>提取 API 指令（{api:"..."}）。</summary>
    private string ExtractApiCommand(string text)
    {
        var start = text.IndexOf("{api:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 6);
        if (end < start) return "";
        return text.Substring(start + 6, end - start - 6).Trim();
    }

    /// <summary>提取文生图指令（{img:"..."}）。</summary>
    private List<string> ExtractImageCommands(string text)
    {
        var descs = new List<string>();
        var start = text.IndexOf("{img:\"");
        while (start >= 0)
        {
            var end = text.IndexOf("\"}", start + 6);
            if (end < start) break;
            var desc = text.Substring(start + 6, end - start - 6).Trim();
            if (!string.IsNullOrEmpty(desc)) descs.Add(desc);
            start = text.IndexOf("{img:\"", end + 2);
        }
        return descs;
    }

    /// <summary>提取上网指令（{browse:"..."}）。</summary>
    private string ExtractBrowseCommand(string text)
    {
        var start = text.IndexOf("{browse:\"");
        if (start < 0) return "";
        var end = text.IndexOf("\"}", start + 9);
        if (end < start) return "";
        return text.Substring(start + 9, end - start - 9).Trim();
    }

    /// <summary>
    /// Shizuku 终端命令管线：检测到 {cmd:"..."} → 解析命令列表 → 逐条执行 → 三行气泡显示执行过程
    /// → 把结果回传给模型生成最终答案。
    /// </summary>
    private async Task RunCommandPipeline(string prompt, ChatMsg aiBubble)
    {
        // 1) 初始化终端状态
        aiBubble.HasTerminalHeader = true;
        aiBubble.TerminalStep = "与Shizuku通信";
        aiBubble.TerminalOutput = "";
        aiBubble.TerminalTitle = "终端执行";
        ScrollToBottom();
        FollowBottomIfNeeded();

        // 2) 检查 Shizuku 权限
#if ANDROID
        if (!ShizukuRunner.Available())
        {
            aiBubble.TerminalOutput = "Shizuku 未授权";
            return;
        }

        var cmds = ExtractCommands(aiBubble.Content);
        if (cmds.Count == 0)
        {
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = "未找到有效命令";
            return;
        }

        // 3) 逐条执行命令
        var outputs = new List<(string cmd, string result)>();
        foreach (var cmd in cmds)
        {
            aiBubble.TerminalStep = "拉取终端";
            aiBubble.TerminalOutput = "";
            ScrollToBottom();
            FollowBottomIfNeeded();

            var result = await ShizukuRunner.ExecuteAsync(cmd);
            var combined = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(result.Stdout)) combined.Append(result.Stdout);
            if (!string.IsNullOrWhiteSpace(result.Stderr)) combined.AppendLine("[stderr] " + result.Stderr);
            if (result.ExitCode != 0) combined.Append($"(exit {result.ExitCode})");
            outputs.Add((cmd, combined.ToString()));

            // 先把结果写入第三行，但保持"运行终端"状态一段时间，
            // 让浅绿扫光动画可被看到（验收），再切到"完成"。
            aiBubble.TerminalOutput = NormalizeSingleLine(combined.ToString());
            aiBubble.TerminalStep = "运行终端";
            //希望你能看到这一行
            FollowBottomIfNeeded();
            await Task.Delay(1500);
            aiBubble.TerminalStep = "完成";
            FollowBottomIfNeeded();
        }

        if (outputs.Count == 0)
        {
            aiBubble.TerminalStep = "完成";
            if (string.IsNullOrEmpty(aiBubble.TerminalOutput)) aiBubble.TerminalOutput = "已取消或无授权";
            return;
        }

        // 有输出则状态保持完成
        aiBubble.TerminalStep = "完成";

        // 保存终端执行记录（命令+输出），供后续作为上下文发送给模型
        var execLog = new StringBuilder();
        foreach (var o in outputs)
            execLog.AppendLine("$ " + o.cmd + "\n" + o.result + "\n");
        aiBubble.TerminalExecLog = execLog.ToString();

        // 第二轮：把命令执行结果回传给模型，生成结合用户问题的最终答案
        var toolResults = new StringBuilder("[Shizuku 终端命令]\n");
        foreach (var o in outputs)
            toolResults.AppendLine($"$ {o.cmd}\n{o.result}");
        await StreamSummaryReply(prompt, toolResults.ToString(), aiBubble);
#endif
    }

    /// <summary>
    /// 感知 API 调用流水线（{api:"名"}）：三行气泡实时显示正在调用哪个 API
    /// （如 调用「屏幕使用时间」API），结果回传给模型生成最终回答，
    /// 并存入 TerminalExecLog 作为后续上下文。
    /// </summary>
    private async Task RunApiPipeline(string prompt, string rawText, ChatMsg aiBubble)
    {
        var apiCmd = ExtractApiCommand(rawText);
        if (string.IsNullOrEmpty(apiCmd)) return;

        // 1) 初始化终端状态
        aiBubble.HasTerminalHeader = true;
        aiBubble.TerminalStep = "调用API";
        aiBubble.TerminalOutput = "";
        aiBubble.TerminalTitle = $"调用「{apiCmd}」API";
        ScrollToBottom();
        FollowBottomIfNeeded();

        try
        {
            // 2) 调用感知 API（DeviceContextService 统一入口）
            var apiResult = await ApiRegistry.ExecuteAsync(apiCmd);
            if (string.IsNullOrEmpty(apiResult))
            {
                aiBubble.TerminalOutput = "API调用失败：无返回结果";
                return;
            }

            // 3) 显示结果
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = apiResult;
            aiBubble.TerminalExecLog = $"[{apiCmd} API]\n{apiResult}";
            ScrollToBottom();
            FollowBottomIfNeeded();

            // 4) 把 API 结果回传给模型，生成最终回答
            await StreamSummaryReply(prompt, $"[{apiCmd} API]\n{apiResult}", aiBubble);
        }
        catch (Exception ex)
        {
            aiBubble.TerminalStep = "完成";
            aiBubble.TerminalOutput = $"API调用失败：{ex.Message}";
            aiBubble.TerminalExecLog = $"[{apiCmd} API]\n错误：{ex.Message}";
        }
    }

    /// <summary>流式总结回复：把工具结果（终端执行/API调用/图片识别）交给模型生成最终答案。</summary>
    private async Task StreamSummaryReply(string originalPrompt, string toolResults, ChatMsg aiBubble)
    {
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        var summarySystem = AppSettings.BuildSystemPrompt();
        if (!string.IsNullOrEmpty(_memoryContext))
            summarySystem += "\n\n" + _memoryContext;

        var messages = new List<object>
        {
            new { role = "system", content = summarySystem },
            new { role = "user", content = originalPrompt },
            // 注意：工具结果必须当**用户侧**消息回传，**绝不能当 assistant**。
            // 当 assistant 的话，模型会把它当成"我自己上一条说过的话"，
            // 下次就照着这个格式**自己编结果**（伪造 FOREGROUND / BATTERY / SCREEN_TIME 那种），
            // 而且不再真的去输出 {api:"…"} 指令 —— 表现就是"瞎调用API、黑气泡还不出来"。
            new { role = "user", content =
                "【客户端回传的真实数据】\n" + toolResults +
                "\n\n（提醒：上面这段是**客户端**给你的数据，不是你说过的话，**不要模仿它的格式自己写**。" +
                "里面没有的信息就老实说看不到，不许猜、不许编。）\n\n" +
                "请根据上面的数据回答用户刚才的问题。" }
        };

        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = content };
        AttachAuth(req);
                var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        await EnsureOkAsync(resp);

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta?.content;
                if (string.IsNullOrEmpty(delta)) continue;
                aiBubble.Content += delta;
                FollowBottomIfNeeded();
            }
            catch { }
        }
        _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>单行化终端输出（去除换行，避免气泡溢出）。</summary>
    private string NormalizeSingleLine(string text)
    {
        return text.Replace("\n", " ").Replace("\r", " ").Trim();
    }

    /// <summary>压缩时保留的最近消息条数（只有更早的那批会被总结掉）。</summary>
    private const int CompressKeepRecent = 10;

    /// <summary>
    /// 送去总结的早期对话正文上限（字符）。超长时只保留**靠近最近**的那一段，
    /// 避免"为了省上下文反而先撑爆上下文"这种蠢事。
    /// </summary>
    private const int CompressTranscriptMaxChars = 40000;

    /// <summary>自动压缩：当上下文占用超过阈值（百分比）时，询问用户是否压缩早期消息。</summary>
    private async Task CheckAndAutoCompressAsync()
    {
        int threshold = AppSettings.AutoCompressThreshold;
        if (threshold <= 0) return;   // 0 表示禁用

        var allTokens = Messages.Sum(m => EstimateTokens(m.Content));
        int cap = AppSettings.EffectiveMaxTokens;   // 模型上下文窗口
        if (cap <= 0) cap = AppSettings.ContextFallbackLimit;
        double pct = (double)allTokens / cap * 100.0;
        if (pct < threshold) return;  // 占用未达阈值百分比，不弹窗

        // 弹窗文案得说实话：压缩是**真调一次模型**去总结，会多花一次请求、要等几秒，
        // 不能再像以前那样让用户以为只是"整理一下本地文本"。
        var alert = await DisplayAlertAsync("上下文过长",
            $"当前对话已用 {allTokens} tokens（占模型上下文 {pct:F0}%）。\n\n" +
            $"压缩会把早期对话交给模型总结成一段摘要，然后丢弃早期原文，" +
            $"只保留摘要 + 最近 {CompressKeepRecent} 条消息。是否压缩？",
            "压缩", "取消");
        if (!alert) return;

        await CompressEarlyMessagesAsync();
    }

    /// <summary>
    /// 压缩早期消息：把早期对话**真的交给模型**总结成一段短摘要，然后丢弃原文，
    /// 只保留摘要 + 最近 <see cref="CompressKeepRecent"/> 条。
    ///
    /// 旧实现是"假压缩"：把早期消息原样拼成 "用户: … / AI: …" 塞进一条消息里，
    /// 一个字都没省，反而多了一堆前缀，纯属自欺欺人。现在改成真总结。
    ///
    /// 任何一步失败（网络/超时/模型返回空）都**静默降级**：原消息一条不动，绝不丢用户数据。
    /// </summary>
    private async Task CompressEarlyMessagesAsync()
    {
        await _compressLock.WaitAsync();
        try
        {
            int compressCount = Messages.Count - CompressKeepRecent;
            if (compressCount <= 0) return;   // 消息太少，没什么可压的

            // ① 先按引用快照要压的那批消息。
            //    后面 await 期间用户可能又发了新消息，用引用摘除才不会误伤。
            var early = new List<ChatMsg>(compressCount);
            for (int i = 0; i < compressCount; i++) early.Add(Messages[i]);

            // ② 拼出给总结模型看的原料
            string transcript = BuildCompressTranscript(early);
            if (string.IsNullOrWhiteSpace(transcript)) return;

            // ③ 真调模型总结。返回 null = 失败，直接放弃，原消息原封不动
            string? summary = await SummarizeEarlyMessagesAsync(transcript);
            if (string.IsNullOrWhiteSpace(summary)) return;

            var compressMsg = new ChatMsg
            {
                Content = summary.Trim(),
                IsUser = false,
                IsCompressed = true   // 沿用原有"压缩摘要"标记，界面仍显示成一条气泡
            };

            // ④ 先把摘要落库（拿到 Id）。存不进去就整个放弃 —— 不能出现"原文没了、摘要也没留下"
            try { await ChatStore.Instance.SaveMessageAsync(compressMsg); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Compress] 摘要落库失败，放弃本次压缩: {ex.Message}");
                return;
            }

            // ⑤ 再把被压缩的旧消息从数据库删掉。
            //    旧写法只存摘要不删原文，重启后旧消息全回来了 —— 压缩白做。
            var oldIds = early.Where(m => m.Id > 0).Select(m => m.Id).ToList();
            if (oldIds.Count > 0)
            {
                try { await ChatStore.Instance.DeleteMessagesAsync(oldIds); }
                catch (Exception ex)
                {
                    // 删失败不丢数据，只是重启后旧消息会回来；记日志便于排查
                    System.Diagnostics.Debug.WriteLine($"[Compress] 删旧消息失败: {ex.Message}");
                }
            }

            // ⑥ 最后才动内存：摘掉早期消息，摘要插到最前面
            foreach (var m in early) Messages.Remove(m);
            Messages.Insert(0, compressMsg);

            ScrollToBottom();
            try { UpdateContextLabel(); } catch { }   // 占用百分比变了，刷新顶部显示
        }
        finally
        {
            _compressLock.Release();
        }
    }

    /// <summary>
    /// 把早期消息拼成给总结模型看的正文。
    /// 空内容的消息直接跳过（占位气泡、还在生成的气泡都没意义）；
    /// 之前压缩留下的摘要会单独标出来，让模型知道这段本身就是二手摘要。
    /// </summary>
    private static string BuildCompressTranscript(List<ChatMsg> early)
    {
        var sb = new StringBuilder();
        foreach (var m in early)
        {
            string text = (m.Content ?? "").Trim();
            if (text.Length == 0) continue;
            string who = m.IsCompressed ? "【更早的摘要】" : (m.IsUser ? "用户" : "青阳");
            sb.Append(who).Append('：').AppendLine(text);
        }

        string transcript = sb.ToString().Trim();
        if (transcript.Length > CompressTranscriptMaxChars)
        {
            // 太长：只留靠近最近的这一段（近的对后续对话更有用），并明确告知前面省略了
            transcript = "（更早的部分太长已省略）\n" +
                         transcript.Substring(transcript.Length - CompressTranscriptMaxChars);
        }
        return transcript;
    }

    /// <summary>
    /// 调对话模型把早期对话总结成 300 字以内的摘要（**非流式**，60 秒超时）。
    /// 任何失败都返回 null，由调用方静默降级 —— 压缩是锦上添花，不能因为总结失败把历史弄丢。
    /// </summary>
    private async Task<string?> SummarizeEarlyMessagesAsync(string transcript)
    {
        try
        {
            string model = AppSettings.ResolveChatModel(false);   // 摘要不用思考模型

            var messages = new List<object>
            {
                new { role = "system", content =
                    "你是对话归档助手。请把给你的早期聊天记录压缩成一段摘要，供后续对话继续使用。" +
                    "必须保留：用户的偏好与习惯、双方达成的约定、发生的重要事件、还没做完的事。" +
                    "寒暄、客套、重复的确认一律丢掉。" +
                    "只输出摘要正文，不要标题、不要分点符号、不要解释你在做什么。" },
                new { role = "user", content =
                    "以下是需要归档的早期聊天记录：\n\n" + transcript +
                    "\n\n请总结成 300 字以内的中文摘要。" }
            };

            // 摘要请求刻意精简：
            //   不带 stream_options（非流式时传 null 会把部分本地服务打 400），
            //   也不开推理（reasoning_effort），省时间省钱。
            object reqBody;
            if (AppSettings.UseLocalModel && AppSettings.LocalModelConfigured)
            {
                // 本地模型：最小兼容集，避免非标准字段被打 400
                reqBody = new { model = AppSettings.LocalModelName, messages, stream = false, max_tokens = 800 };
            }
            else
            {
                reqBody = new { model, messages, stream = false, max_tokens = 800, reasoning_effort = "none" };
            }

            string json = JsonSerializer.Serialize(reqBody);
            using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            AttachAuth(req);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var resp = await _httpClient.SendAsync(req, cts.Token);
            await EnsureOkAsync(resp);

            string body = await resp.Content.ReadAsStringAsync();
            string? content = ExtractNonStreamContent(body);
            if (string.IsNullOrWhiteSpace(content)) return null;
            return content.Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Compress] 总结失败，保留原消息: {ex.Message}");
            return null;
        }
    }

    /// <summary>从非流式响应里取出 choices[0].message.content（取不到返回 null）。</summary>
    private static string? ExtractNonStreamContent(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("choices", out var choices)) return null;
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return null;
            if (!choices[0].TryGetProperty("message", out var msg)) return null;
            if (!msg.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) return null;
            return c.GetString();
        }
        catch { return null; }
    }

    /// <summary>估算消息的 token 数（粗略估算）。</summary>
    private int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int chars = text.Length;
        // 中文 1 字≈1.1 token，其它 4 字符≈1 token
        double tokens = 0;
        tokens += chars * (1.0 / 3.0) * 1.1;       // 中文字符占比 1/3
        tokens += chars * (2.0 / 3.0) * 0.25;      // 其余字符占比 2/3
        return (int)Math.Round(tokens) + 32;       // 基础头信息附加
    }

    /// <summary>语音识别：优先 Android 系统 SpeechRecognizer，失败回退听觉模型 API。</summary>
    private async Task<string?> RecognizeVoiceAsync()
    {
#if ANDROID
        // 1) 系统识别
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            var recognizer = Android.Speech.SpeechRecognizer.CreateSpeechRecognizer(
                Microsoft.Maui.ApplicationModel.Platform.AppContext);
            if (recognizer == null) throw new Exception("系统不支持语音识别");
            recognizer.SetRecognitionListener(new SystemRecognizerListener(tcs));
            recognizer.StartListening(new Android.Content.Intent(Android.Speech.RecognizerIntent.ActionRecognizeSpeech)
                .PutExtra(Android.Speech.RecognizerIntent.ExtraLanguage, "zh-CN"));
            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            recognizer.Destroy();
            if (!string.IsNullOrWhiteSpace(result)) return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Voice] 系统识别失败: {ex.Message}");
        }
#endif

        // 2) 听觉模型 API 回退：录音（弹窗期间录音，最长 15 秒）→ multipart 上传转写
#if ANDROID
        if (AppSettings.AudioEnabled)
            return await RecordAndTranscribeAsync();
#endif

        throw new Exception("语音识别不可用：未配置听觉模型，且系统识别失败。");
    }

#if ANDROID
    /// <summary>录音并上传听觉模型转写。弹窗期间录音，点「完成」提前结束，最长 15 秒。</summary>
    private async Task<string?> RecordAndTranscribeAsync()
    {
        var status = await Permissions.RequestAsync<Permissions.Microphone>();
        if (status != PermissionStatus.Granted)
            throw new Exception("没有麦克风权限，无法录音");

        var path = Path.Combine(FileSystem.CacheDirectory ?? ".", $"voice_{Guid.NewGuid():N}.m4a");
        var recorder = new Android.Media.MediaRecorder();
        bool started = false;
        try
        {
            recorder.SetAudioSource(Android.Media.AudioSource.Mic);
            recorder.SetOutputFormat(Android.Media.OutputFormat.Mpeg4);
            recorder.SetAudioEncoder(Android.Media.AudioEncoder.Aac);
            recorder.SetAudioEncodingBitRate(96000);
            recorder.SetAudioSamplingRate(44100);
            recorder.SetOutputFile(path);
            recorder.Prepare();
            recorder.Start();
            started = true;
        }
        catch (Exception ex)
        {
            recorder.Release();
            throw new Exception("录音启动失败：" + ex.Message);
        }

        try
        {
            // 录音提示：说完点「完成」提前结束；最长 15 秒自动结束
            var alert = DisplayAlertAsync("正在听你说…", "说完后点击「完成」结束录音。", "完成");
            await Task.WhenAny(alert, Task.Delay(15000));
        }
        finally
        {
            if (started) { try { recorder.Stop(); } catch { } }
            recorder.Release();
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.Length < 1024)
            throw new Exception("没有录到声音");

        return await TranscribeWithAudioModelAsync(path);
    }

    /// <summary>调用听觉模型 /audio/transcriptions 转写音频文件（OpenAI 兼容 multipart）。</summary>
    private async Task<string?> TranscribeWithAudioModelAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.AudioApiUrl);
        req.Headers.Add("Authorization", $"Bearer {AppSettings.AudioApiKey}");

        var fileContent = new StreamContent(File.OpenRead(path));
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mp4");
        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", Path.GetFileName(path));
        form.Add(new StringContent(
            string.IsNullOrWhiteSpace(AppSettings.AudioModel) ? "whisper-1" : AppSettings.AudioModel), "model");
        req.Content = form;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var resp = await _httpClient.SendAsync(req, cts.Token);
        await EnsureOkAsync(resp);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
    }
#endif

    /// <summary>点击图片按钮：相册选图 → 视觉模型识别 → 文本对话模型回答。</summary>
    private async void OnImageClicked(object? sender, EventArgs e)
    {
        ChatMsg? userMsg = null;
        ChatMsg? aiMsg = null;
        try
        {
            var file = await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "选择图片" });
            if (file == null) return;

            var path = file.FullPath;
            // 用户图片消息（识别结果稍后补进同一条）
            userMsg = new ChatMsg { IsUser = true, ImageUrl = path, Content = "（图片）" };
            Messages.Add(userMsg);
            ScrollToBottom();

            aiMsg = new ChatMsg { Content = "", IsUser = false, IsWaiting = true };
            Messages.Add(aiMsg);
            ScrollToBottom();

            // 注意：立刻落库并回填 Id。
            // 旧写法要等视觉识别完（可能几十秒）才第一次保存 —— 中途被切后台掐断，
            // 就会留下两条 Id=0 的孤儿消息，之后数据库同步还会把它们当「新消息」再插一遍，越滚越多。
            await ChatStore.Instance.SaveMessageAsync(userMsg);
            await ChatStore.Instance.SaveMessageAsync(aiMsg);

            // 视觉模型识别（瞬时网络错误自动重试一次，对齐文本对话的行为）
            string visionResult;
            if (AppSettings.VisionEnabled)
            {
                try
                {
                    visionResult = await AnalyzeImageAsync(path);
                }
                catch (Exception ex) when (IsTransientNetworkError(ex))
                {
                    // 切后台被系统冻结、服务端掐线 → 重试一次
                    visionResult = await AnalyzeImageAsync(path);
                }
            }
            else
            {
                visionResult = "（未配置视觉模型，无法识别图片内容）";
            }

            // 识别结果存入图片消息历史（后续对话 AI 也能记住图片内容）
            userMsg.VisionResult = visionResult;
            _ = ChatStore.Instance.SaveMessageAsync(userMsg);

            // 把识别结果交给文本对话模型回答
            try
            {
                await StreamVisionReply("请描述这张图片的内容。", visionResult, path, aiMsg);
            }
            catch (Exception ex) when (IsTransientNetworkError(ex))
            {
                aiMsg.Content = "";
                aiMsg.ErrorText = "";   // 重试就把上次的错误行清掉
                await StreamVisionReply("请描述这张图片的内容。", visionResult, path, aiMsg);
            }
            aiMsg.IsWaiting = false;
        }
        catch (Exception ex)
        {
            // 失败也要落库：把错误写进气泡，避免留下一条永远「加载中」的孤儿
            if (aiMsg is not null)
            {
                aiMsg.IsWaiting = false;
                aiMsg.ErrorText = "图片发送失败：" + ex.Message;
                _ = ChatStore.Instance.SaveMessageAsync(aiMsg);
            }

            // 诊断提示：显示视觉模型 URL 与图片体积，便于区分"连不上/格式错/体积过大"
            string hint = AppSettings.VisionEnabled ? $"\n\n视觉模型：{AppSettings.VisionApiUrl}" : "\n\n（未配置视觉模型）";
            hint += $"\n图片压缩后约 {_lastVisionBytes / 1024} KB（base64 约 {_lastVisionB64 / 1024} KB）";
            await DisplayAlertAsync("图片发送失败", ex.Message + hint, "确定");
        }
    }

    private long _lastVisionBytes;
    private long _lastVisionB64;

    /// <summary>调用视觉模型识别图片，返回描述文本（先压缩避免超限）。请求体严格对齐 SenseNova 官方示例。</summary>
    private async Task<string> AnalyzeImageAsync(string path)
    {
        byte[] bytes = await CompressImageAsync(path);
        _lastVisionBytes = bytes.Length;
        string b64 = Convert.ToBase64String(bytes);
        _lastVisionB64 = b64.Length;
        string mime = "image/jpeg"; // 统一压缩为 JPEG

        var messages = new object[]
        {
            new { content = "你是一个说话客观公正的小助手", role = "system" },
            new { role = "user", content = new object[]
            {
                new { type = "text", text = "请描述这张图片的内容，尽量详细。" },
                new { type = "image_url", image_url = new { url = $"data:{mime};base64,{b64}" } }
            } }
        };
        var reqBody = new
        {
            model = string.IsNullOrWhiteSpace(AppSettings.VisionModel) ? "sensenova-6.8-flash-lite" : AppSettings.VisionModel,
            messages,
            n = 1,
            stream = true,           // 用 SSE 流式：边生成边推，连接持续活跃，避免长连接被掐断
            max_tokens = 1000,
            reasoning_effort = "none"
        };
        string reqJson = JsonSerializer.Serialize(reqBody);

        // 429 限流自动重试（退避重试，最多 3 次），偶发限流能自愈
        // 注意：HttpRequestMessage 只能发送一次，每次重试必须新建请求对象
        const int maxRetries = 3;
        HttpResponseMessage resp = null!;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.VisionApiUrl);
            req.Headers.Add("Authorization", $"Bearer {AppSettings.VisionApiKey}");
            req.Content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                resp = await _httpClient.SendAsync(req, cts.Token);
                if ((int)resp.StatusCode != 429) break; // 非限流，直接返回
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                resp?.Dispose();
                await Task.Delay(1000 * (attempt + 1)); // 退避：1s, 2s, 3s
                continue;
            }
            resp?.Dispose();
            await Task.Delay(2000 * (attempt + 1)); // 429 退避：2s, 4s, 6s
        }
        await EnsureOkAsync(resp);

        // SSE 流式解析：累积所有 delta.content
        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta?.content;
                if (!string.IsNullOrEmpty(delta)) sb.Append(delta);
            }
            catch { }
        }
        string result = sb.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            // 兜底：有些实现把结果放 choices[0].message.content，尝试整段解析
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("choices", out var ch) && ch.GetArrayLength() > 0
                && ch[0].TryGetProperty("message", out var msg))
            {
                result = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
            }
        }
        return result;
    }

    /// <summary>压缩图片到合适尺寸（最长边 512px、JPEG 质量 60），尽可能缩小 base64 体积，避免上传被服务端断开。</summary>
    private async Task<byte[]> CompressImageAsync(string path)
    {
#if ANDROID
        var opts = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
        Android.Graphics.BitmapFactory.DecodeFile(path, opts);
        int srcW = opts.OutWidth, srcH = opts.OutHeight;
        if (srcW <= 0 || srcH <= 0)
            return await File.ReadAllBytesAsync(path);

        const int maxEdge = 512;
        int sample = 1;
        while (Math.Max(srcW / sample, srcH / sample) > maxEdge * 2) sample *= 2;

        var decodeOpts = new Android.Graphics.BitmapFactory.Options { InSampleSize = sample };
        using var src = Android.Graphics.BitmapFactory.DecodeFile(path, decodeOpts);
        if (src == null)
            return await File.ReadAllBytesAsync(path);

        int scale = 1;
        while (Math.Max(src.Width / scale, src.Height / scale) > maxEdge) scale *= 2;
        using var scaled = scale > 1
            ? Android.Graphics.Bitmap.CreateScaledBitmap(src, src.Width / scale, src.Height / scale, true)
            : Android.Graphics.Bitmap.CreateBitmap(src);

        using var ms = new MemoryStream();
        scaled.Compress(Android.Graphics.Bitmap.CompressFormat.Jpeg, 60, ms);
        return ms.ToArray();
#else
        return await File.ReadAllBytesAsync(path);
#endif
    }

    /// <summary>把视觉识别结果回传给文本对话模型，流式生成回答。</summary>
    private async Task StreamVisionReply(string prompt, string visionResult, string imagePath, ChatMsg aiBubble)
    {
        string model = AppSettings.ResolveChatModel(AppSettings.ForceThinking);

        var visionSystem = AppSettings.BuildSystemPrompt();
        if (!string.IsNullOrEmpty(_memoryContext))
            visionSystem += "\n\n" + _memoryContext;

        var messages = new List<object>
        {
            new { role = "system", content = visionSystem }
        };
        // 把图片路径与视觉识别结果作为临时上下文（不持久化到 Messages，避免历史文件保存 base64）
        messages.Add(new { role = "user", content = prompt });
        // 注意：识别结果必须当**用户侧**回传，不能当 assistant ——
        // 当 assistant 模型会以为是自己说的，进而照着编（同 StreamSummaryReply 那个坑）
        messages.Add(new { role = "user", content = "【客户端回传的视觉识别结果】\n" + visionResult });
        messages.Add(new { role = "user", content =
            "请根据上面的视觉识别结果回答用户的问题，用纯普通文本，禁止 Markdown，直接给结论。" +
            "结果里没有的东西不要编。" });

        var reqBody = BuildChatBody(model, messages, stream: true);
        var json = JsonSerializer.Serialize(reqBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = content };
        AttachAuth(req);
                var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
        await EnsureOkAsync(resp);

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line.Substring(6);
            if (data == "[DONE]") break;
            try
            {
                var chunk = JsonSerializer.Deserialize<StreamChunk>(data);
                var delta = chunk?.choices?[0]?.delta?.content;
                if (string.IsNullOrEmpty(delta)) continue;
                aiBubble.Content += delta;
                FollowBottomIfNeeded();
            }
            catch { }
        }
        _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
        await SpeakIfEnabledAsync(aiBubble.Content);
    }

    /// <summary>点击思考过程折叠头部：展开/收起。</summary>
    private void OnThinkingHeaderTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is ChatMsg msg)
            msg.ThinkingExpanded = !msg.ThinkingExpanded;
    }

    /// <summary>点击 Agent 过程区头部：展开/收起（折叠后仍可点开查看明细）。</summary>
    private void OnAgentHeaderTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo) return;
        if (bo.BindingContext is not ChatMsg msg) return;

        if (msg.AgentRun != null)
        {
            msg.AgentRun.Collapsed = !msg.AgentRun.Collapsed;
        }
        else
        {
            // 从库里恢复的过程区（没有运行时对象）：直接翻转存下来的展开状态
            msg.ToggleSavedAgentExpanded();
        }
        msg.NotifyAgentChanged();
    }

    // ─────────── 权限气泡（盾牌） ───────────

    // ─────────── 三条杠功能菜单 ───────────

    /// <summary>当前展开的功能分组："" / "model" / "reason" / "perm" / "image"。</summary>
    private string _menuPane = "";

    /// <summary>点 ≡：展开/收起功能菜单。</summary>
    private void OnMenuTapped(object? sender, EventArgs e)
    {
        if (menuLayer.IsVisible) { CloseMenu(); return; }
        OpenMenu("model");   // 默认先展开「选择模型」这一组
    }

    /// <summary>点空白遮罩：收起菜单。</summary>
    private void OnMenuBackdropTapped(object? sender, EventArgs e) => CloseMenu();

    private void CloseMenu()
    {
        menuLayer.IsVisible = false;
        menuRoot.IsVisible = false;
    }

    /// <summary>展开菜单并定位到某个分组。</summary>
    private void OpenMenu(string pane)
    {
        _menuPane = pane;
        menuLayer.IsVisible = true;
        menuRoot.IsVisible = true;
        RefreshMenu();
        ApplyPanelInsets(Height);
    }

    /// <summary>按当前分组刷新菜单内容（标题、各次气泡的提示字、主气泡里显示哪个面板）。</summary>
    private void RefreshMenu()
    {
        try
        {
            // 各次气泡的副标题（显示当前值，省得点进去才知道）
            lblSubModelHint.Text = ShortModelName(AppSettings.Model);
            lblSubPermHint.Text = AppSettings.FileAccessDesc;

            // 推理次气泡：只有模型返回了推理档位、或手动开了强制思考，才显示
            var reasonOptions = AppSettings.GetReasoningOptions();
            bool showReason = AppSettings.ReasoningSupported || AppSettings.ForceThinking;
            subReason.IsVisible = showReason;
            if (showReason)
            {
                var choice = AppSettings.ReasoningChoice;
                lblSubReasonHint.Text = string.IsNullOrEmpty(choice) ? "关" : choice;
            }

            // 分组面板互斥显示
            paneModel.IsVisible = _menuPane == "model";
            paneReason.IsVisible = _menuPane == "reason";
            panePerm.IsVisible = _menuPane == "perm";
            paneImage.IsVisible = _menuPane == "image";

            lblMenuTitle.Text = _menuPane switch
            {
                "reason" => "推理强度",
                "perm" => "文件访问权限",
                "image" => "发送图片",
                _ => "选择模型",
            };

            if (_menuPane == "model") BuildModelPane();
            else if (_menuPane == "reason") BuildReasonPane();
            else if (_menuPane == "perm") RefreshPermPanel();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RefreshMenu] {ex.Message}"); }
    }

    /// <summary>把模型 id 缩成适合小气泡副标题的短名（去掉括号里的 token 后缀）。</summary>
    private static string ShortModelName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "未选择";
        var p = id.LastIndexOf(" (", StringComparison.Ordinal);
        var name = p > 0 && id.EndsWith(")", StringComparison.Ordinal) ? id[..p] : id;
        return name.Length > 16 ? name[..15] + "…" : name;
    }

    /// <summary>构建「选择模型」面板：API 模型一组 + 本地模型一组（没有启用的本地模型就不显示那组）。</summary>
    private void BuildModelPane()
    {
        apiModelList.Children.Clear();
        localModelList.Children.Clear();

        var models = AppSettings.GetCachedModelList(AppSettings.ModelCat.Chat);
        var current = AppSettings.Model;

        if (models.Count == 0)
        {
            lblModelEmpty.IsVisible = true;
        }
        else
        {
            lblModelEmpty.IsVisible = false;
            foreach (var m in models)
            {
                var isCur = !AppSettings.UseLocalModel &&
                            string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase);
                apiModelList.Children.Add(BuildModelRow(
                    AppSettings.ModelLabel(m.Id, m.Max), isCur, $"API|{m.Id}"));
            }
        }

        // 本地模型分组：列出「已部署」的模型 + 当前配置的本地模型。
        // 未启用（一个都没有）就整组隐藏 —— 对应需求「未启用的就不显示」。
        var localNames = new List<string>();
        foreach (var key in LocalModelDownloader.Deployed.Keys)
        {
            var n = LocalModelDownloader.FileNameToModelName(key);
            if (!string.IsNullOrWhiteSpace(n) && !localNames.Contains(n, StringComparer.OrdinalIgnoreCase))
                localNames.Add(n);
        }
        var cfgLocal = AppSettings.LocalModelName;
        if (!string.IsNullOrWhiteSpace(cfgLocal) && !localNames.Contains(cfgLocal, StringComparer.OrdinalIgnoreCase))
            localNames.Add(cfgLocal);

        if (localNames.Count > 0)
        {
            localModelGroup.IsVisible = true;
            foreach (var name in localNames)
            {
                var isCur = AppSettings.UseLocalModel &&
                            string.Equals(name, cfgLocal, StringComparison.OrdinalIgnoreCase);
                localModelList.Children.Add(BuildModelRow(name, isCur, $"LOCAL|{name}"));
            }
        }
        else
        {
            localModelGroup.IsVisible = false;
        }
    }


    /// <summary>菜单里的一行模型（点一下切换成该模型）。</summary>
    private View BuildModelRow(string label, bool selected, string tag)
    {
        var text = new Label
        {
            Text = (selected ? "● " : "○ ") + label,
            TextColor = selected ? AppSettings.ThemeColor : Color.FromArgb("#D5C2C1"),
            FontSize = 12.5,
        };
        var row = new Border
        {
            BackgroundColor = Color.FromArgb(selected ? "#2F2928" : "#00000000"),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(8, 6),
            Content = text,
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await SelectModelFromMenuAsync(tag);
        row.GestureRecognizers.Add(tap);
        return row;
    }

    /// <summary>从菜单里选定一个模型（API 模型 / 本地模型 / 推理档位）。</summary>
    private async Task SelectModelFromMenuAsync(string tag)
    {
        try
        {
            var parts = tag.Split('|', 2);
            var kind = parts[0];
            var id = parts.Length > 1 ? parts[1] : "";

            // 推理档位：以 R| 前缀区分
            if (kind == "R")
            {
                if (id == "关")
                {
                    AppSettings.ReasoningChoice = "";
                    AppSettings.ForceThinking = false;
                }
                else if (id == "开")
                {
                    AppSettings.ForceThinking = true;
                }
                else
                {
                    AppSettings.ReasoningChoice = id;   // 档位名（低/中/高 等）
                    AppSettings.ForceThinking = false;
                }
                RefreshMenu();
                UpdateReasoningChip();
                return;
            }

            if (kind == "LOCAL")
            {
                if (string.IsNullOrWhiteSpace(AppSettings.LocalModelApiUrl))
                    AppSettings.LocalModelApiUrl = AppSettings.DefaultLocalApiUrl;
                if (!string.IsNullOrWhiteSpace(id)) AppSettings.LocalModelName = id;
                AppSettings.UseLocalModel = true;
            }
            else
            {
                AppSettings.UseLocalModel = false;
                AppSettings.Model = id;
            }

            // 注意：必须重设请求头：云端切到本地时 Authorization 要跟着从云端 key 换成本地 key
            // （反之亦然），否则会带着错误的凭据或空头去打服务。
            ApplyApiKey();

            RefreshMenu();
            UpdateReasoningChip();
            await DisplayAlertAsync("已切换模型",
                "当前模型：" + (AppSettings.UseLocalModel ? AppSettings.LocalModelName : id), "好");
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SelectModel] {ex.Message}"); }
    }

    /// <summary>构建「推理强度」面板：档位以模型返回为准；没档位时给「关/开」两项。</summary>
    private void BuildReasonPane()
    {
        reasonList.Children.Clear();
        var options = AppSettings.GetReasoningOptions();
        var choice = AppSettings.ReasoningChoice;

        if (options.Count == 0)
        {
            // 模型没返回档位：只给开关
            reasonList.Children.Add(BuildModelRow("关", !AppSettings.ForceThinking, "R|关"));
            reasonList.Children.Add(BuildModelRow("开（强制思考）", AppSettings.ForceThinking, "R|开"));
        }
        else
        {
            reasonList.Children.Add(BuildModelRow("关", string.IsNullOrEmpty(choice), "R|关"));
            foreach (var o in options)
                reasonList.Children.Add(BuildModelRow(o, string.Equals(o, choice, StringComparison.Ordinal), "R|" + o));
        }
    }

    /// <summary>点次气泡「选择模型」。</summary>
    private void OnSubModelTapped(object? sender, EventArgs e) { _menuPane = "model"; RefreshMenu(); }
    /// <summary>点次气泡「推理强度」。</summary>
    private void OnSubReasonTapped(object? sender, EventArgs e) { _menuPane = "reason"; RefreshMenu(); }
    /// <summary>点次气泡「文件访问权限」：在主气泡里展开权限面板（与「选择模型」同一套显示逻辑）。</summary>
    private void OnSubPermTapped(object? sender, EventArgs e) { _menuPane = "perm"; RefreshMenu(); }
    /// <summary>点次气泡「发送图片」。</summary>
    private void OnSubImageTapped(object? sender, EventArgs e) { _menuPane = "image"; RefreshMenu(); }

    /// <summary>
    /// Android 返回键：菜单开着时先收起来，不直接退出页面。
    /// （旧版没有这层拦截，面板一旦弹出就只能靠切页触发 OnAppearing 才能消失。）
    /// </summary>
    protected override bool OnBackButtonPressed()
    {
        if (menuLayer.IsVisible) { CloseMenu(); return true; }
        return base.OnBackButtonPressed();
    }

    /// <summary>菜单里点「从相册选择」：收起菜单并走原有的选图流程。</summary>
    private void OnPickImageFromMenu(object? sender, EventArgs e)
    {
        CloseMenu();
        OnImageClicked(sender, e);
    }

    /// <summary>点击盾牌图标：打开菜单并定位到权限分组（盾牌入口当前不在 UI 上，保留以备用）。</summary>
    private void OnShieldTapped(object? sender, EventArgs e) => OpenMenu("perm");

    /// <summary>刷新权限面板的勾选状态与摘要。</summary>
    private void RefreshPermPanel()
    {
        int level = AppSettings.FileAccessLevel;
        bool granted = StorageAccess.IsAllFilesGranted();

        chkReadWs.Text = level == 1 ? "●" : "○";
        chkWriteWs.Text = level == 2 ? "●" : "○";
        chkReadDisk.Text = level == 3 ? "●" : "○";
        chkWriteDisk.Text = level == 4 ? "●" : "○";

        // 选中项高亮，未选中置灰
        icoReadWs.Opacity = level == 1 ? 1 : 0.45;
        icoWriteWs.Opacity = level == 2 ? 1 : 0.45;

        chkBrowser.Text = AppSettings.BrowserPermission ? "●" : "○";
        chkFullAccess.Text = AppSettings.FullAccess ? "●" : "○";

        // 完全访问时上面四个单选置灰（已被覆盖）
        permSingleGroup.Opacity = AppSettings.FullAccess ? 0.4 : 1;

        // 全盘相关项：没有系统权限时一律置灰，避免"假放开"
        bool diskDim = AppSettings.FullAccess || !granted;
        permDiskGroup.Opacity = diskDim ? 0.4 : 1;
        rowFullAccess.Opacity = granted ? 1 : 0.4;

        // 没系统权限 → 顶部亮出引导条（按系统版本给不同说法：
        // Android 11+ 叫「所有文件访问」，Android 10- 只有普通「存储」权限）
        storageWarnBox.IsVisible = !granted;
        lblGrantStorage.Text = StorageAccess.GrantButtonText;
        if (!granted)
            lblStorageWarn.Text = "尚未授予" + StorageAccess.PermissionLabel + "，全盘读写与完全访问无法生效。"
                                + "开启后 Ta 才能读写内部存储、生成的文件你也能在文件管理器里看到。";

        // 摘要：说清当前真实状态 + 边界。
        // 注意：必须精简：这段文字直接决定面板底部有多高 —— 旧版把工作区可见性、边界说明、
        //    系统版本、授权判定全堆上来，面板底下拖出一大截空白（"大下巴"）。
        //    现在只留一句状态 + 一行系统信息，边界说明挪进「导出」旁的短提示。
        var sb = new StringBuilder();
        if (!granted)
        {
            sb.Append("当前仅能访问工作区");
            sb.Append(StorageAccess.WorkspaceVisible ? "（公共存储，文件管理器可见）。" : "（应用私有目录，外部不可见）。");
        }
        else if (AppSettings.FullAccess)
        {
            sb.Append("完全访问已开启：公共存储任意位置可读写。");
        }
        else
        {
            sb.Append("当前：").Append(AppSettings.FileAccessDesc).Append('。');
        }
        sb.Append("\n").Append(SuperAdmin.AndroidVersionText)
          .Append(" · ").Append(StorageAccess.PermissionLabel)
          .Append(granted ? "已授予" : "未授予");
        lblPermSummary.Text = sb.ToString();
    }

    /// <summary>选择文件权限级别（四选一互斥）。</summary>
    private async void OnPermLevelTapped(object? sender, EventArgs e)
    {
        if (AppSettings.FullAccess)
        {
            await DisplayAlertAsync("完全访问已开启", "当前是「完全访问」模式，已覆盖所有文件权限。请先取消完全访问再单独选择。", "好");
            return;
        }
        if (sender is not TapGestureRecognizer tg || tg.CommandParameter is not string s) return;
        if (!int.TryParse(s, out var level)) return;

        // 全盘级别（3/4）需要系统真实权限，先拦一道并引导
        if (level >= 3 && !StorageAccess.IsAllFilesGranted())
        {
            await PromptGrantStorageAsync();
            return;
        }

        // 再点一次同一项 = 取消授权（回到未授权）
        AppSettings.FileAccessLevel = AppSettings.FileAccessLevel == level ? 0 : level;
        RefreshPermPanel();
    }

    /// <summary>切换浏览器权限。</summary>
    private void OnPermBrowserTapped(object? sender, EventArgs e)
    {
        AppSettings.BrowserPermission = !AppSettings.BrowserPermission;
        RefreshPermPanel();
    }

    /// <summary>切换完全访问（高风险，需二次确认 + 系统权限前置检测）。</summary>
    private async void OnPermFullAccessTapped(object? sender, EventArgs e)
    {
        if (!AppSettings.FullAccess)
        {
            // 没有系统「所有文件访问」权限时不允许开启，避免出现"假放开"
            if (!StorageAccess.IsAllFilesGranted())
            {
                await PromptGrantStorageAsync();
                return;
            }

            bool ok = await DisplayAlertAsync("注意：开启完全访问",
                "开启后 Ta 可以读写公共存储上任意文件、不受目录限制，风险很高。\n\n"
                + "请确认你完全信任当前模型与接口（中间的 API 服务商也能看到文件内容）。\n\n"
                + "注意：该权限只能访问公共存储，读不到其他 App 的私有目录。",
                "我确认", "取消");
            if (!ok) return;
        }
        AppSettings.FullAccess = !AppSettings.FullAccess;
        RefreshPermPanel();
    }

    /// <summary>
    /// 点「去开启」：拿存储权限。
    /// Android 11+ 跳系统「所有文件访问」页；Android 10 及以下直接弹运行时权限申请。
    /// 拿到后立刻刷新面板，不用等回到页面。
    /// </summary>
    private async void OnGrantStorageTapped(object? sender, EventArgs e)
    {
        await StorageAccess.RequestPermissionAsync();
        StorageAccess.InvalidateProbe();   // 重新探测，别拿旧缓存判
        RefreshPermPanel();
    }

    /// <summary>
    /// 全盘权限缺失时的统一引导：说清为什么、给出跳转按钮。
    /// 用户从系统设置回来后，OnAppearing 会重新刷新面板状态。
    /// </summary>
    private async Task PromptGrantStorageAsync()
    {
        string how = SuperAdmin.HasAllFilesAccessApi
            ? "点「去开启」会跳到系统设置，找到「青阳AI」并打开「允许访问所有文件」，回来后权限就会自动生效。\n\n"
              + "（部分定制系统——比如鸿蒙/EMUI——把入口放在「设置 → 应用 → 应用管理 → 青阳AI → 权限」里，"
              + "找不到「所有文件访问」时去那儿翻一下。）"
            : "点「去开启」会弹出系统的存储权限申请，允许即可。\n\n"
              + "（你这台是 " + SuperAdmin.AndroidVersionText + "，这个版本的系统没有「所有文件访问」那个开关，"
              + "用普通存储权限 + 传统存储模式实现同样的全盘读写效果。）";

        bool go = await DisplayAlertAsync("需要存储权限",
            "「读取/修改全盘文件」和「完全访问」需要" + StorageAccess.PermissionLabel + "，"
            + "当前尚未授予，所以这两个选项还不能生效。\n\n"
            + how + "\n\n"
            + "（开启后 Ta 生成的文件会放在 内部存储/QingYangAI/WorkSpace，你在文件管理器里能直接看到）",
            "去开启", "暂不");
        if (go)
        {
            await StorageAccess.RequestPermissionAsync();
            StorageAccess.InvalidateProbe();
            RefreshPermPanel();
        }
    }

    /// <summary>把工作区文件导出到公共 Download 目录。</summary>
    private async void OnExportWorkspaceTapped(object? sender, EventArgs e)
    {
        if (StorageAccess.WorkspaceVisible)
        {
            await DisplayAlertAsync("无需导出",
                $"当前工作区已经在公共存储里，文件管理器可以直接看到：\n\n{StorageAccess.ToDisplay(AppSettings.EffectiveWorkspacePath)}",
                "好");
            return;
        }

        if (!StorageAccess.IsAllFilesGranted())
        {
            await PromptGrantStorageAsync();
            return;
        }

        var (ok, msg) = await StorageAccess.ExportWorkspaceAsync();
        await DisplayAlertAsync(ok ? "导出完成" : "导出失败", msg, "好");
        RefreshPermPanel();
    }

    /// <summary>点击图片框架：进入全屏鉴赏模式。</summary>
    private async void OnImageFrameTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo || bo.BindingContext is not ChatMsg msg || !msg.HasImage) return;
        ImageSource source = msg.ImageSource;
        if (source == null) return;
        await Navigation.PushAsync(new ImagePreviewPage(source));
    }

    /// <summary>点击 AI 气泡上的小喇叭：朗读该条回复。</summary>
    private async void OnSpeakTapped(object? sender, EventArgs e)
    {
        if (sender is not BindableObject bo || bo.BindingContext is not ChatMsg msg) return;
        var text = msg.Content;
        if (string.IsNullOrWhiteSpace(text)) return;
        try { await TtsPlayer.PlayAsync(text); }
        catch (Exception ex) { await DisplayAlertAsync("语音播放失败", ex.Message, "确定"); }
    }

    /// <summary>
    /// 文生图管线：检测到 {img:"描述"} → 先显示预加载图片框架，调用文生图 API
    /// 生成图片（返回 base64），完成后填充进框架；失败则显示错误状态。
    /// </summary>
    /// <summary>
    /// 生图落盘 + 显示 —— Agent 模式和普通模式**共用**的那一段。
    /// 返回 (是否成功, 给模型看的观察结果)，观察结果会直接进 Agent 的对话上下文。
    /// </summary>
    private async Task<(bool ok, string obs)> GenerateImageForBubbleAsync(string desc, ChatMsg bubble)
    {
        bubble.ImageState = "loading";
        bubble.ImageProgressNote = "";              // 后端没返回进度就不显示这一行
        bubble.ImagePreviewUnsupported = false;
        bubble.PreviewBase64 = "";
        ScrollToBottom();
        FollowBottomIfNeeded();

        // 后端每推一帧就刷到气泡上。
        // Progress<T> 会把回调派回创建它的线程（这里是 UI 线程），所以直接改属性是安全的。
        var prog = new Progress<ImageGenService.ImageProgress>(p =>
        {
            try
            {
                if (p.Unsupported)
                {
                    // 后端明确不支持实时预览 → 图框里显示小字
                    bubble.ImagePreviewUnsupported = true;
                    bubble.ImageProgressNote = "";
                    return;
                }
                if (!string.IsNullOrEmpty(p.Note)) bubble.ImageProgressNote = p.Note;
                if (p.Preview is { Length: > 0 }) bubble.PreviewBase64 = Convert.ToBase64String(p.Preview);
            }
            catch { }
        });

        var r = await ImageGenService.GenerateAsync(desc, prog);

        if (r.Ok && r.Bytes != null)
        {
            try
            {
                // 图片落盘为文件（历史只存路径），base64 仅供本次会话显示
                var path = await ChatStore.SaveImageBytesAsync(r.Bytes, ".png");
                bubble.ImageUrl = path;
                if (!string.IsNullOrWhiteSpace(r.B64)) bubble.ImageBase64 = r.B64;
                bubble.ImageState = "done";
                bubble.PreviewBase64 = "";          // 成品图出来就把中间预览撤掉
                bubble.ImageProgressNote = "";
                ScrollToBottom();
                _ = ChatStore.Instance.SaveMessageAsync(bubble);
                return (true,
                    "已完成：图片已生成并显示在聊天气泡里，用户此刻能看到这张图。"
                    + "不要再输出 {img:...}，也不要重复说\"我画好了\"；"
                    + "可以在 {done:\"...\"} 里简短说明并问要不要调整。");
            }
            catch (Exception ex)
            {
                bubble.ImageState = "error";
                _ = ChatStore.Instance.SaveMessageAsync(bubble);
                return (false, "失败：图片画出来了但保存失败：" + ex.Message + "。请如实告诉用户。");
            }
        }

        bubble.ImageState = "error";
        _ = ChatStore.Instance.SaveMessageAsync(bubble);
        return (false,
            $"失败：生成图片失败：{r.Error}（一共尝试了 {r.Attempts} 次）。"
            + "用户没有看到任何图片。请如实告诉用户失败了和原因，**绝不可以说\"画好啦\"**；"
            + "可以建议 TA 换个描述重试，或者去设置里检查文生图配置。");
    }

    /// <summary>
    /// 普通聊天的生图管线：拿到 {img:"描述"} → 真的去画 → 失败就续一轮让 Ta 如实告诉用户。
    /// </summary>
    private async Task GenerateImagePipelineAsync(string userText, string rawText, ChatMsg aiBubble)
    {
        var descs = ExtractImageCommands(rawText);

        // 断流时明明看到 {img: 却抠不出描述（多半是被 SSE 拆包了）——
        // 不能静默收场，否则用户只会看到一句"画好啦"却没有图。让模型重发一次。
        if (descs.Count == 0)
        {
            await ContinueAfterImageAsync(userText, aiBubble,
                "（系统提示）你刚才输出的 {img:...} 指令没有被客户端识别（格式不对或被截断了）。\n"
                + "如果确实要画图，请重新完整输出一条 {img:\"详细描述\"}；"
                + "不需要画图就用 {done:\"...\"} 正常回应用户。");
            return;
        }

        // 开始画之前先把模型那句"画好啦"清掉，避免它和图不一致
        aiBubble.Content = "";

        var (ok, obs) = await GenerateImageForBubbleAsync(descs[0], aiBubble);

        // 成功不续轮（图本身就是答案，省一轮延迟和 token）；失败才让 Ta 用自己的话道歉
        if (!ok)
            await ContinueAfterImageAsync(userText, aiBubble, "【图片生成结果】\n" + obs);
    }

    /// <summary>生图失败 / 指令没识别时，把情况回传给模型续说一轮（照下载管线同构）。</summary>
    private async Task ContinueAfterImageAsync(string userText, ChatMsg aiBubble, string hint)
    {
        var continueMessages = new List<object>
        {
            new { role = "system", content = AppSettings.BuildSystemPrompt() },
            new { role = "user", content = userText },
            new { role = "assistant", content = InstructionParser.RemoveInstructions(aiBubble.Content) },
            new { role = "user", content = hint }
        };
        try
        {
            var body = BuildChatBody(AppSettings.ResolveChatModel(AppSettings.ForceThinking), continueMessages, stream: true);
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl) { Content = content };
            AttachAuth(req);
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None);
            await EnsureOkAsync(resp);
            await ConsumeSseAsync(resp, aiBubble, freshRound: true);
        }
        catch
        {
            // 连"解释失败"这一轮都挂了，至少别让气泡空着
            if (string.IsNullOrWhiteSpace(aiBubble.Content))
                aiBubble.Content = "（图片没画成，也没能说上话。过会儿再试试。）";
        }
        finally
        {
            _ = ChatStore.Instance.SaveMessageAsync(aiBubble);
            UpdateContextLabel();
        }
    }

    /// <summary>语音播放：如果开启 TTS 且自动播放，则在回复完成后朗读。</summary>
    private async Task SpeakIfEnabledAsync(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && AppSettings.TtsEnabled && AppSettings.TtsAutoPlay)
        {
            try { await TtsPlayer.PlayAsync(text); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[TTS] 播放失败: {ex.Message}"); }
        }
    }

#if ANDROID
/// <summary>Android 系统语音识别结果监听。</summary>
public class SystemRecognizerListener : Java.Lang.Object, Android.Speech.IRecognitionListener
{
    private readonly TaskCompletionSource<string?> _tcs;
    private readonly StringBuilder _sb = new();

    public SystemRecognizerListener(TaskCompletionSource<string?> tcs) => _tcs = tcs;

    public void OnReadyForSpeech(Android.OS.Bundle? p0) { }
    public void OnBeginningOfSpeech() { }
    public void OnRmsChanged(float p0) { }
    public void OnBufferReceived(byte[]? p0) { }
    public void OnEndOfSpeech() => _tcs.TrySetResult(_sb.ToString());
    public void OnError(Android.Speech.SpeechRecognizerError p0) =>
        _tcs.TrySetException(new Exception("系统语音识别错误：" + p0));
    public void OnEvent(int p0, Android.OS.Bundle? p1) { }
    public void OnPartialResults(Android.OS.Bundle? p0) => Collect(p0);
    public void OnResults(Android.OS.Bundle? p0)
    {
        Collect(p0);
        _tcs.TrySetResult(_sb.ToString());
    }

    private void Collect(Android.OS.Bundle? bundle)
    {
        var matches = bundle?.GetStringArrayList("android.speech.extra.RESULTS_RECOGNITION");
        if (matches == null) return;
        foreach (var m in matches)
        {
            if (!string.IsNullOrEmpty(m)) _sb.Append(m);
        }
    }
}
#endif
}

public class ChatMsg : INotifyPropertyChanged
{
    private string _content = "";
    private bool _isUser;
    private bool _hasTerminalHeader;
    private string _terminalStep = "与Shizuku通信";
    private string _terminalOutput = "";
    private string _terminalTitle = "";
    private string _thinking = "";
    private string _terminalExecLog = "";
    private string _visionResult = "";    // 图片识别文字（存历史，供后续对话使用）
    private string _imageBase64 = "";     // 生成的图片数据
    private string _imageUrl = "";        // 用户发送的图片来源（本地路径）
    private string _imageState = "";      // "" 无图 / "loading" 预加载 / "done" 完成 / "error" 失败
    private string _imageProgressNote = "";      // 生成进度那行字（后端没给就是空）
    private bool _imagePreviewUnsupported;       // 后端明确不给实时预览
    private string _previewBase64 = "";          // 中间预览帧
    private ImageSource? _previewSourceCache;    // 预览帧解出来的显示源（缓存）
    private string _voicePath = "";       // 语音消息音频文件路径

    /// <summary>数据库主键（0 表示尚未入库）。</summary>
    public int Id { get; set; }

    /// <summary>消息时间（后台主动消息、日记归属日期都依赖它）。</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>是否为后台主动发来的关心消息。</summary>
    public bool IsProactive { get; set; }

    /// <summary>是否被用户收藏。</summary>
    public bool IsFavorite { get; set; }

    /// <summary>是否为压缩摘要消息（替代早期历史消息）。</summary>
    public bool IsCompressed { get; set; }

    private bool _isWaiting;

    /// <summary>是否正在等待 AI 回复（显示转圈指示器，收到首字后关闭）。</summary>
    public bool IsWaiting
    {
        get => _isWaiting;
        set
        {
            if (_isWaiting == value) return;
            _isWaiting = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWaiting)));
        }
    }

    /// <summary>消息时间（小时:分钟，展示用；时间源为 Timestamp）。</summary>
    [JsonIgnore]
    public string TimeText => Timestamp.ToString("HH:mm");

    /// <summary>该条消息的终端执行记录（命令+输出），供后续上下文发送给模型。</summary>
    public string TerminalExecLog
    {
        get => _terminalExecLog;
        set
        {
            if (_terminalExecLog == value) return;
            _terminalExecLog = value ?? "";
        }
    }

    /// <summary>图片识别文字（视觉模型结果，存入历史供后续对话使用）。</summary>
    public string VisionResult
    {
        get => _visionResult;
        set
        {
            if (_visionResult == value) return;
            _visionResult = value ?? "";
        }
    }

    /// <summary>生成的图片数据（base64）。</summary>
    public string ImageBase64
    {
        get => _imageBase64;
        set
        {
            if (_imageBase64 == value) return;
            _imageBase64 = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageBase64)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageDone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowImageFrame)));
        }
    }

    /// <summary>用户发送的图片来源（本地路径或 URL）。</summary>
    public string ImageUrl
    {
        get => _imageUrl;
        set
        {
            if (_imageUrl == value) return;
            _imageUrl = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageUrl)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasImage)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageDone)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowImageFrame)));
        }
    }

    /// <summary>图片状态："" 无图 / "loading" 预加载 / "done" 完成 / "error" 失败。</summary>
    public string ImageState
    {
        get => _imageState;
        set
        {
            if (_imageState == value) return;
            _imageState = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageLoading)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsImageError)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowImageFrame)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowImageProgress)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNoPreviewHint)));
        }
    }

    /// <summary>语音消息音频文件路径。</summary>
    public string VoicePath
    {
        get => _voicePath;
        set
        {
            if (_voicePath == value) return;
            _voicePath = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VoicePath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasVoice)));
        }
    }

    /// <summary>是否有图片（生成图或用户图）。</summary>
    [JsonIgnore]
    public bool HasImage => !string.IsNullOrEmpty(_imageBase64) || !string.IsNullOrEmpty(_imageUrl);

    /// <summary>图片是否生成完成（可显示）。</summary>
    [JsonIgnore]
    public bool IsImageDone => !string.IsNullOrEmpty(_imageBase64) || !string.IsNullOrEmpty(_imageUrl);

    /// <summary>是否图片预加载中。</summary>
    [JsonIgnore]
    public bool IsImageLoading => _imageState == "loading";

    /// <summary>图片是否生成失败。</summary>
    [JsonIgnore]
    public bool IsImageError => _imageState == "error";

    /// <summary>
    /// AI 图框是否该显示（加载中 / 出错 / 已完成都要显示）。
    ///
    /// 注意：不能直接用 <see cref="HasImage"/> —— 它要求真有图，而失败时图是空的，
    /// 结果整个图框（连同里面的错误提示）一起被隐藏，用户根本看不到失败原因。
    /// 用户自己发的那张图仍然用 HasImage（见 ChatPage.xaml）。
    /// </summary>
    [JsonIgnore]
    public bool ShowImageFrame => IsImageLoading || IsImageError || IsImageDone;

    // ───────────── 生图实时进度 / 中间预览 ─────────────

    /// <summary>
    /// 转圈下面那行字（"正在生成… 45%" / "采样 12/30"）。
    /// **后端没返回进度就是空字符串 → 界面不显示这一行**。
    /// </summary>
    public string ImageProgressNote
    {
        get => _imageProgressNote;
        set
        {
            if (_imageProgressNote == value) return;
            _imageProgressNote = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageProgressNote)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowImageProgress)));
        }
    }

    /// <summary>是否显示进度行（正在生成 且 后端给了进度）。</summary>
    [JsonIgnore]
    public bool ShowImageProgress => IsImageLoading && !string.IsNullOrEmpty(_imageProgressNote);

    /// <summary>后端**明确**不支持实时预览时置 true → 图框里显示小字提示。</summary>
    public bool ImagePreviewUnsupported
    {
        get => _imagePreviewUnsupported;
        set
        {
            if (_imagePreviewUnsupported == value) return;
            _imagePreviewUnsupported = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImagePreviewUnsupported)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowNoPreviewHint)));
        }
    }

    /// <summary>是否显示「当前模型不支持实时预览」小字（生成中 且 后端不给预览）。</summary>
    [JsonIgnore]
    public bool ShowNoPreviewHint => IsImageLoading && _imagePreviewUnsupported;

    /// <summary>
    /// 中间预览帧（base64）。支持实时预览的后端（OpenAI partial_images / ComfyUI / SD-WebUI）
    /// 会在生成过程中不停往这儿塞新的帧。
    /// </summary>
    public string PreviewBase64
    {
        get => _previewBase64;
        set
        {
            if (_previewBase64 == value) return;
            _previewBase64 = value ?? "";
            _previewSourceCache = null;   // 帧变了，缓存作废
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewBase64)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPreview)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewSource)));
        }
    }

    /// <summary>有没有中间预览帧可显示。</summary>
    [JsonIgnore]
    public bool HasPreview => !string.IsNullOrEmpty(_previewBase64);

    /// <summary>中间预览帧的显示源（base64 解出来，带缓存，别每帧重复解）。</summary>
    [JsonIgnore]
    public ImageSource? PreviewSource
    {
        get
        {
            if (string.IsNullOrEmpty(_previewBase64)) return null;
            if (_previewSourceCache != null) return _previewSourceCache;
            try
            {
                var bytes = Convert.FromBase64String(_previewBase64);
                _previewSourceCache = ImageSource.FromStream(() => new MemoryStream(bytes));
            }
            catch { return null; }
            return _previewSourceCache;
        }
    }

    /// <summary>是否有语音消息。</summary>
    [JsonIgnore]
    public bool HasVoice => !string.IsNullOrEmpty(_voicePath);

    /// <summary>图片显示源：base64 优先，否则本地文件/URL。</summary>
    [JsonIgnore]
    public ImageSource ImageSource
    {
        get
        {
            if (!string.IsNullOrEmpty(_imageBase64))
            {
                try { return ImageSource.FromStream(() => new MemoryStream(Convert.FromBase64String(_imageBase64))); }
                catch { }
            }
            if (!string.IsNullOrEmpty(_imageUrl))
            {
                if (File.Exists(_imageUrl)) return ImageSource.FromFile(_imageUrl);
                return ImageSource.FromUri(new Uri(_imageUrl));
            }
            return null!;
        }
    }

    /// <summary>是否有思考过程（推理内容）。</summary>
    [JsonIgnore]
    public bool HasThinking => !string.IsNullOrEmpty(_thinking);

    // ─────────── Agent 循环过程（单行缩略 + 折叠） ───────────

    private AgentRun? _agentRun;

    /// <summary>本条消息的 Agent 循环过程（未开启 Agent 模式时为 null）。</summary>
    [JsonIgnore]
    public AgentRun? AgentRun
    {
        get => _agentRun;
        set
        {
            _agentRun = value;
            NotifyAgentChanged();
        }
    }

    // ───────────── Agent 过程区：重启后恢复用 ─────────────
    // 运行时是 _agentRun（有 Steps 对象）；从数据库读回来时没有对象，
    // 就靠这两个存下来的文本把"Ta 当时做了什么"重新显示出来。

    private string _agentSummarySaved = "";
    private string _agentLogSaved = "";
    private bool _agentExpandedSaved;

    /// <summary>从数据库恢复的 Agent 折叠摘要。</summary>
    public string SavedAgentSummary
    {
        get => _agentSummarySaved;
        set { _agentSummarySaved = value ?? ""; NotifyAgentChanged(); }
    }

    /// <summary>从数据库恢复的 Agent 步骤明细。</summary>
    public string SavedAgentLog
    {
        get => _agentLogSaved;
        set { _agentLogSaved = value ?? ""; NotifyAgentChanged(); }
    }

    /// <summary>翻转"恢复出来的过程区"的展开状态（没有运行时对象时用）。</summary>
    public void ToggleSavedAgentExpanded()
    {
        _agentExpandedSaved = !_agentExpandedSaved;
        NotifyAgentChanged();
    }

    /// <summary>是否显示 Agent 过程区（运行时的对象，或从库里恢复的文本）。</summary>
    [JsonIgnore]
    public bool HasAgentRun => (_agentRun != null && _agentRun.Steps.Count > 0)
                               || !string.IsNullOrEmpty(_agentLogSaved);

    /// <summary>
    /// Agent 模式下，正文（最终答复）是否还要包在 AI 圆角气泡里。
    ///
    /// 2026-10-24 用户要求：**Agent 开启时不显示 AI 聊天气泡**。
    /// 执行过程以"无气泡的步骤行"呈现；只有真的产出了最终答复，才用气泡承载。
    /// 这样执行阶段页面上是干净的一行行动作，没有那层圆角壳。
    /// </summary>
    [JsonIgnore]
    public bool ShowAiBubbleShell
    {
        get
        {
            if (_agentRun == null) return true;                  // 普通模式：照旧
            if (!string.IsNullOrEmpty(_errorText)) return true;   // 出错：要有个壳显示错误
            if (_agentRun.Steps.Count == 0) return true;          // 还没开始执行：等待指示器要有个壳
            return !string.IsNullOrEmpty(_content);               // 有最终答复才给壳
        }
    }

    /// <summary>
    /// Agent 模式下"正文以外的一切"（步骤区、思考区）是否显示 —— 它们不需要气泡壳。
    /// 只在普通模式、或 Agent 已有最终答复时与正文同框。
    /// </summary>
    [JsonIgnore]
    public bool IsAgentFlowMode => _agentRun != null && _agentRun.Steps.Count > 0;

    /// <summary>Agent 过程区标题：运行中显示当前动作，结束后显示步骤总账（▸/▾ 表示能否展开）。</summary>
    [JsonIgnore]
    public string AgentHeaderText
    {
        get
        {
            // 从库里恢复的（没有运行时对象）：只有文本可显示
            if (_agentRun == null)
                return string.IsNullOrEmpty(_agentSummarySaved)
                    ? ""
                    : _agentSummarySaved + (_agentExpandedSaved ? "  ▾" : "  ▸");

            // 运行中不给箭头（还没内容可展）；结束后给箭头提示可点开明细
            if (!_agentRun.Finished) return _agentRun.CollapsedSummary;
            return _agentRun.CollapsedSummary + (_agentRun.Collapsed ? "  ▸" : "  ▾");
        }
    }

    /// <summary>Agent 步骤明细（展开时显示，一行一步）。</summary>
    [JsonIgnore]
    public string AgentStepsText => _agentRun?.FullText ?? _agentLogSaved;

    /// <summary>过程区是否展开。</summary>
    [JsonIgnore]
    public bool AgentExpanded => _agentRun != null ? !_agentRun.Collapsed : _agentExpandedSaved;

    /// <summary>
    /// 过程区明细是否可见。
    ///
    /// 2026-10-24 用户要求：**执行中展开、执行完成自动收起**。
    /// 所以运行中一律可见（不受折叠标记影响），结束后才看用户的展开状态。
    /// </summary>
    [JsonIgnore]
    public bool AgentDetailsVisible => HasAgentRun && (AgentRunning || AgentExpanded);

    /// <summary>任务是否仍在执行中（标题流光）。</summary>
    [JsonIgnore]
    public bool AgentRunning => _agentRun != null && !_agentRun.Finished;

    /// <summary>把 Agent 过程区的所有绑定属性一次性通知刷新。</summary>
    public void NotifyAgentChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentRun)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAgentRun)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentHeaderText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentStepsText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentExpanded)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentDetailsVisible)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentRunning)));
        // Agent 模式下要隐藏黑气泡、隐藏 AI 气泡壳，这两个也一并刷新
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowTerminalHeader)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAiBubbleShell)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAgentFlowMode)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowThinkingDivider)));
    }

    /// <summary>思考过程与下方内容（终端或正文）之间的内嵌分割线是否显示。</summary>
    [JsonIgnore]
    public bool ShowThinkingDivider => HasThinking && (ShowTerminalHeader || !string.IsNullOrEmpty(_content));

    /// <summary>思考过程文本（deepseek-reasoner 的 reasoning_content）。</summary>
    public string Thinking
    {
        get => _thinking;
        set
        {
            if (_thinking == value) return;
            _thinking = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thinking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThinking)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowThinkingDivider)));
        }
    }

    /// <summary>思考是否正在生成（思考中 → 流光；正文开始或完成 → 停）。</summary>
    private bool _isThinkingActive;

    [JsonIgnore]
    public bool IsThinkingActive
    {
        get => _isThinkingActive;
        set
        {
            if (_isThinkingActive == value) return;
            _isThinkingActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsThinkingActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingStatusText)));
        }
    }

    /// <summary>正文是否已开始（决定思考状态文字："思考中…" → "思考结束·用时N秒"）。</summary>
    [JsonIgnore]
    public bool ContentStarted => !string.IsNullOrEmpty(_content);

    /// <summary>终端是否正在运行（有终端头部且未完成 → 步骤行流光）。</summary>
    [JsonIgnore]
    public bool IsTerminalRunning =>
        _hasTerminalHeader && !string.Equals(_terminalStep, "完成", StringComparison.Ordinal);

    /// <summary>思考是否展开（默认收起）。</summary>
    private bool _thinkingExpanded;

    [JsonIgnore]
    public bool ThinkingExpanded
    {
        get => _thinkingExpanded;
        set
        {
            if (_thinkingExpanded == value) return;
            _thinkingExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingArrow)));
        }
    }

    /// <summary>三角符号：展开 ▼ / 收起 ▶。</summary>
    [JsonIgnore]
    public string ThinkingArrow => ThinkingExpanded ? "ChevronDown" : "ChevronRight";

    /// <summary>思考用时（秒）。</summary>
    private double _thinkingSeconds;

    [JsonIgnore]
    public double ThinkingSeconds
    {
        get => _thinkingSeconds;
        set
        {
            if (Math.Abs(_thinkingSeconds - value) < 0.05) return;
            _thinkingSeconds = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingSeconds)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingStatusText)));
        }
    }

    /// <summary>
    /// 思考状态文字：
    /// 思考过程中 →「思考中…」（流光下动态跳动）；
    /// 正文开始后 →「思考结束·用时N秒」（只计思考过程，正文不计入）。
    /// </summary>
    [JsonIgnore]
    public string ThinkingStatusText
    {
        get
        {
            if (_isThinkingActive && !ContentStarted) return "思考中…";
            if (_thinkingSeconds < 60)
                return $"思考结束·用时{_thinkingSeconds:F0}秒";
            int min = (int)_thinkingSeconds / 60;
            int sec = (int)_thinkingSeconds % 60;
            return $"思考结束·用时{min}分{sec}秒";
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Content)));
            // 没在打字的时候，界面直接跟 Content 走（打字机跑着时由计时器驱动 DisplayContent）
            if (_revealed < 0)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayContent)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasContent)));
            // Agent 模式下：有了最终答复才给气泡壳（见 ShowAiBubbleShell）
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAiBubbleShell)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowThinkingDivider)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentStarted)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThinkingStatusText)));
        }
    }

    /// <summary>是否有正文内容（控制朗读按钮显隐）。</summary>
    [JsonIgnore]
    public bool HasContent => !string.IsNullOrEmpty(_content);

    // ───────────── 打字机（显示侧逐字上屏）─────────────

    /// <summary>已揭开的字数；-1 = 没启用打字机（直接显示全文）。</summary>
    private int _revealed = -1;

    /// <summary>
    /// 界面上**实际显示**的正文。
    ///
    /// 打字机跑着的时候只显示前 `_revealed` 个字；没跑就等于 <see cref="Content"/>。
    /// 气泡正文绑的是**这个**而不是 Content —— Content 永远是完整文本
    /// （存库、给模型看、朗读都用它，不能因为显示慢就丢字）。
    /// </summary>
    [JsonIgnore]
    public string DisplayContent =>
        _revealed < 0 || _revealed >= _content.Length ? _content : _content[.._revealed];

    /// <summary>打字机是否还在跑。</summary>
    [JsonIgnore]
    public bool IsRevealing => _revealed >= 0 && _revealed < _content.Length;

    /// <summary>开始打字机：从 0 字开始逐字揭开。</summary>
    public void BeginReveal()
    {
        _revealed = 0;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayContent)));
    }

    /// <summary>
    /// 推进一帧。返回 true = 还在打（没追平）。
    ///
    /// **落后越多补得越快** —— 否则长回复生成完还要再"打"十几秒，用户会以为卡住了。
    /// <paramref name="fast"/> = 流已经收完了（内容不会再增长），用更猛的步长把尾巴排空，
    /// 但**仍然是逐字出**，不会一下子全蹦出来。
    ///
    /// 注意：内容还是空的时候**不能**判成"追平"：
    /// 计时器在 `BeginTyping` 时（Content 尚为空）就已启动，若首块数据 25ms 内还没到，
    /// 旧逻辑会立刻返回 false → 调用方 FlushReveal 并停表 → 打字机永久失效（整块蹦出）。
    /// 所以内容为空时返回 true（继续等），只有"已有内容且已追平"才算结束。
    /// </summary>
    public bool RevealTick(bool fast = false)
    {
        if (_revealed < 0) return false;

        int full = _content.Length;

        // 内容还没来（或还没增长）：保持"在打"状态，等数据到达，别让计时器自杀
        if (full == 0) return true;

        if (_revealed >= full) return false;

        int backlog = full - _revealed;
        int step = fast
            ? Math.Clamp(backlog / 2 + 1, 2, 40)    // 收尾：最多 1600 字/秒
            : Math.Clamp(backlog / 6 + 1, 1, 16);   // 生成中：1~16 字/帧（40~640 字/秒）
        _revealed = Math.Min(full, _revealed + step);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayContent)));
        return _revealed < full;
    }

    /// <summary>把剩下的字一次全放出来（流结束 / 出错 / 换气泡时调，保证一个字都不丢）。</summary>
    public void FlushReveal()
    {
        if (_revealed < 0) return;
        _revealed = -1;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayContent)));
    }

    // ───────────── 错误行（和正文分开显示）─────────────

    private string _errorText = "";

    /// <summary>
    /// 出错时的错误行，**单独显示在正文下面**。
    ///
    /// 刻意不拼进 <see cref="Content"/> —— 错误混在回复里会看着像 Ta 说的话，
    /// 也会把已经流出来的正文搅浑。正文归正文、错误归错误。
    /// 只存在内存里（不落库）：错误是临时状态，重启后消失没关系。
    /// </summary>
    [JsonIgnore]
    public string ErrorText
    {
        get => _errorText;
        set
        {
            if (_errorText == value) return;
            _errorText = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
            // 出错时 Agent 模式也要把气泡壳显示出来（见 ShowAiBubbleShell）
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAiBubbleShell)));
        }
    }

    /// <summary>有没有错误要显示。</summary>
    [JsonIgnore]
    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public bool IsUser
    {
        get => _isUser;
        set
        {
            if (_isUser == value) return;
            _isUser = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsUser)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAI)));
        }
    }

    [JsonIgnore]
    public bool IsAI => !_isUser;

    /// <summary>是否显示终端头部三行小气泡。</summary>
    /// <summary>
    /// 是否显示终端黑气泡（&gt;_ 三行小框）。
    ///
    /// 2026-10-24 用户要求：**只有 Agent 循环关闭时才显示这个黑气泡**。
    /// Agent 开启时执行过程改用「无气泡的步骤行」（见 HasAgentRun），不再出现黑框。
    /// 判定同时看"这一步是否真设了终端头"和"Agent 是否在跑"，两者都满足才显示。
    /// </summary>
    [JsonIgnore]
    public bool ShowTerminalHeader => _hasTerminalHeader && _agentRun == null;

    public bool HasTerminalHeader
    {
        get => _hasTerminalHeader;
        set
        {
            if (_hasTerminalHeader == value) return;
            _hasTerminalHeader = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTerminalHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowTerminalHeader)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowThinkingDivider)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminalRunning)));
        }
    }

    /// <summary>头部第二行状态：与Shizuku通信 / 拉取终端 / 运行终端 / 完成。</summary>
    public string TerminalStep
    {
        get => _terminalStep;
        set
        {
            if (_terminalStep == value) return;
            _terminalStep = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalStep)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalStepLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalHeaderText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminalRunning)));
        }
    }

    /// <summary>头部第三行输出结果。</summary>
    public string TerminalOutput
    {
        get => _terminalOutput;
        set
        {
            if (_terminalOutput == value) return;
            _terminalOutput = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalOutput)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalOutputLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalHeaderText)));
        }
    }

    /// <summary>第一行固定文案。</summary>
    [JsonIgnore]
    public string TerminalTitleLine => string.IsNullOrEmpty(_terminalTitle) ? "使用shizuku — 终端" : _terminalTitle;

    /// <summary>工具气泡标题：本次实际调用了什么（API 名 / Shizuku 终端命令），空 = 旧默认文案。</summary>
    public string TerminalTitle
    {
        get => _terminalTitle;
        set
        {
            if (_terminalTitle == value) return;
            _terminalTitle = value ?? "";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalTitle)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalTitleLine)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TerminalHeaderText)));
        }
    }

    /// <summary>第二行：当前步骤。</summary>
    [JsonIgnore]
    public string TerminalStepLine => $"当前步骤：{TerminalStep}";

    /// <summary>第三行：输出。</summary>
    [JsonIgnore]
    public string TerminalOutputLine => $"输出：[{TerminalOutput}]";

    /// <summary>三行完整文本（供折叠/展示）。</summary>
    [JsonIgnore]
    public string TerminalHeaderText =>
        TerminalTitleLine + "\n" + TerminalStepLine + "\n" + TerminalOutputLine;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class StreamChunk
{
    public List<ChoiceItem>? choices { get; set; }

    /// <summary>原始 usage 节点（保留成 JsonElement，交给 TokenUsageMapper 做多厂商字段映射）。</summary>
    public JsonElement? usage { get; set; }
}

/// <summary>ConsumeSseAsync 的收流结果。</summary>
public sealed class SseConsumeResult
{
    /// <summary>网络侧收到的完整正文（含尚未上屏的部分）。</summary>
    public StringBuilder Buffer { get; } = new();
    public DateTime? ThinkingStart { get; set; }
    /// <summary>最后一个思考字到达的时刻（思考用时的终点，正文不计入）。</summary>
    public DateTime? ThinkingEnd { get; set; }
    /// <summary>思考用时是否已冻结（正文已开始）。</summary>
    public bool ThinkingFrozen { get; set; }
    public bool ThinkingActive { get; set; }
    public bool ContentStarted { get; set; }
    /// <summary>网络侧是否已收完流。</summary>
    public bool ReadDone { get; set; }
    /// <summary>正文中出现 {cmd:"..."} 指令，已提前断流。</summary>
    public bool CancelledForCommand { get; set; }
    /// <summary>正文中出现 {api:"..."} 指令，已提前断流。</summary>
    public bool CancelledForApi { get; set; }
    /// <summary>正文中出现 {img:"..."} 指令，已提前断流。</summary>
    public bool CancelledForImage { get; set; }
    /// <summary>正文中出现 {browse:"..."} 指令，已提前断流。</summary>
    public bool CancelledForBrowse { get; set; }

    /// <summary>正文中出现 {download:"..."} 指令，已提前断流。</summary>
    public bool CancelledForDownload { get; set; }

    /// <summary>正文中出现 {web:"..."} 指令（真浏览器），已提前断流。</summary>
    public bool CancelledForWeb { get; set; }

    /// <summary>正文中出现 {remind:"时间|内容"} 指令（承诺提醒），已提前断流。</summary>
    public bool CancelledForReminder { get; set; }

    // ── 流式诊断（排查"整段蹦出来"到底卡在谁身上）──

    /// <summary>从发请求到收到第一块内容的毫秒数；-1 = 还没收到。</summary>
    public int FirstDeltaMs { get; set; } = -1;

    /// <summary>一共收到多少块内容。</summary>
    public int Chunks { get; set; }

    /// <summary>整个流收完用了多少毫秒。</summary>
    public int TotalMs { get; set; }

    /// <summary>是否被"卡死看门狗"叫停（服务器长时间没吐数据）。</summary>
    public bool Stalled { get; set; }
}
public class ChoiceItem
{
    public DeltaItem? delta { get; set; }
}
public class DeltaItem
{
    public string? content { get; set; }
    public string? reasoning_content { get; set; }
}
public class UsageItem
{
    public int prompt_cache_hit_tokens { get; set; }
    public int prompt_cache_miss_tokens { get; set; }

    // 兼容嵌套结构：prompt_cache_hit_token_details.cache_tokens
    public CacheDetails? prompt_cache_hit_token_details { get; set; }

    // 个别接口用 cached_tokens / uncached_tokens（如 Anthropic 风格）
    public int? cached_tokens { get; set; }
    public int? uncached_tokens { get; set; }
}

public class CacheDetails
{
    public int? cache_tokens { get; set; }
    public int? text_tokens { get; set; }
}

/// <summary>从 usage 兼容提取 (命中, 未命中)。</summary>
public static class CacheUsage
{
    public static (int hit, int miss) Extract(UsageItem? usage)
    {
        if (usage == null) return (0, 0);
        int hit = usage.prompt_cache_hit_tokens;
        int miss = usage.prompt_cache_miss_tokens;

        // 嵌套 details.cache_tokens 兜底
        if (hit <= 0 && usage.prompt_cache_hit_token_details?.cache_tokens is int ch && ch > 0)
            hit = ch;
        // cached_tokens / uncached_tokens 兜底
        if (hit <= 0 && usage.cached_tokens is int ct && ct > 0) hit = ct;
        if (miss <= 0 && usage.uncached_tokens is int ut && ut > 0) miss = ut;
        return (hit, miss);
    }
}

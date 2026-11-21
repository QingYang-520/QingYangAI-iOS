using System.Text;

namespace 青阳AI;

// ============================================================================
// 感知体系（2026-10-29 整合）
//
// 合并自原来的四个文件：
//   DeviceContextService.cs —— 聚合所有数据源，输出「用户此刻状态」文本
//   ApiRegistry.cs          —— 10 个命名感知 API 的注册表
//   CareWatch.cs            —— 后台默默观察清单（不打扰、不展示）
//   CareScheduler.cs        —— 主动消息的安静守则硬闸门
// ============================================================================

// ────────────── 原 DeviceContextService.cs ──────────────
/// <summary>
/// 感知中心：
/// - 零权限基础项（时间/电量/网络/媒体/屏幕/闹钟）被动注入每次对话；
/// - 深度感知项（屏幕使用时间/前台应用/通知/日程）不再被动注入，
///   注册进 ApiRegistry 由 AI 通过 {api:"名"} 主动调用（想查就查）。
/// 设计原则：能用系统 API 直接读的就不走 Shizuku；Shizuku 只作前台应用兜底。
/// </summary>
public static class DeviceContextService
{
    /// <summary>构建被动注入的"用户此刻状态"（只含零权限基础项，多行纯文本）。</summary>
    public static async Task<string> BuildAsync()
    {
        var lines = new List<string>();
        var now = DateTime.Now;
        lines.Add($"时间：{now:yyyy年M月d日 HH:mm}（{WeekCn(now.DayOfWeek)}）");

        try
        {
            var b = Battery.Default;
            if (b.ChargeLevel >= 0)
            {
                var charge = b.State switch
                {
                    BatteryState.Charging => "充电中",
                    BatteryState.Full => "已充满",
                    _ => "未充电"
                };
                lines.Add($"电量：{(int)Math.Round(b.ChargeLevel * 100)}%（{charge}）");
            }
        }
        catch { }

        try
        {
            var conn = Connectivity.Current;
            string net = conn.ConnectionProfiles.Contains(ConnectionProfile.WiFi) ? "Wi-Fi"
                : conn.ConnectionProfiles.Contains(ConnectionProfile.Cellular) ? "移动数据"
                : "未知";
            if (conn.NetworkAccess != NetworkAccess.Internet) net += "（无互联网）";
            lines.Add($"网络：{net}");
        }
        catch { }

#if ANDROID
        try
        {
            var media = await GetMediaTextAsync();
            if (!string.IsNullOrWhiteSpace(media)) lines.Add(media);
        }
        catch { }

        try
        {
            var screen = await GetScreenTextAsync();
            if (!string.IsNullOrWhiteSpace(screen)) lines.Add(screen);
        }
        catch { }

        try
        {
            var alarm = await GetAlarmTextAsync();
            if (!string.IsNullOrWhiteSpace(alarm)) lines.Add(alarm);
        }
        catch { }
#endif

        return string.Join("\n", lines);
    }

    // ─────────── 以下为各感知 API 的实现（ApiRegistry 调用）───────────

    /// <summary>屏幕使用时间：今天各应用前台时长排行（需使用情况访问权）。</summary>
    public static Task<string> GetScreenTimeTextAsync()
    {
#if ANDROID
        if (!AppSettings.CareSenseEnabled)
            return Task.FromResult("用户没有开启生活感知开关，看不到");
        if (!UsageStatsHelper.IsGranted())
            return Task.FromResult("用户未授权「使用情况访问权」，看不到");

        var top = UsageStatsHelper.GetTodayTopApps(5);
        if (top.Count == 0)
            return Task.FromResult("今天还没有可统计的使用记录");

        return Task.FromResult("今天各应用使用时长：" +
            string.Join("、", top.Select(t => $"{t.app} {FormatDuration(t.time)}")));
#else
        return Task.FromResult("仅 Android 支持");
#endif
    }

    /// <summary>当前前台应用（使用情况访问权优先，Shizuku 兜底）。</summary>
    public static async Task<string> GetForegroundTextAsync()
    {
#if ANDROID
        if (!AppSettings.CareSenseEnabled)
            return "用户没有开启生活感知开关，看不到";

        if (UsageStatsHelper.IsGranted())
        {
            var fg = UsageStatsHelper.GetForegroundPackage();
            if (!string.IsNullOrWhiteSpace(fg))
                return $"用户当前正在用：{GetAppLabel(fg)}";
        }
        if (ShizukuRunner.Available())
        {
            var viaShizuku = await GetForegroundViaShizukuAsync();
            if (!string.IsNullOrWhiteSpace(viaShizuku))
                return $"用户当前正在用：{viaShizuku}";
        }
        return "拿不到前台应用（未授权且 Shizuku 不可用）";
#else
        return "仅 Android 支持";
#endif
    }

    /// <summary>
    /// 最近几分钟收到的新消息通知——四路智能切换，取可用的一路：
    /// ① 通知使用权服务（若已连接）② 无障碍服务（鸿蒙可用）③ Shizuku dumpsys（插线时）
    /// ④ 系统 GetActiveNotifications（免权限兜底）。共享同一持久化缓冲。
    /// </summary>
    public static async Task<string> GetNotificationsTextAsync()
    {
#if ANDROID
        if (!AppSettings.CareSenseEnabled)
            return "用户没有开启生活感知开关，看不到";

        // ① 通知使用权服务已连接 → 走它的缓冲
        // ② 无障碍服务已开启 → 同样写入同一缓冲（Store 共用）
        bool svcOk = CareNotificationListener.IsEnabled()
                     && !string.IsNullOrEmpty(Preferences.Default.Get("NotifSvcConnected", ""));
        bool a11yOk = QyAccessibilityService.IsEnabled();
        if (svcOk || a11yOk)
        {
            var notes = CareNotificationListener.GetRecentSummaries(10, 6);
            return notes.Count > 0
                ? "最近收到的通知：\n" + string.Join("\n", notes)
                : "最近 10 分钟没有新通知";
        }

        // ③ Shizuku 在线（插线时）→ dumpsys notification 读当前通知
        if (ShizukuRunner.Available())
        {
            var r = await ShizukuRunner.ExecuteAsync("dumpsys notification --noredact | grep -E 'android.title|android.text' | head -20");
            var outText = (r.Stdout ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(outText))
                return "当前通知栏内容：\n" + outText;
        }

        // ④ 直连：系统允许读到的活动通知（免权限，仅本App+部分系统通知）
        return "通知感知未启用任何通道（通知使用权服务未连接、无障碍未开、Shizuku未插线）。";
#else
        return "仅 Android 支持";
#endif
    }

    /// <summary>今天剩余的日程（需日历权限）。</summary>
    public static Task<string> GetCalendarTextAsync()
    {
#if ANDROID
        if (!CalendarHelper.IsGranted())
            return Task.FromResult("用户未授权日历权限，看不到");
        var events = CalendarHelper.GetTodayEvents();
        return Task.FromResult(events.Count > 0
            ? "今天接下来的日程：\n" + string.Join("\n", events)
            : "今天没有剩余日程");
#else
        return Task.FromResult("仅 Android 支持");
#endif
    }

    /// <summary>电量与充电状态。</summary>
    public static Task<string> GetBatteryTextAsync()
    {
        try
        {
            var b = Battery.Default;
            if (b.ChargeLevel < 0) return Task.FromResult("电量信息不可用");
            var charge = b.State switch
            {
                BatteryState.Charging => "充电中",
                BatteryState.Full => "已充满",
                _ => "未充电"
            };
            return Task.FromResult($"电量 {(int)Math.Round(b.ChargeLevel * 100)}%（{charge}）");
        }
        catch (Exception ex) { return Task.FromResult("电量信息不可用：" + ex.Message); }
    }

    /// <summary>媒体播放与铃声模式。</summary>
    public static Task<string> GetMediaTextAsync()
    {
#if ANDROID
        try
        {
            var am = (Android.Media.AudioManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.AudioService);
            if (am == null) return Task.FromResult("");
            var mode = am.RingerMode switch
            {
                Android.Media.RingerMode.Silent => "静音",
                Android.Media.RingerMode.Vibrate => "振动",
                _ => "正常"
            };
            return Task.FromResult($"铃声：{mode}；{(am.IsMusicActive ? "正在播放音乐/媒体" : "没在播放媒体")}");
        }
        catch { return Task.FromResult(""); }
#else
        return Task.FromResult("");
#endif
    }

    /// <summary>下一个闹钟（绑定库缺 TriggerAtMillis，走 Java 反射取触发时间）。</summary>
    public static Task<string> GetAlarmTextAsync()
    {
#if ANDROID
        try
        {
            var alm = (Android.App.AlarmManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.AlarmService);
            var next = alm?.NextAlarmClock;
            if (next == null) return Task.FromResult("没有设置下一个闹钟");

            var getter = next.Class.GetMethod("getTriggerAtMillis");
            if (getter?.Invoke(next) is Java.Lang.Long lng && (long)lng > 0)
            {
                var t = DateTimeOffset.FromUnixTimeMilliseconds((long)lng).LocalDateTime;
                return Task.FromResult($"下一个闹钟：{t:HH:mm}");
            }
            return Task.FromResult("");
        }
        catch { return Task.FromResult(""); }
#else
        return Task.FromResult("");
#endif
    }

    /// <summary>屏幕亮灭。</summary>
    public static Task<string> GetScreenTextAsync()
    {
#if ANDROID
        try
        {
            var pm = (Android.OS.PowerManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.PowerService);
            if (pm == null) return Task.FromResult("");
            return Task.FromResult(pm.IsInteractive ? "屏幕：亮屏使用中" : "屏幕：熄屏");
        }
        catch { return Task.FromResult(""); }
#else
        return Task.FromResult("");
#endif
    }

    /// <summary>网络状态。</summary>
    public static Task<string> GetNetworkTextAsync()
    {
        try
        {
            var conn = Connectivity.Current;
            string net = conn.ConnectionProfiles.Contains(ConnectionProfile.WiFi) ? "Wi-Fi"
                : conn.ConnectionProfiles.Contains(ConnectionProfile.Cellular) ? "移动数据"
                : "未知";
            if (conn.NetworkAccess != NetworkAccess.Internet) net += "（无互联网）";
            return Task.FromResult($"网络：{net}");
        }
        catch (Exception ex) { return Task.FromResult("网络信息不可用：" + ex.Message); }
    }

    /// <summary>剪贴板正文超过这个长度就截断（避免把整篇文档灌进上下文）。</summary>
    private const int ClipboardMaxChars = 300;

    /// <summary>
    /// 用户最近复制到剪贴板的文字。
    ///
    /// 注意：Android 10（Q）起系统只允许**前台**应用读剪贴板：应用在后台 / 锁屏时调用
    /// 一律返回空（部分 ROM 还会额外弹"已读取剪贴板"的提示）。
    /// 所以"空"不等于"真的没复制"——文案里必须把这条限制讲明白，
    /// 否则 AI 会把"读不到"当成"用户没复制"，然后瞎猜。
    /// </summary>
    public static async Task<string> GetClipboardTextAsync()
    {
        string? text;
        try
        {
            // MAUI Essentials 的剪贴板 API 必须在主线程调用
            text = await MainThread.InvokeOnMainThreadAsync(async () => await Clipboard.Default.GetTextAsync());
        }
        catch (Exception ex)
        {
            return "读不到剪贴板（系统限制）：" + ex.Message
                 + "。Android 10 起只有应用在前台时才允许读剪贴板。";
        }

        if (string.IsNullOrWhiteSpace(text))
            return "剪贴板里没有文字（也可能是 Android 10+ 的限制：只有应用在前台时才能读剪贴板，后台读一律返回空）";

        text = text.Trim();
        if (text.Length > ClipboardMaxChars) text = text[..ClipboardMaxChars] + "…";
        return "用户刚复制了：" + text;
    }

#if ANDROID
    /// <summary>包名 → 应用显示名（拿不到返回包名）。</summary>
    public static string GetAppLabel(string pkg)
    {
        try
        {
            var pm = Android.App.Application.Context.PackageManager;
            var info = pm?.GetApplicationInfo(pkg, 0);
            if (info != null && pm != null)
            {
                var label = pm.GetApplicationLabel(info)?.ToString();
                if (!string.IsNullOrWhiteSpace(label)) return label;
            }
        }
        catch { }
        return pkg;
    }

    /// <summary>Shizuku 兜底：dumpsys 查前台应用包名并转显示名。</summary>
    private static async Task<string?> GetForegroundViaShizukuAsync()
    {
        try
        {
            var r = await ShizukuRunner.ExecuteAsync("dumpsys activity activities | grep -i mResumedActivity");
            var line = (r.Stdout ?? "").Trim();
            if (line.Length == 0) return null;

            // 形如 mResumedActivity: ActivityRecord{... u0 com.xx/.yy t123}
            int u0 = line.IndexOf("u0 ", StringComparison.Ordinal);
            var pkg = u0 >= 0 ? line[(u0 + 3)..] : line;
            int sp = pkg.IndexOf(' ');
            if (sp > 0) pkg = pkg[..sp];
            if (pkg.EndsWith('}')) pkg = pkg[..^1];
            return GetAppLabel(pkg.Length > 80 ? pkg[..80] : pkg);
        }
        catch { return null; }
    }
#endif

    /// <summary>时长人性化：45秒 / 12分钟 / 3小时42分钟。</summary>
    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}秒";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}分钟";
        return $"{(int)t.TotalHours}小时{t.Minutes}分钟";
    }

    public static string WeekCn(DayOfWeek d) => d switch
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

// ────────────── 原 ApiRegistry.cs ──────────────
/// <summary>
/// 「Ta可以调用的感知 API」注册表：把手机上的各类信息封装成命名的 API，
/// AI 通过 {api:"名"} 隐藏指令主动调用（想查什么就查什么），客户端执行后把结果回传。
/// 全部走系统标准 API，无需 Shizuku；未授权/不可用时返回说明文字而不是报错。
/// </summary>
public static class ApiRegistry
{
    public sealed class ApiInfo
    {
        public string Id = "";
        public string Name = "";   // 中文名（气泡与结果标注用）
        public string Desc = "";   // 给模型看的用途说明
        public Func<Task<string>> Run = () => Task.FromResult("");
    }

    public static readonly List<ApiInfo> All = new()
    {
        new() {
            Id = "screen_time", Name = "屏幕使用时间",
            Desc = "今天各应用的使用时长排行。想知道用户今天刷了多久手机、在忙什么、睡没睡好时调用",
            Run = DeviceContextService.GetScreenTimeTextAsync
        },
        new() {
            Id = "foreground", Name = "前台应用",
            Desc = "用户此刻正在用哪个应用。想确认用户当前在做什么时调用",
            Run = DeviceContextService.GetForegroundTextAsync
        },
        new() {
            Id = "notifications", Name = "最近通知",
            Desc = "最近几分钟手机收到的新消息通知（微信等）。想知道用户有没有收到/看到消息时调用",
            Run = DeviceContextService.GetNotificationsTextAsync
        },
        new() {
            Id = "calendar", Name = "今日日程",
            Desc = "用户日历里今天剩余的日程。早上或聊到安排、行程时调用",
            Run = DeviceContextService.GetCalendarTextAsync
        },
        new() {
            Id = "battery", Name = "电量状态",
            Desc = "电量和充电状态。电量低时提醒用户充电",
            Run = DeviceContextService.GetBatteryTextAsync
        },
        new() {
            Id = "music", Name = "媒体播放",
            Desc = "是否在播放音乐/媒体、铃声模式。想知道用户在不在听歌、是不是在开会静音时调用",
            Run = DeviceContextService.GetMediaTextAsync
        },
        new() {
            Id = "alarm", Name = "下一个闹钟",
            Desc = "系统里设置的下一个闹钟时间。结合当前时间判断用户睡没睡、该不该起时调用",
            Run = DeviceContextService.GetAlarmTextAsync
        },
        new() {
            Id = "screen", Name = "屏幕状态",
            Desc = "屏幕亮还是灭。判断用户是否正在使用手机时调用",
            Run = DeviceContextService.GetScreenTextAsync
        },
        new() {
            Id = "network", Name = "网络状态",
            Desc = "Wi-Fi 还是流量。判断用户在家还是在外面时调用",
            Run = DeviceContextService.GetNetworkTextAsync
        },
        new() {
            Id = "clipboard", Name = "剪贴板",
            Desc = "用户最近复制到剪贴板的文字。想知道用户是不是刚复制了什么想给你看时调用",
            Run = DeviceContextService.GetClipboardTextAsync
        },
    };

    public static ApiInfo? Find(string id)
    {
        id = id.Trim();
        foreach (var a in All)
            if (a.Id == id) return a;
        return null;
    }

    /// <summary>执行一个感知 API，返回结果文本（失败/未授权返回说明文字）。</summary>
    public static async Task<string> ExecuteAsync(string id)
    {
        var api = Find(id);
        if (api == null) return $"没有叫「{id}」的 API";
        try
        {
            return await api.Run();
        }
        catch (Exception ex)
        {
            return "调用失败：" + ex.Message;
        }
    }

    /// <summary>给模型看的 API 菜单文本。</summary>
    public static string BuildMenuText()
    {
        var sb = new System.Text.StringBuilder("可调用的感知 API（输出 {api:\"名\"} 调用，一行一个，最多两条）：\n");
        foreach (var a in All)
            sb.AppendLine($"{a.Id} = {a.Name}：{a.Desc}");
        return sb.ToString();
    }
}

// ────────────── 原 CareWatch.cs ──────────────
/// <summary>
/// 内部关注清单 + 定期默默观察。
/// 「她」在后台维护一份看不到的关注清单（几点该留意用户什么），定期自己调感知 API
/// 默默了解用户的行动状态——结果只存在后台（Preferences），不发通知不打扰，
/// 下次聊天/回复时自然带出（像真人"心里记挂着，偷偷瞄过一眼"）。
/// </summary>
public static class CareWatch
{
    // ── 内部关注清单（不展示给用户；按需增删项） ──

    private sealed record WatchItem(string Id, int StartHour, int EndHour, string Api, string Reason);

    private static readonly WatchItem[] Watchlist =
    {
        // 早上：看TA昨晚几点睡、睡多久（用今天使用时长估）
        new("morning", 6, 11, "screen_time", "想看看TA昨晚睡得好不好"),
        // 白天：瞄一眼TA现在在忙什么
        new("foreground", 9, 22, "foreground", "想知道TA这会儿在做什么"),
        // 午后/傍晚：今天整体用了多久手机
        new("day_usage", 12, 19, "screen_time", "想看看TA今天手机用得久不久"),
        // 深夜：看TA睡没睡、在不在玩手机
        new("night", 22, 5, "screen", "深夜了，看看TA睡没睡"),
        // 随时：有没有新消息可能让TA分心/烦心
        new("notif", 8, 23, "notifications", "看看TA有没有收到什么新消息"),
    };

    // ── 观察节奏（后台静默，不打扰） ──

    private const int ObserveIntervalMin = 45;   // 距上次观察至少隔多久
    private const int ObserveStaleHours = 8;      // 超过多久的近况不再注入（太旧就不当"记得"）

    /// <summary>后台定时调一次：到点则挑一条当前时段相关的关注项默默查一下并存起来。</summary>
    public static async Task ObserveOnceAsync()
    {
        try
        {
            // 感知总开关没开 → 不默默观察（她"看不见"）
            if (!AppSettings.CareSenseEnabled) return;

            var now = DateTime.Now;
            if (DateTime.TryParse(Preferences.Default.Get("CareWatchLastAt", ""), out var last))
            {
                if ((now - last).TotalMinutes < ObserveIntervalMin) return;
            }

            // 挑当前时段命中的关注项（轮转：记录上次查的是哪条，下条接着来）
            int cursor = Preferences.Default.Get("CareWatchCursor", 0);
            var candidates = new List<WatchItem>();
            for (int i = 0; i < Watchlist.Length; i++)
            {
                var it = Watchlist[(cursor + i) % Watchlist.Length];
                if (InWindow(it, now)) { candidates.Add(it); break; }
            }
            var pick = candidates.FirstOrDefault();
            if (pick == null) return; // 当前时段没有匹配项，等下一轮

            var text = await ApiRegistry.ExecuteAsync(pick.Api);
            // 没授权/查不到时 text 会是"看不到"说明——不记录，避免注入废话
            if (text.Contains("看不到") || text.Contains("未授权") || text.Contains("没有开启")) return;

            Preferences.Default.Set("CareWatchLastAt", now.ToString("yyyy-MM-dd HH:mm:ss"));
            Preferences.Default.Set("CareWatchCursor", (cursor + 1) % Watchlist.Length);
            Preferences.Default.Set("CareWatchText", text);
            Preferences.Default.Set("CareWatchReason", pick.Reason);
        }
        catch { /* 观察失败静默，下轮再试 */ }
    }

    /// <summary>读取最近一次默默观察的近况（用于注入 system；太旧或没有返回空）。</summary>
    public static string BuildObservedContext()
    {
        try
        {
            var atText = Preferences.Default.Get("CareWatchLastAt", "");
            if (!DateTime.TryParse(atText, out var at)) return "";
            if ((DateTime.Now - at).TotalHours > ObserveStaleHours) return "";

            var text = Preferences.Default.Get("CareWatchText", "");
            var reason = Preferences.Default.Get("CareWatchReason", "");
            if (string.IsNullOrWhiteSpace(text)) return "";

            return $"（你刚才自己悄悄留意到的：{reason} → {text}，约 {at:HH:mm}。用户看不到这段，" +
                   "把它当你自己亲眼看到的一样自然带进对话，别复述原始数据。）";
        }
        catch { return ""; }
    }

    private static bool InWindow(WatchItem it, DateTime now)
    {
        int h = now.Hour;
        // 跨零点窗口（如 22~5）
        return it.StartHour <= it.EndHour
            ? h >= it.StartHour && h < it.EndHour
            : h >= it.StartHour || h < it.EndHour;
    }
}

// ────────────── 原 CareScheduler.cs ──────────────
/// <summary>
/// 主动消息的硬闸门（安静守则）：免打扰时段、每日上限、最小间隔。
/// 要不要说话、说什么，由 InnerLifeService 的心跳让Ta自己判断；
/// 这里只负责"什么情况下绝对不打扰"的底线。
/// </summary>
public static class CareScheduler
{
    /// <summary>两条主动消息之间的最小间隔（小时）。</summary>
    public const double MinProactiveGapHours = 3;

    /// <summary>当前是否允许发主动消息（硬闸门，通过后由内心心跳自主决定说不说）。</summary>
    public static bool CanSpeak(DateTime now, int todayProactiveCount, DateTime? lastProactiveAt)
    {
        if (!AppSettings.CareEnabled) return false;
        if (AppSettings.IsInQuietHours(now)) return false;
        if (todayProactiveCount >= AppSettings.CareDailyCap) return false;
        if (lastProactiveAt.HasValue &&
            (now - lastProactiveAt.Value).TotalHours < MinProactiveGapHours) return false;
        return true;
    }
}


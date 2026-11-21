#if ANDROID
using Android.Content;
using Android.OS;
using Android.Views;

namespace 青阳AI;

/// <summary>
/// 「替换原机 AI 助手」（实验性功能）。
///
/// 原理：Android 官方 <c>RoleManager.ROLE_ASSISTANT</c>（API 29+）。
/// 把青阳AI 设为默认助手后，用户长按电源键（0.5 秒左右）系统就会唤醒本应用，
/// 而不再是原机助手。全程不需要 root、不需要 Shizuku、不需要无障碍服务。
///
/// 注意：已知限制（务必如实告知用户，切勿假装成功）：
///   1. 电源键的**按下时序**应用拿不到 —— Android 只在长按触发时才派发 KEYCODE_POWER。
///      所以「按到 0.1 秒白条就开始长」物理上做不到，动画只能从系统唤醒那一刻开始。
///   2. 部分定制 ROM 把长按电源键改成了开关机菜单或自家助手，且不可改（鸿蒙 4.0、ColorOS 等）。
///   3. 触发动画需要悬浮窗权限（SYSTEM_ALERT_WINDOW），用户须手动开启。
///
/// 注意：命名：MAUI 的 Application / RoleManager 会和 Android 类型撞名，一律用全限定名。
/// </summary>
public static class AssistantReplacer
{
    private static Context Ctx => global::Android.App.Application.Context;

    /// <summary>ROLE_ASSISTANT 从 Android 10（API 29）才有。</summary>
    public static bool RoleApiSupported
    {
        get
        {
            try { return Build.VERSION.SdkInt >= BuildVersionCodes.Q; }
            catch { return false; }
        }
    }

    /// <summary>跨版本取 RoleManager（绑定类型在 Android.App.Roles 下）。</summary>
    private static global::Android.App.Roles.RoleManager? GetRoleManager()
    {
        try { return Ctx.GetSystemService(Context.RoleService) as global::Android.App.Roles.RoleManager; }
        catch { return null; }
    }

    /// <summary>青阳AI 现在是不是系统的默认助手。</summary>
    public static bool IsUsDefaultAssistant()
    {
        if (!RoleApiSupported) return false;
        try
        {
            var rm = GetRoleManager();
            if (rm == null) return false;
            return rm.IsRoleHeld(global::Android.App.Roles.RoleManager.RoleAssistant)!;
        }
        catch { return false; }
    }

    /// <summary>当前默认助手是谁（包名）。拿不到时返回空串。</summary>
    public static string CurrentAssistantPackage()
    {
        try
        {
            // 绑定库里没有 GetRoleHolders，统一从 Secure 设置读 "assistant" 组件
            // 格式形如 com.foo.bar/.AssistantActivity
            var flat = global::Android.Provider.Settings.Secure.GetString(Ctx.ContentResolver, "assistant");
            if (!string.IsNullOrWhiteSpace(flat))
            {
                var slash = flat.IndexOf('/');
                return slash > 0 ? flat.Substring(0, slash) : flat;
            }
        }
        catch { }
        return "";
    }

    /// <summary>当前默认助手的显示名（查不到就用包名）。</summary>
    public static string CurrentAssistantName()
    {
        var pkg = CurrentAssistantPackage();
        if (string.IsNullOrEmpty(pkg)) return "（未知）";
        if (pkg == SelfPackage) return "青阳AI";
        try
        {
            var pm = Ctx.PackageManager;
            if (pm != null)
            {
                var ai = pm.GetApplicationInfo(pkg, 0);
                var label = pm.GetApplicationLabel(ai)?.ToString();
                if (!string.IsNullOrWhiteSpace(label)) return label!;
            }
        }
        catch { }
        return pkg;
    }

    private static string SelfPackage
    {
        get
        {
            try { return Ctx.PackageName ?? "com.qingyang.ai"; } catch { return "com.qingyang.ai"; }
        }
    }

    /// <summary>
    /// 拉起系统「选择默认助手」弹窗。用户点同意即完成替换。
    /// 返回 true 表示弹窗已成功拉起（不代表用户已同意）。
    /// </summary>
    public static bool RequestRole()
    {
        if (!RoleApiSupported) return false;
        try
        {
            var rm = GetRoleManager();
            if (rm == null) return false;
            var intent = rm.CreateRequestRoleIntent(global::Android.App.Roles.RoleManager.RoleAssistant);
            if (intent == null) return false;
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
            return true;
        }
        catch { return false; }
    }

    /// <summary>退回到系统「默认应用」设置页，作为角色弹窗拉起失败时的兜底。</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionManageDefaultAppsSettings);
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
        }
        catch
        {
            try
            {
                var intent = new Intent(global::Android.Provider.Settings.ActionApplicationDetailsSettings,
                    global::Android.Net.Uri.Parse("package:" + SelfPackage));
                intent.AddFlags(ActivityFlags.NewTask);
                Ctx.StartActivity(intent);
            }
            catch { }
        }
    }

    // ── 悬浮窗权限（画动画要用）──

    /// <summary>有没有悬浮窗权限（SYSTEM_ALERT_WINDOW）。</summary>
    public static bool HasOverlayPermission()
    {
        try { return global::Android.Provider.Settings.CanDrawOverlays(Ctx); }
        catch { return false; }
    }

    /// <summary>跳系统「显示在其他应用上层」设置页。</summary>
    public static void OpenOverlaySettings()
    {
        try
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionManageOverlayPermission,
                global::Android.Net.Uri.Parse("package:" + SelfPackage));
            intent.AddFlags(ActivityFlags.NewTask);
            Ctx.StartActivity(intent);
        }
        catch { }
    }

    // ── 诊断 ──

    /// <summary>
    /// 生成一段给人看的诊断文字：这台机器到底能不能用这个功能。
    /// 定制 ROM 的坑很多，宁可说清楚，也不让用户以为设好了。
    /// </summary>
    public static string Diagnose()
    {
        var sb = new System.Text.StringBuilder();

        string brand = "", model = "";
        try
        {
            brand = Build.Brand ?? "";
            model = Build.Model ?? "";
        }
        catch { }

        if (!RoleApiSupported)
        {
            sb.AppendLine($"本机 {SuperAdmin.AndroidVersionText}（{brand} {model}）。");
            sb.AppendLine("系统低于 Android 10，没有「默认助手」角色接口，无法一键替换。");
            sb.AppendLine("可以到「设置 → 默认应用」里手动找找看。");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"本机 {SuperAdmin.AndroidVersionText}（{brand} {model}）。");

        if (IsUsDefaultAssistant())
            sb.AppendLine("已完成：青阳AI 已是默认助手，长按电源键应能唤醒。");
        else
            sb.AppendLine($"当前默认助手：{CurrentAssistantName()}。");

        var b = brand.ToLowerInvariant();
        if (b.Contains("huawei") || b.Contains("honor") || LooksLikeHarmony())
        {
            sb.AppendLine("注意：华为/鸿蒙：长按电源键默认弹开关机菜单，");
            sb.AppendLine("  且不一定开放「默认助手」设置，替换很可能不生效。");
        }
        else if (b.Contains("oppo") || b.Contains("realme") || b.Contains("vivo")
              || b.Contains("xiaomi") || b.Contains("redmi"))
        {
            sb.AppendLine("注意：国产定制系统：长按电源键常被厂商绑定到自家语音助手，");
            sb.AppendLine("  即使设为默认助手，唤醒行为也可能不变。");
        }

        if (!HasOverlayPermission())
            sb.AppendLine("· 悬浮窗权限未开：唤醒动画画不出来（不影响助手本身）。");

        sb.AppendLine("注意：Android 不把电源键按下/松开的时序交给应用，");
        sb.AppendLine("所以「按到 0.1 秒白条就开始长」做不到，动画只能从系统唤醒那一刻开始。");

        return sb.ToString().TrimEnd();
    }

    private static bool LooksLikeHarmony()
    {
        try
        {
            foreach (var f in typeof(Build).GetFields())
            {
                if (f.FieldType != typeof(string) || !f.IsStatic) continue;
                var v = f.GetValue(null) as string;
                if (string.IsNullOrEmpty(v)) continue;
                var lv = v.ToLowerInvariant();
                if (lv.Contains("harmony") || lv.Contains("emui")) return true;
            }
        }
        catch { }
        return false;
    }
}
#endif

#if !ANDROID
/// <summary>
/// 「替换原机 AI 助手」是 Android 专属能力（依赖 RoleManager / 悬浮窗）。
/// iOS 项目的空实现：所有查询返回「不支持」，所有动作空操作，
/// 保证 ExperimentalPage 的引用在非 Android 目标也能编译、且不会假装成功。
/// </summary>
public static class AssistantReplacer
{
    public static bool RoleApiSupported => false;
    public static bool IsUsDefaultAssistant() => false;
    public static string CurrentAssistantName() => "不可用（仅 Android 支持）";
    public static bool HasOverlayPermission() => false;
    public static string Diagnose() => "此功能仅 Android 支持。";
    public static void OpenDefaultAppsSettings() { }
    public static bool RequestRole() => false;
    public static void OpenOverlaySettings() { }
}
#endif

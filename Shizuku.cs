#if ANDROID
using Rikka.Shizuku;
using Moe.Shizuku.Server;
using Android.Content;
using Android;
#endif
using System.Text;

namespace 青阳AI;

// ============================================================================
// Shizuku 能力（2026-10-29 整合）
//
// 合并自原来的四个文件：
//   ShizukuHelper.cs    —— 连接状态与权限查询
//   ShizukuLifecycle.cs —— 生命周期监听与授权结果回写
//   ShizukuRunner.cs    —— 以 shell 身份执行命令
//   CommandSecurity.cs  —— 高危命令检测
// ============================================================================

// ────────────── 原 ShizukuHelper.cs ──────────────
/// <summary>
/// Shizuku 连接状态与权限辅助。
/// </summary>
public static class ShizukuHelper
{
    public const string ShizukuPermission = "moe.shizuku.manager.permission.API_V23";

#if ANDROID
    private static bool Ping()
    {
        try { return Rikka.Shizuku.Shizuku.PingBinder(); }
        catch { return false; }
    }
#endif

    /// <summary>Shizuku adb 服务是否在线（ping 成功）。</summary>
    public static bool IsOnline
    {
        get
        {
#if ANDROID
            return Ping();
#else
            return false;
#endif
        }
    }

    public static string StatusText => IsOnline ? "online" : "offline";

    /// <summary>Shizuku 权限是否已授予。离线=必然未授权。</summary>
    public static bool IsPermissionGranted
    {
        get
        {
#if ANDROID
            if (!IsOnline) return false;
            try { return Rikka.Shizuku.Shizuku.CheckSelfPermission() == 0; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>请求 Shizuku 授权（拉起 Shizuku 授权界面）。</summary>
    public static void RequestPermission()
    {
#if ANDROID
        try
        {
            if (!IsOnline) return;
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.M)
                Rikka.Shizuku.Shizuku.RequestPermission(ShizukuRunner.PermissionRequestCode);
        }
        catch { }
#endif
    }
}

// ────────────── 原 ShizukuLifecycle.cs ──────────────
/// <summary>
/// Shizuku 生命周期与授权结果管理。
/// 在应用启动时把本应用注册进 Shizuku 的授权监听，使「青阳AI」
/// 能正常出现在 Shizuku 的授权队列并通过系统弹窗完成授权。
/// </summary>
public static class ShizukuLifecycle
{
    private static bool _initialized;

    /// <summary>是否已授予 Shizuku 权限（持久化）。</summary>
    public static bool IsGranted
    {
        get => Preferences.Default.Get("ShizukuGranted", false);
        private set => Preferences.Default.Set("ShizukuGranted", value);
    }

    /// <summary>在应用启动时调用一次，注册 Shizuku 监听。</summary>
    public static void Init()
    {
        if (_initialized) return;
        _initialized = true;

#if ANDROID
        try
        {
            // 1) 监听 binder 就绪：Shizuku 服务已连接
            Rikka.Shizuku.Shizuku.AddBinderReceivedListenerSticky(
                new BinderListener());

            // 2) 监听授权结果：用户通过系统弹窗授权后写回状态
            Rikka.Shizuku.Shizuku.AddRequestPermissionResultListener(
                new PermissionListener());
        }
        catch { }
#endif
    }

#if ANDROID
    /// <summary>Shizuku binder 就绪回调。</summary>
    private sealed class BinderListener : Java.Lang.Object,
        Rikka.Shizuku.Shizuku.IOnBinderReceivedListener
    {
        public void OnBinderReceived()
        {
            // binder 到达，Shizuku 可 ping 通
        }
    }

    /// <summary>授权结果回调。</summary>
    private sealed class PermissionListener : Java.Lang.Object,
        Rikka.Shizuku.Shizuku.IOnRequestPermissionResultListener
    {
        public void OnRequestPermissionResult(int requestCode, int grantResult)
        {
            // 后台线程回调：切到主线程更新状态
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // 0 = 允许；-1 = 拒绝
                IsGranted = grantResult == 0;
                if (requestCode == ShizukuRunner.PermissionRequestCode)
                    ShizukuRunner.OnPermissionResult(grantResult == 0);
            });
        }
    }
#endif
}

// ────────────── 原 ShizukuRunner.cs ──────────────
#if ANDROID
#endif


/// <summary>
/// 通过 Shizuku 执行本地 shell 命令。
/// </summary>
public static class ShizukuRunner
{
    #if ANDROID
private static IShizukuService? _service;
#else
private static object? _service;
#endif

    /// <summary>Shizuku 授权请求码。</summary>
    public const int PermissionRequestCode = 100;

    /// <summary>授权结果回调（由 ShizukuLifecycle 触发）。</summary>
    public static void OnPermissionResult(bool granted)
    {
        // 授权后刷新 _service，下次执行直接用新 binder
        if (!granted) _service = null;
    }

    public sealed class Result
    {
        public int ExitCode { get; set; } = -1;
        public string Stdout { get; set; } = "";
        public string Stderr { get; set; } = "";
        public bool Ok => ExitCode == 0;
    }

    /// <summary>Shizuku 是否在线且已授权。</summary>
    public static bool Available()
    {
#if ANDROID
        try
        {
            return Rikka.Shizuku.Shizuku.PingBinder()
                && Rikka.Shizuku.Shizuku.CheckSelfPermission() == 0;
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>拉起 Shizuku 授权界面。</summary>
    public static void RequestPermission()
    {
#if ANDROID
        try { Rikka.Shizuku.Shizuku.RequestPermission(PermissionRequestCode); } catch { }
#endif
    }

    private static bool Bind()
    {
#if ANDROID
        if (_service != null) return true;
        try
        {
            var binder = Rikka.Shizuku.Shizuku.Binder;
            if (binder == null) return false;
            _service = IShizukuService.Stub.AsInterface(binder);
            return _service != null;
        }
        catch { return false; }
#else
        _service = null;
        return false;
#endif
    }

    /// <summary>
    /// 以 shell 身份执行一条命令。
    /// </summary>
    /// <param name="timeoutMs">超时毫秒（&lt;=0 表示不超时）。超时会 Destroy 进程并返回 ExitCode = -2。</param>
    public static async Task<Result> ExecuteAsync(string command, int timeoutMs = 0)
    {
        var r = new Result();
#if ANDROID
        try
        {
            if (!Bind())
            {
                r.Stderr = "Shizuku 未连接或未授权";
                return r;
            }

            var process = _service.NewProcess(
                new[] { "/system/bin/sh", "-c", command },
                null,
                null);

            if (process == null)
            {
                r.Stderr = "无法创建进程";
                return r;
            }

            // 注意：stdout / stderr 必须**并行**读。
            //    顺序 await 会踩经典 pipe deadlock：若进程先写满 stderr 管道（默认 64KB），
            //    而我们在等 stdout，进程就会永久阻塞在 write(stderr) 上 —— 命令再也不退出。
            var stdoutTask = ReadFdAsync(process.InputStream);
            var stderrTask = ReadFdAsync(process.ErrorStream);

            // 注意：WaitFor() 是**同步阻塞**的 binder 调用。
            //    MAUI 里 await 之后的 continuation 会回到 UI 线程，直接调它会卡死界面
            //    （下载/解压动辄几分钟）→ 必须丢到线程池。
            var waitTask = Task.Run(() => process.WaitFor());

            if (timeoutMs > 0)
            {
                var first = await Task.WhenAny(waitTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (first != waitTask)
                {
                    try { process.Destroy(); } catch { }
                    r.Stdout = await AwaitOrEmpty(stdoutTask).ConfigureAwait(false);
                    var se = await AwaitOrEmpty(stderrTask).ConfigureAwait(false);
                    r.ExitCode = -2;
                    r.Stderr = $"命令超时（{timeoutMs / 1000} 秒）"
                             + (se.Trim().Length > 0 ? "：" + se.Trim() : "");
                    return r;
                }
            }

            r.Stdout = await stdoutTask.ConfigureAwait(false);
            r.Stderr = await stderrTask.ConfigureAwait(false);
            r.ExitCode = await waitTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            r.Stderr = ex.Message;
        }
#else
        r.Stderr = "仅 Android 支持 Shizuku";
#endif
        return r;
    }

#if ANDROID
    /// <summary>等一个读流任务，最多 ms 毫秒；超时或异常返回空串（进程被 Destroy 后兜底用）。</summary>
    private static async Task<string> AwaitOrEmpty(Task<string> t, int ms = 3000)
    {
        var done = await Task.WhenAny(t, Task.Delay(ms)).ConfigureAwait(false);
        if (done != t) return "";
        try { return await t.ConfigureAwait(false); } catch { return ""; }
    }

    private static Task<string> ReadFdAsync(Android.OS.ParcelFileDescriptor? fd)
    {
        return Task.Run(() =>
        {
            if (fd == null) return "";
            try
            {
                using var input = new Android.OS.ParcelFileDescriptor.AutoCloseInputStream(fd);
                var memory = new MemoryStream();
                var buffer = new byte[8192];
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    memory.Write(buffer, 0, n);
                return Encoding.UTF8.GetString(memory.ToArray());
            }
            catch { return ""; }
        });
    }
#endif
}

// ────────────── 原 CommandSecurity.cs ──────────────
/// <summary>
/// 高危命令检测。用于对用户即将通过 Shizuku 执行的命令做安全判定。
/// </summary>
public static class CommandSecurity
{
   
    private static readonly string[] BlockedPrefixes =
    {
        "rm -rf /", "rm -fr /", "rm -r /", "format", "mkfs", "dd if=/dev/zero",
        "reboot", "shutdown", "poweroff", "halt", "wipe", "fastboot erase", "fastboot flash",
    };

  
    private static readonly string[] DangerousTokens =
    {
        "rm -rf", "rm -fr", "chmod 777", "chmod 666", "pm uninstall", "pm clear",
        "settings clear", "settings delete", "content delete", "iplist", "iptables -F",
        "svc wifi disable", "svc data disable", "mount -o rw", "su -c", "dd ",
    };

    /// <summary>是否是高危命令（需要 5 秒倒计时确认）。</summary>
    public static bool IsHighRisk(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var c = command.Trim().ToLowerInvariant();
        foreach (var p in BlockedPrefixes)
            if (c.StartsWith(p, System.StringComparison.Ordinal)) return true;
        foreach (var t in DangerousTokens)
            if (c.Contains(t, System.StringComparison.Ordinal)) return true;
        return false;
    }
}


using System.Runtime.InteropServices;

namespace 青阳AI;

// ============================================================================
// iOS JIT 检测 / 获取引导（QingYangAIiOS 项目专用）
//
// 现实约束（重要）：
//   iOS 不允许 App 自行开启 JIT —— 必须由外部调试器（AltStore / AltServer /
//   StikDebug 等）在 App 运行时附加才能启用。因此 App 能做的只有三件事：
//     1) 检测当前进程是否已有 JIT 权限（尝试 mmap 可执行内存并执行）；
//     2) 没有 JIT 时，尝试唤起本机已安装的外部工具 scheme（自动获取）；
//     3) 唤起失败 / 没装工具时，弹窗明确指导用户去 AltStore 开 AltJIT。
//
// 参考：AltStore AltJIT 官方说明 https://faq.altstore.io/altstore-classic/enabling-jit/altjit
// 一旦启用，JIT 会一直有效，直到 App 被强制退出或后台被回收。
// ============================================================================

internal static class JitService
{
    /// <summary>是否已成功引导过 JIT（本次进程内避免重复弹窗）。</summary>
    private static bool _prompted;

    /// <summary>外部工具的 URL scheme（按优先级：能唤起就唤起）。</summary>
    private static readonly string[] HelperSchemes =
    {
        "stikdebug://",          // StikDebug（iOS 17.4+ 设备端 JIT 启用器）
        "altstore://",           // AltStore（长按 App → Enable JIT）
    };

    /// <summary>
    /// 检测当前进程是否具备 JIT（动态代码可执行）能力。
    /// 原理：mmap 一页 RW 内存 → 写入一段「返回 1」的机器码 → mprotect 改 RX → 调用。
    /// 若系统禁止 JIT，mprotect(PROT_EXEC) 会失败或调用触发崩溃，返回 false。
    /// </summary>
    private static bool DetectJit()
    {
#if IOS
        try { return JitProbe.CanExecuteDynamicCode(); }
        catch { return false; }
#else
        return true;   // 非 iOS（如模拟器/其它平台）：视为已具备，不打扰
#endif
    }

    /// <summary>
    /// 启动时调用：自动获取 JIT。
    /// 已启用 → 静默返回 true；
    /// 未启用 → 依次尝试唤起外部工具 scheme（自动获取），随后弹窗提示 AltJIT。
    /// </summary>
    internal static async Task<bool> EnsureJitAsync(Page host)
    {
#if IOS
        if (DetectJit()) return true;

        // 防止同一次进程反复弹窗打扰
        if (_prompted) return false;
        _prompted = true;

        // 1) 自动获取：尝试唤起本机已安装的 JIT 工具（能否成功由系统决定，App 无法预知）
        foreach (var scheme in HelperSchemes)
        {
            try
            {
                var uri = new Uri(scheme);
                if (await Launcher.CanOpenAsync(uri))
                {
                    await Launcher.OpenAsync(uri);
                    return false;   // 已唤起工具，用户按工具指引开启；不重复弹窗
                }
            }
            catch { /* 该工具没装或 scheme 无效，试下一个 */ }
        }

        // 2) 没有可唤起的工具 → 弹窗，明确指导 AltJIT
        await host.Dispatcher.DispatchAsync(async () =>
        {
            await host.DisplayAlertAsync(
                "需要开启 JIT",
                "为让 AI 模型流畅运行，本 App 需要 JIT 权限。\n\n" +
                "请在 AltStore 的「My Apps」中长按「青阳AI」→「Enable JIT」，" +
                "然后保持本 App 在前台。\n\n" +
                "（iOS 17+ 需在 Mac 上用 AltServer + pymobiledevice3 开启）",
                "我知道了", "稍后");
        });
        return false;
#else
        return true;
#endif
    }
}

#if IOS
/// <summary>
/// 用 mmap/mprotect 探测动态代码是否可执行（即是否有 JIT 权限）。
/// 全部 native 调用只在 iOS 目标编译。
/// </summary>
internal static class JitProbe
{
    private const int PROT_READ = 0x1;
    private const int PROT_WRITE = 0x2;
    private const int PROT_EXEC = 0x4;
    private const int MAP_PRIVATE = 0x02;
    private const int MAP_ANON = 0x1000;
    private const int PAGE_SIZE = 16384;   // Apple Silicon 页大小 16KB

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr mmap(IntPtr addr, nuint length, int prot, int flags, int fd, nint offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, nuint length, int prot);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(IntPtr addr, nuint length);

    public static bool CanExecuteDynamicCode()
    {
        IntPtr mem = mmap(IntPtr.Zero, PAGE_SIZE, PROT_READ | PROT_WRITE,
            MAP_PRIVATE | MAP_ANON, -1, 0);
        if (mem == new IntPtr(-1) || mem == IntPtr.Zero) return false;

        try
        {
            // 写入 ARM64 指令：mov w0, #1 ; ret  → 0xD2800020 0xD65F03C0
            byte[] code =
            {
                0x20, 0x00, 0x80, 0xD2,   // mov w0, #1
                0xC0, 0x03, 0x5F, 0xD6,   // ret
            };
            Marshal.Copy(code, 0, mem, code.Length);

            // 改为只读+可执行：JIT 被禁时 mprotect 会失败
            if (mprotect(mem, PAGE_SIZE, PROT_READ | PROT_EXEC) != 0) return false;

            var func = Marshal.GetDelegateForFunctionPointer<Fn>(mem);
            return func() == 1;
        }
        catch { return false; }
        finally { munmap(mem, PAGE_SIZE); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Fn();
}
#endif

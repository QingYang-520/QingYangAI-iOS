using System.Text;
using MauiPermissions = Microsoft.Maui.ApplicationModel.Permissions;
using MauiPermissionStatus = Microsoft.Maui.ApplicationModel.PermissionStatus;

namespace 青阳AI;

// ============================================================================
// 文件系统与存储权限（2026-10-29 整合）
//
// 合并自原来的四个文件：
//   FileGuard.cs      —— 文件操作的多道闸门（权限判定 + 路径越界防护）
//   FileTool.cs       —— 读写/列目录/删除的实际执行与结果文案
//   StorageAccess.cs  —— 真实存储权限 + 公共工作区路径的唯一入口
//   SuperAdmin.cs     —— 深度系统权限（全盘访问 / 设备管理器）跳转
// ============================================================================

// ────────────── 原 FileGuard.cs ──────────────
/// <summary>
/// 文件访问闸门：所有文件操作的第一道（也是最硬的一道）关卡。
///
/// 设计原则：
/// 1. 权限级别由用户在聊天页盾牌气泡里显式选择，模型无法自行提权；
/// 2. 路径先规范化（解析 ../ 与符号链接的父目录），再判定是否越界——
///    绝不允许用 "workspace/../../etc" 这种方式绕出去；
/// 3. 系统关键目录即使"完全访问"也默认拦截（可被 FullAccess 覆盖，但会留下警告）；
/// 4. 任何拒绝都返回可读的中文说明，交回模型时模型能理解并换路子。
/// </summary>
public static class FileGuard
{
    /// <summary>系统关键目录（读也拦，避免 AI 误读敏感数据；写更不用说）。</summary>
    private static readonly string[] ForbiddenPrefixes =
    {
        "/proc", "/sys", "/dev", "/system", "/vendor", "/data/system",
        "/data/misc", "/data/adb", "/data/local/tmp",
    };

    private sealed class GuardResult
    {
        public bool Allowed;
        public string Reason = "";
        public static GuardResult Ok() => new() { Allowed = true };
        public static GuardResult No(string reason) => new() { Allowed = false, Reason = reason };
    }

    /// <summary>
    /// 把相对路径解析成绝对路径。
    /// 注意：这里只做字符串层面的规范化（Path.GetFullPath 会解析 . 和 ..），
    /// 不走 File.ResolveLinkTarget（Android 上对部分路径会抛异常）。
    /// </summary>
    public static string Resolve(string path)
    {
        path = (path ?? "").Trim().Trim('"');
        if (string.IsNullOrEmpty(path)) return AppSettings.EffectiveWorkspacePath;

        // 展开 ~ 为工作区（给模型一个友好的简写）
        if (path == "~" || path.StartsWith("~/") || path.StartsWith("~\\"))
            path = Path.Combine(AppSettings.EffectiveWorkspacePath, path.Length > 2 ? path[2..] : "");

        try
        {
            // 相对路径一律按工作区解析。不能交给 Path.GetFullPath 用进程当前目录兜底——
            // Android 上进程 CWD 是 /，会把 "notes.txt" 解析成 /notes.txt 这个意想不到的位置。
            if (!Path.IsPathRooted(path))
                path = Path.Combine(AppSettings.EffectiveWorkspacePath, path);
            return Path.GetFullPath(path);
        }
        catch
        {
            // 非法字符等：原样返回，后续 Exists 检查会自然失败
            return path;
        }
    }

    /// <summary>读权限校验。</summary>
    public static (bool Allowed, string Reason) CheckRead(string path) => Check(path, isWrite: false);

    /// <summary>写权限校验。</summary>
    public static (bool Allowed, string Reason) CheckWrite(string path) => Check(path, isWrite: true);

    private static (bool Allowed, string Reason) Check(string path, bool isWrite)
    {
        var level = AppSettings.FileAccessLevel;
        var fullAccess = AppSettings.FullAccess;

        // 0 = 未授权：任何文件操作都不行
        if (level == 0 && !fullAccess)
            return (false, "现在没有文件访问权限。如果用户希望我读写文件，请在聊天页顶部的盾牌图标里选择权限级别。");

        var full = Resolve(path);
        var workspace = AppSettings.EffectiveWorkspacePath;

        // —— 系统关键目录拦截（完全访问可越过，但要提示）——
        if (!fullAccess)
        {
            foreach (var p in ForbiddenPrefixes)
            {
                if (full.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return (false, $"系统目录 {p} 不允许访问。");
            }
        }

        bool inWorkspace = IsUnder(full, workspace);
        bool workspaceWritable = level == 2;
        bool diskReadable = level == 3 || level == 4;
        bool diskWritable = level == 4;

        if (isWrite)
        {
            if (inWorkspace)
            {
                // 工作区内：级别 2（仅改工作区）/ 3（读全盘时也允许改工作区）/ 4 都可写
                if (workspaceWritable || diskReadable || diskWritable || fullAccess) return (true, "");
                return (false, $"当前权限是「{AppSettings.FileAccessDesc}」，只能读取工作区文件，不能修改。");
            }
            if (diskWritable || fullAccess)
                return GateDisk(full, isWrite: true);
            return (false, $"当前权限是「{AppSettings.FileAccessDesc}」，不能修改工作区以外的文件。请让用户在盾牌图标里选择「修改全盘文件」或开启「完全访问」。");
        }
        else
        {
            if (inWorkspace) return (true, "");   // 有任意文件权限时，工作区永远可读
            if (diskReadable || diskWritable || fullAccess)
                return GateDisk(full, isWrite: false);
            return (false, $"当前权限是「{AppSettings.FileAccessDesc}」，只能读取工作区文件。请让用户在盾牌图标里选择「读取全盘文件」。");
        }
    }

    /// <summary>
    /// 越出工作区时的第二道闸门：校验 Android 系统层的真实存储权限。
    ///
    /// APP 内的权限等级只代表"用户允许 Ta 这么做"，但真正能不能写进 /storage/emulated/0
    /// 取决于系统是否授予 MANAGE_EXTERNAL_STORAGE。这里提前拦下并给出可执行的引导，
    /// 避免模型一路撞到 UnauthorizedAccessException 才发现，白费轮次。
    /// </summary>
    private static (bool Allowed, string Reason) GateDisk(string full, bool isWrite)
    {
        // 其他 APP 的私有目录：即使有全盘权限也读不到（Android 沙盒硬限制），提前说清楚
        if (full.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
        {
            bool isOurs = false;
            try
            {
                var appDir = Microsoft.Maui.Storage.FileSystem.AppDataDirectory;
                isOurs = !string.IsNullOrEmpty(appDir) && IsUnder(full, appDir);
            }
            catch { }

            if (!isOurs)
                return (false, "这是其他应用的私有目录，Android 沙盒不允许任何应用访问（全盘权限也不行）。"
                               + "只能访问公共存储（内部存储）里的文件。");
        }

        // 工作区以外的路径，必须真有全盘读写能力（系统版本不同，叫法/判定也不同）
        if (!StorageAccess.IsAllFilesGranted())
            return (false, "系统还没有授予" + StorageAccess.PermissionLabel + "，无法读写公共存储。"
                           + "请让用户在聊天页盾牌面板里点「" + StorageAccess.GrantButtonText + "」；"
                           + "在那之前，只能读写工作区内的文件。");

        return (true, "");
    }

    /// <summary>child 是否在 parent 目录之下（大小写不敏感，带分隔符边界判断）。</summary>
    public static bool IsUnder(string child, string parent)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(parent)) return false;
            var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, '/');
            var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, '/');
            if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
            return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(p + '/', StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>给模型看的能力说明文本（拼进 system 提示词）。</summary>
    public static string BuildCapabilityText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("【文件能力】");
        sb.AppendLine($"当前权限：{AppSettings.FileAccessDesc}"
                      + (AppSettings.FullAccess ? "（已开启完全访问，限制全部放开）" : ""));
        var ws = AppSettings.EffectiveWorkspacePath;
        sb.AppendLine($"工作区目录：{ws}");
        sb.AppendLine($"用户看到的路径：{StorageAccess.ToDisplay(ws)}");
        sb.AppendLine("  {ls:\"路径\"} — 列出目录内容");
        sb.AppendLine("  {read:\"路径\"} — 读取文本文件");
        sb.AppendLine("  {write:\"路径|完整内容\"} — 覆盖写入文件（不存在则创建）");
        sb.AppendLine("  {append:\"路径|追加内容\"} — 追加到文件末尾");
        sb.AppendLine("  {edit:\"路径|原文|新内容\"} — 把文件里的原文替换成新内容（修改文件的首选，比整文件覆写安全）");
        sb.AppendLine("  {del:\"路径\"} — 删除文件（高风险，只有「修改全盘文件」或「完全访问」才可用）");
        sb.AppendLine("路径支持相对写法（相对工作区），也可以用 ~ 代表工作区。/ 开头是绝对路径。"
                      + "相对路径直接写文件名即可，例如 {read:\"notes.txt\"}。");
        sb.AppendLine("修改代码类文件时优先用 {edit:...} 做精确替换，不要整文件覆写。");
        if (!AppSettings.FullAccess && AppSettings.FileAccessLevel == 0)
            sb.AppendLine("注意：用户尚未授予文件权限，上面这些指令会被拒绝。"
                          + "需要文件能力时，先告诉用户在聊天页顶部的盾牌图标里开启。");
        sb.AppendLine();
        // 存储实况：让模型知道文件写到哪、用户找不找得到
        sb.Append(StorageAccess.BuildStorageText());
        return sb.ToString();
    }
}

// ────────────── 原 FileTool.cs ──────────────
/// <summary>
/// AI 的文件工具：读 / 写 / 追加 / 替换 / 列目录 / 删除。
/// 所有操作都先过权限闸门（<see cref="FileGuard"/>），越界直接拒绝并返回说明文字，
/// 不抛异常给上层——调用方永远拿到一段可读文本。
/// </summary>
public static class FileTool
{
    /// <summary>单次读取的最大字符数（防止把整个大文件灌进上下文）。</summary>
    public const int MaxReadChars = 20000;

    /// <summary>单次列目录最多返回多少个条目。</summary>
    public const int MaxListEntries = 200;

    // ─────────── 主要操作 ───────────

    /// <summary>读取文本文件。返回内容或拒绝/错误说明。</summary>
    public static async Task<string> ReadAsync(string path)
    {
        var check = FileGuard.CheckRead(path);
        if (!check.Allowed) return check.Reason;

        try
        {
            var full = FileGuard.Resolve(path);
            if (!File.Exists(full)) return $"文件不存在：{path}";
            var info = new FileInfo(full);
            if (info.Length > 2 * 1024 * 1024) return $"文件太大（{info.Length / 1024 / 1024}MB），暂不支持读取。";

            var text = await File.ReadAllTextAsync(full, Encoding.UTF8);
            if (text.Length > MaxReadChars)
                text = text[..MaxReadChars] + $"\n\n…（已截断，全文共 {text.Length} 字）";
            return $"已读取 {StorageAccess.ToDisplay(full)}（{info.Length} 字节）：\n\n{text}";
        }
        catch (Exception ex)
        {
            return $"读取失败：{ex.Message}";
        }
    }

    /// <summary>写入（覆盖）文本文件，自动建目录。返回结果说明 + 行数变化。</summary>
    public static async Task<FileOpResult> WriteAsync(string path, string content)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return FileOpResult.Denied(check.Reason);

        try
        {
            var full = FileGuard.Resolve(path);
            var oldLines = File.Exists(full) ? CountLines(await File.ReadAllTextAsync(full, Encoding.UTF8)) : 0;
            var newLines = CountLines(content);

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, content, Encoding.UTF8);

            var delta = newLines - oldLines;
            return new FileOpResult
            {
                Success = true,
                Added = Math.Max(0, delta),
                Removed = Math.Max(0, -delta),
                Message = $"已写入 {StorageAccess.ToDisplay(full)}（{newLines} 行）"
            };
        }
        catch (Exception ex)
        {
            return FileOpResult.Failed($"写入失败：{ex.Message}");
        }
    }

    /// <summary>把文本追加到文件末尾（不存在则创建）。</summary>
    public static async Task<FileOpResult> AppendAsync(string path, string content)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return FileOpResult.Denied(check.Reason);

        try
        {
            var full = FileGuard.Resolve(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.AppendAllTextAsync(full, content, Encoding.UTF8);
            var added = CountLines(content);
            return new FileOpResult
            {
                Success = true,
                Added = added,
                Message = $"已追加到 {StorageAccess.ToDisplay(full)}（+{added} 行）"
            };
        }
        catch (Exception ex)
        {
            return FileOpResult.Failed($"追加失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 精确替换文件中的一段文本（oldText → newText），只替换第一处。
    /// 这是 AI 修改代码的主要手段：比整文件覆写安全得多。
    /// </summary>
    public static async Task<FileOpResult> EditAsync(string path, string oldText, string newText)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return FileOpResult.Denied(check.Reason);

        try
        {
            var full = FileGuard.Resolve(path);
            if (!File.Exists(full)) return FileOpResult.Failed($"文件不存在：{path}");

            var text = await File.ReadAllTextAsync(full, Encoding.UTF8);
            var idx = text.IndexOf(oldText, StringComparison.Ordinal);
            if (idx < 0)
                return FileOpResult.Failed($"在 {path} 里没找到要替换的内容（请确认原文完全一致）。");

            var updated = text[..idx] + newText + text[(idx + oldText.Length)..];
            await File.WriteAllTextAsync(full, updated, Encoding.UTF8);

            var added = CountLines(newText);
            var removed = CountLines(oldText);
            return new FileOpResult
            {
                Success = true,
                Added = added,
                Removed = removed,
                Message = $"已编辑 {StorageAccess.ToDisplay(full)}（+{added} -{removed} 行）"
            };
        }
        catch (Exception ex)
        {
            return FileOpResult.Failed($"编辑失败：{ex.Message}");
        }
    }

    /// <summary>列出目录内容（目录在前，含文件大小）。</summary>
    public static async Task<string> ListAsync(string path)
    {
        var check = FileGuard.CheckRead(path);
        if (!check.Allowed) return check.Reason;

        try
        {
            var full = FileGuard.Resolve(path);
            if (!Directory.Exists(full)) return $"目录不存在：{path}";

            var dirs = Directory.GetDirectories(full).OrderBy(d => d).ToList();
            var files = Directory.GetFiles(full).OrderBy(f => f).ToList();

            var sb = new StringBuilder($"目录 {path}（{dirs.Count} 个子目录 / {files.Count} 个文件）：\n");
            int count = 0;
            foreach (var d in dirs)
            {
                if (count++ >= MaxListEntries) { sb.AppendLine("…（条目过多，已截断）"); break; }
                sb.AppendLine("[目录] " + Path.GetFileName(d) + "/");
            }
            foreach (var f in files)
            {
                if (count++ >= MaxListEntries) { sb.AppendLine("…（条目过多，已截断）"); break; }
                try
                {
                    var len = new FileInfo(f).Length;
                    sb.AppendLine($"[文件] {Path.GetFileName(f)}  ({len} 字节)");
                }
                catch { sb.AppendLine("[文件] " + Path.GetFileName(f)); }
            }
            return await Task.FromResult(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return $"列目录失败：{ex.Message}";
        }
    }

    /// <summary>删除文件。注意：高风险：完全访问除外，一律要求调用方二次确认。</summary>
    public static Task<FileOpResult> DeleteAsync(string path)
    {
        var check = FileGuard.CheckWrite(path);
        if (!check.Allowed) return Task.FromResult(FileOpResult.Denied(check.Reason));

        try
        {
            var full = FileGuard.Resolve(path);
            if (!File.Exists(full)) return Task.FromResult(FileOpResult.Failed($"文件不存在：{path}"));

            // 删除前记下原行数，用于统计"减少了多少行"
            var removed = CountLines(File.ReadAllText(full, Encoding.UTF8));
            File.Delete(full);
            return Task.FromResult(new FileOpResult
            {
                Success = true,
                Removed = removed,
                Message = $"已删除 {StorageAccess.ToDisplay(full)}（-{removed} 行）"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOpResult.Failed($"删除失败：{ex.Message}"));
        }
    }

    /// <summary>统计行数（空串 = 0 行）。</summary>
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 1;
        foreach (var c in text)
            if (c == '\n') n++;
        // 以换行结尾时不算多出最后一行
        return text.EndsWith('\n') ? n - 1 : n;
    }
}

/// <summary>文件操作结果：成功与否 + 行数增减（供缩略行显示 +N/-N，并汇总）。</summary>
public sealed class FileOpResult
{
    public bool Success { get; set; }
    /// <summary>新增行数（绿色 +N）。</summary>
    public int Added { get; set; }
    /// <summary>删除行数（红色 -N）。</summary>
    public int Removed { get; set; }
    /// <summary>给模型/用户看的说明文字。</summary>
    public string Message { get; set; } = "";

    public static FileOpResult Denied(string reason) => new() { Success = false, Message = reason };
    public static FileOpResult Failed(string msg) => new() { Success = false, Message = msg };
}

// ────────────── 原 StorageAccess.cs ──────────────
// 全限定别名：Android 目标的隐式 using 里也有 Permission/PermissionStatus 相关类型，避免歧义


/// <summary>
/// 真实存储权限 + 公共存储路径的唯一入口。
///
/// 为什么需要这一层：
/// 之前「完全访问」只是 APP 内部的虚拟开关——它只决定 FileGuard 放不放行，
/// 但 Android 系统层面并没有授予 MANAGE_EXTERNAL_STORAGE，于是写 /storage/emulated/0
/// 会直接抛 UnauthorizedAccessException；而且默认工作区落在 app 私有沙盒
/// （/data/user/0/包名/files/workspace），用户在文件管理器里根本看不到生成的文件。
///
/// 本类负责：
///   1. 查询/申请系统「所有文件访问」权限；
///   2. 给出公共存储（内部存储）下的默认工作区；
///   3. 把内部绝对路径翻译成人能看懂的路径；
///   4. 把沙盒里的文件一键搬运到公共 Download。
/// </summary>
public static class StorageAccess
{
    /// <summary>公共存储根：Android 上是 /storage/emulated/0（即"内部存储"）。</summary>
    public static string PublicRoot
    {
        get
        {
#if ANDROID
            try
            {
                var dir = Android.OS.Environment.ExternalStorageDirectory;
                if (dir?.AbsolutePath is { Length: > 0 } p) return p.TrimEnd('/');
            }
            catch { }
            return "/storage/emulated/0";
#else
            return "";
#endif
        }
    }

    /// <summary>公共 Download 目录。</summary>
    public static string PublicDownloads
    {
        get
        {
#if ANDROID
            try
            {
                var dir = Android.OS.Environment.GetExternalStoragePublicDirectory(
                    Android.OS.Environment.DirectoryDownloads);
                if (dir?.AbsolutePath is { Length: > 0 } p) return p.TrimEnd('/');
            }
            catch { }
#endif
            var root = PublicRoot;
            return string.IsNullOrEmpty(root) ? "" : Path.Combine(root, "Download");
        }
    }

    /// <summary>我们自己的公共存储根目录名：内部存储/QingYangAI。</summary>
    public const string AppFolderName = "QingYangAI";

    /// <summary>默认工作区子目录名。</summary>
    public const string WorkspaceFolderName = "WorkSpace";

    /// <summary>
    /// 授予全盘权限后，默认工作区落在公共存储：
    /// /storage/emulated/0/QingYangAI/WorkSpace —— 文件管理器里能直接看到。
    ///
    /// 这是一个"约定路径"（纯字符串拼接，不碰磁盘），
    /// 真正建目录由 AppSettings.EnsureDefaultWorkspace() 触发。
    /// </summary>
    public static string DefaultPublicWorkspace
    {
        get
        {
            var root = PublicRoot;
            if (string.IsNullOrEmpty(root)) return "";
            // 注意：这里刻意不用 Path.Combine 逐段拼，避免不同平台分隔符差异导致
            // 提示词里露出的路径和用户在文件管理器看到的不一致
            return root.TrimEnd('/') + "/" + AppFolderName + "/" + WorkspaceFolderName;
        }
    }

    /// <summary>
    /// 逐级创建默认工作区目录。中间任何一级缺失都会自动补建。
    /// 返回是否可用（失败不抛异常，交给调用方决定怎么提示）。
    /// </summary>
    public static bool EnsureDefaultWorkspace(out string reason)
    {
        reason = "";
        var target = DefaultPublicWorkspace;
        if (string.IsNullOrEmpty(target))
        {
            reason = "无法解析公共存储路径。";
            return false;
        }
        if (!IsAllFilesGranted())
        {
            reason = "尚未授予系统「所有文件访问」权限。";
            return false;
        }

        try
        {
            // Directory.CreateDirectory 本身就会逐级建好缺失的父目录，
            // 但这里显式分两级，方便在某一级失败时给出准确原因。
            var root = PublicRoot;
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
            {
                reason = $"公共存储根目录不存在：{root}";
                return false;
            }

            var appDir = Path.Combine(root, AppFolderName);
            Directory.CreateDirectory(appDir);

            Directory.CreateDirectory(target);

            if (!Directory.Exists(target))
            {
                reason = "目录创建后仍不存在，可能是存储不可写。";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = "创建目录失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>默认工作区当前是否已经真实存在。</summary>
    public static bool DefaultWorkspaceExists()
    {
        var target = DefaultPublicWorkspace;
        if (string.IsNullOrEmpty(target)) return false;
        try { return Directory.Exists(target); }
        catch { return false; }
    }

    // ─────────── 系统权限 ───────────

    /// <summary>
    /// 是否已获得全盘读写能力。
    ///
    /// ① 先问系统：Android 11+ 看「所有文件访问」（MANAGE_EXTERNAL_STORAGE）；
    ///    Android 10 及以下没有那个开关，看普通存储权限（配 requestLegacyExternalStorage）。
    /// ② 系统说"没有"时，再做一次**真实写入探测** ——
    ///    因为部分 ROM（实测：鸿蒙）的系统开关和实际能力对不上：
    ///    用户明明已经在系统设置里给了权限，开关却还是 false，
    ///    结果「完全访问」被永久锁死，点了没反应。
    ///    与其信开关，不如直接试着写一下。
    /// </summary>
    public static bool IsAllFilesGranted()
        => SuperAdmin.IsStorageGranted() || ProbePublicWritable();

    private static bool? _probeOk;
    private static DateTime _probeAt;

    /// <summary>
    /// 去公共存储根目录写一个探针文件再删掉 —— 这是"到底能不能写"的最终事实，
    /// 不依赖任何 ROM 的权限开关实现。结果缓存 10 秒，避免每次刷面板都做 I/O。
    /// </summary>
    private static bool ProbePublicWritable()
    {
        if (_probeOk.HasValue && (DateTime.UtcNow - _probeAt).TotalSeconds < 10)
            return _probeOk.Value;

        bool ok = false;
        try
        {
            var root = PublicRoot;
            if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
            {
                var probe = root.TrimEnd('/') + "/.qy_write_probe";
                File.WriteAllText(probe, "1");
                File.Delete(probe);
                ok = true;
            }
        }
        catch { ok = false; }

        _probeOk = ok;
        _probeAt = DateTime.UtcNow;
        return ok;
    }

    /// <summary>权限状态可能变了（刚授权回来）时清掉探测缓存。</summary>
    public static void InvalidateProbe() { _probeOk = null; }

    /// <summary>这个权限在界面上叫什么（不同系统版本叫法不一样）。</summary>
    public static string PermissionLabel
        => SuperAdmin.HasAllFilesAccessApi ? "系统「所有文件访问」" : "系统「存储」权限";

    /// <summary>「去开启」按钮上的文案。</summary>
    public static string GrantButtonText
        => SuperAdmin.HasAllFilesAccessApi ? "去开启「所有文件访问」" : "去授予存储权限";

    /// <summary>
    /// 引导用户拿到全盘读写能力。
    /// Android 11+ ：跳系统「所有文件访问」页面；
    /// Android 10- ：系统里根本没有那个页面，直接申请运行时存储权限；
    ///               被永久拒绝时再跳到本应用的系统详情页让用户手动开。
    /// 返回调用后是否已经拿到权限。
    /// </summary>
    public static async Task<bool> RequestPermissionAsync()
    {
        if (IsAllFilesGranted()) return true;

        if (SuperAdmin.HasAllFilesAccessApi)
        {
            SuperAdmin.OpenStorageSettings();
            return false;
        }

        try
        {
            var w = await MauiPermissions.CheckStatusAsync<MauiPermissions.StorageWrite>();
            if (w != MauiPermissionStatus.Granted)
                w = await MauiPermissions.RequestAsync<MauiPermissions.StorageWrite>();

            var r = await MauiPermissions.CheckStatusAsync<MauiPermissions.StorageRead>();
            if (r != MauiPermissionStatus.Granted)
                r = await MauiPermissions.RequestAsync<MauiPermissions.StorageRead>();

            if (w == MauiPermissionStatus.Granted || r == MauiPermissionStatus.Granted) return true;

            // 用户选了「不再询问」之类 → 只能去应用详情页手动开
            SuperAdmin.OpenAppSettings();
        }
        catch { }
        return false;
    }

    /// <summary>不等待结果的版本（兼容旧的 void 调用点）。</summary>
    public static void RequestPermission() { _ = RequestPermissionAsync(); }

    // ─────────── 路径识别与显示 ───────────

    /// <summary>该路径是否位于 APP 私有沙盒内（外部文件管理器不可见）。</summary>
    public static bool IsSandboxed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var appDir = FileSystem.AppDataDirectory;   // /data/user/0/包名/files
            if (string.IsNullOrEmpty(appDir)) return false;
            return FileGuard.IsUnder(Path.GetFullPath(path), appDir);
        }
        catch { return false; }
    }

    /// <summary>
    /// 把内部绝对路径翻译成人能看懂的路径：
    ///   /storage/emulated/0/Download/a.py → 内部存储/Download/a.py
    ///   /data/user/0/包名/files/workspace/a.py → 应用私有目录/a.py（外部不可见）
    /// </summary>
    public static string ToDisplay(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return path; }

        var root = PublicRoot;
        if (!string.IsNullOrEmpty(root) && FileGuard.IsUnder(full, root))
        {
            var rel = full.Length > root.Length ? full[(root.Length + 1)..] : "";
            return rel.Length > 0 ? "内部存储/" + rel.Replace('\\', '/') : "内部存储";
        }

        try
        {
            var appDir = FileSystem.AppDataDirectory;
            if (!string.IsNullOrEmpty(appDir) && FileGuard.IsUnder(full, appDir))
            {
                var rel = full.Length > appDir.Length ? full[(appDir.Length + 1)..] : "";
                return (rel.Length > 0 ? "应用私有目录/" + rel.Replace('\\', '/') : "应用私有目录")
                       + "（外部不可见）";
            }
        }
        catch { }

        return path;
    }

    /// <summary>工作区当前是否对外可见（决定要不要提示"外部看不到"）。</summary>
    public static bool WorkspaceVisible => !IsSandboxed(AppSettings.EffectiveWorkspacePath);

    // ─────────── 目录准备 ───────────

    /// <summary>确保目录存在。返回是否可用。绝不抛异常。</summary>
    public static bool EnsureDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            Directory.CreateDirectory(dir);
            return true;
        }
        catch { return false; }
    }

    // ─────────── 导出到公共存储 ───────────

    /// <summary>
    /// 把沙盒里的文件/目录复制到公共 Download 目录，让用户能在文件管理器里找到。
    /// 返回结果说明（成功/失败原因），绝不抛异常。
    /// </summary>
    public static async Task<(bool Ok, string Message)> ExportAsync(string path)
    {
        if (!IsAllFilesGranted())
            return (false, "还没有系统「所有文件访问」权限，无法写入公共存储。请先在盾牌面板里点「去开启」。");

        var downloads = PublicDownloads;
        if (string.IsNullOrEmpty(downloads))
            return (false, "找不到公共 Download 目录。");

        try
        {
            var full = FileGuard.Resolve(path);

            if (File.Exists(full))
            {
                var target = Path.Combine(downloads, Path.GetFileName(full));
                target = UniquePath(target);
                EnsureDir(downloads);
                await CopyFileAsync(full, target);
                return (true, $"已导出到 {ToDisplay(target)}");
            }

            if (Directory.Exists(full))
            {
                var dirName = new DirectoryInfo(full).Name;
                if (string.IsNullOrEmpty(dirName)) dirName = "workspace";
                var targetRoot = Path.Combine(downloads, dirName);
                targetRoot = UniquePath(targetRoot);
                int n = await CopyDirAsync(full, targetRoot);
                return (true, $"已导出 {n} 个文件到 {ToDisplay(targetRoot)}");
            }

            return (false, $"找不到要导出的路径：{path}");
        }
        catch (Exception ex)
        {
            return (false, "导出失败：" + ex.Message);
        }
    }

    /// <summary>把整个工作区导出到公共 Download。</summary>
    public static Task<(bool Ok, string Message)> ExportWorkspaceAsync()
        => ExportAsync(AppSettings.EffectiveWorkspacePath);

    // ─────────── 沙盒 → 公共存储 迁移 ───────────

    /// <summary>旧的沙盒工作区路径（不管权限如何，固定为 app 私有目录下的 workspace/）。</summary>
    private static string SandboxWorkspace
    {
        get
        {
            try { return Path.Combine(FileSystem.AppDataDirectory, "workspace"); }
            catch { return ""; }
        }
    }

    /// <summary>
    /// 用户首次授予全盘权限后，把原先落在沙盒里的工作区文件搬到公共存储，
    /// 免得"一授权文件就消失"（其实还在沙盒，只是用户看不到、Ta 也不再往那儿写）。
    /// 目标已存在同名文件时跳过，不覆盖。
    /// </summary>
    public static async Task<(bool Ok, string Message)> MigrateSandboxToPublicAsync()
    {
        var target = DefaultPublicWorkspace;
        var source = SandboxWorkspace;

        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(source))
            return (false, "路径解析失败。");
        if (!IsAllFilesGranted())
            return (false, "还没有系统存储权限。");
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return (false, "无需迁移。");
        if (!Directory.Exists(source))
            return (false, "沙盒工作区不存在，无需迁移。");

        try
        {
            // 确保目标目录（含中间层级）存在，否则复制会直接失败
            if (!EnsureDefaultWorkspace(out var reason))
                return (false, "无法准备目标目录：" + reason);

            int moved = 0, skipped = 0;

            foreach (var f in Directory.GetFiles(source))
            {
                var dst = Path.Combine(target, Path.GetFileName(f));
                if (File.Exists(dst)) { skipped++; continue; }
                await CopyFileAsync(f, dst);
                moved++;
            }

            if (moved == 0 && skipped == 0) return (false, "沙盒工作区是空的。");

            var msg = $"已把 {moved} 个文件搬到 {ToDisplay(target)}";
            if (skipped > 0) msg += $"（{skipped} 个同名文件已存在，跳过）";
            return (true, msg);
        }
        catch (Exception ex)
        {
            return (false, "迁移失败：" + ex.Message);
        }
    }

    /// <summary>目标已存在时自动加 (1)(2) 后缀，避免覆盖用户文件。</summary>
    private static string UniquePath(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var dir = Path.GetDirectoryName(target) ?? "";
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (int i = 1; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{name}({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return target;
    }

    private static async Task CopyFileAsync(string src, string dst)
    {
        using var input = File.OpenRead(src);
        using var output = File.Create(dst);
        await input.CopyToAsync(output);
    }

    private static async Task<int> CopyDirAsync(string srcDir, string dstDir)
    {
        EnsureDir(dstDir);
        int count = 0;
        foreach (var f in Directory.GetFiles(srcDir))
        {
            await CopyFileAsync(f, Path.Combine(dstDir, Path.GetFileName(f)));
            count++;
        }
        foreach (var d in Directory.GetDirectories(srcDir))
            count += await CopyDirAsync(d, Path.Combine(dstDir, Path.GetFileName(d)));
        return count;
    }

    // ─────────── 给模型看的说明 ───────────

    /// <summary>
    /// 告诉模型"你现在能写到哪、用户能不能看到"。
    /// 这段会拼进 system 提示词，直接决定它会不会把文件写到用户找不到的地方。
    /// </summary>
    public static string BuildStorageText()
    {
        var sb = new StringBuilder();
        var ws = AppSettings.EffectiveWorkspacePath;
        var granted = IsAllFilesGranted();

        sb.AppendLine("【存储实况】");
        sb.AppendLine($"工作区：{ws}");
        sb.AppendLine($"用户可见性：{(WorkspaceVisible ? "可见（用户在文件管理器里能找到）" : "不可见（这是 APP 私有目录，用户在外面找不到）")}");
        sb.AppendLine($"系统「所有文件访问」权限：{(granted ? "已授予" : "未授予")}");

        if (granted)
        {
            sb.AppendLine("默认工作区是 " + DefaultPublicWorkspace + "（内部存储里，用户能直接看到），"
                          + "中间目录不存在时会自动建好，你直接读写即可。");
            sb.AppendLine("也可以写到公共存储的其他位置（如 " + PublicRoot + "/Download/xxx），用完整绝对路径。");
        }
        else
        {
            sb.AppendLine("你只能读写工作区（APP 私有目录），无法访问 "
                          + (PublicRoot.Length > 0 ? PublicRoot : "公共存储") + " 等公共路径。");
            sb.AppendLine("如果要写到公共存储，先告诉用户在聊天页盾牌图标里开启" + PermissionLabel + "。");
        }

        sb.AppendLine("注意：即使开启全盘权限，也只能访问公共存储，读不到其他 APP 的私有 /data 目录（Android 沙盒限制）。");
        return sb.ToString();
    }

    // 注：原来这里有个 DescribeDefaultWorkspace()，全项目没人调用，已删（瘦身）。
}

// ────────────── 原 SuperAdmin.cs ──────────────
/// <summary>
/// Super Admin 深度权限状态与引导（高风险，设置页独立开关控制并明示风险）。
/// - 存储访问：MANAGE_EXTERNAL_STORAGE，全盘读写文件；
/// - 设备管理器：DevicePolicyManager 系统级能力。
/// 两个都是 Android 特殊权限，需用户到系统设置手动开启（本类提供一键跳转）。
/// </summary>
public static class SuperAdmin
{
    // ── 存储访问 ──

    /// <summary>
    /// 当前系统有没有「所有文件访问」这个特殊权限。
    /// MANAGE_EXTERNAL_STORAGE 是 Android 11（API 30）才引入的，
    /// 在 Android 10 及以下的系统上：
    ///   · Environment.IsExternalStorageManager 这个 API 根本不存在（调用会抛异常）
    ///   · 系统设置里也没有「所有文件访问」这个页面（跳转必然失败）
    /// 所以低版本必须走普通存储权限那条路，不能拿这个开关判定。
    /// </summary>
    public static bool HasAllFilesAccessApi
    {
        get
        {
#if ANDROID
            try { return Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

    /// <summary>当前系统的版本号文本，用于界面提示/排查。</summary>
    public static string AndroidVersionText
    {
        get
        {
#if ANDROID
            try
            {
                var v = Android.OS.Build.VERSION.Release ?? "";
                var sdk = (int)Android.OS.Build.VERSION.SdkInt;
                return string.IsNullOrEmpty(v) ? ("API " + sdk) : ("Android " + v + "（API " + sdk + "）");
            }
            catch { return "未知"; }
#else
            return "非 Android";
#endif
        }
    }

    /// <summary>
    /// 是否已获得「全盘读写」能力。按系统版本走两条完全不同的判定：
    ///   · Android 11+ ：查 MANAGE_EXTERNAL_STORAGE（所有文件访问）
    ///   · Android 10- ：没有那个开关，查普通存储权限
    ///     （配合 manifest 的 requestLegacyExternalStorage，拿到后同样是全盘可读写）
    /// </summary>
    public static bool IsStorageGranted()
    {
#if ANDROID
        try
        {
            if (HasAllFilesAccessApi)
                return Android.OS.Environment.IsExternalStorageManager;

            var ctx = Android.App.Application.Context;
            return ctx.CheckSelfPermission(Android.Manifest.Permission.ReadExternalStorage)
                       == Android.Content.PM.Permission.Granted
                || ctx.CheckSelfPermission(Android.Manifest.Permission.WriteExternalStorage)
                       == Android.Content.PM.Permission.Granted;
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>打开系统「所有文件访问」设置页（仅 Android 11+ 有效）。</summary>
    public static void OpenStorageSettings()
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionManageAllFilesAccessPermission,
                Android.Net.Uri.Parse("package:" + Android.App.Application.Context.PackageName));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    /// <summary>
    /// 打开本应用的系统详情页（权限入口）。
    /// Android 10 及以下没有「所有文件访问」页面，用这个兜底，
    /// 用户可以在里面手动开「存储」权限。
    /// </summary>
    public static void OpenAppSettings()
    {
#if ANDROID
        try
        {
            var intent = new Android.Content.Intent(
                Android.Provider.Settings.ActionApplicationDetailsSettings,
                Android.Net.Uri.Parse("package:" + Android.App.Application.Context.PackageName));
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    // ── 设备管理器 ──

    /// <summary>设备管理器是否已激活。</summary>
    public static bool IsAdminActive()
    {
#if ANDROID
        try
        {
            var dpm = (Android.App.Admin.DevicePolicyManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.DevicePolicyService);
            if (dpm == null) return false;
            var cn = new Android.Content.ComponentName(Android.App.Application.Context,
                Java.Lang.Class.FromType(typeof(QyAdminReceiver)));
            return dpm.IsAdminActive(cn);
        }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>拉起系统「激活设备管理器」界面。</summary>
    public static void RequestAdmin()
    {
#if ANDROID
        try
        {
            var cn = new Android.Content.ComponentName(Android.App.Application.Context,
                Java.Lang.Class.FromType(typeof(QyAdminReceiver)));
            var intent = new Android.Content.Intent(
                Android.App.Admin.DevicePolicyManager.ActionAddDeviceAdmin);
            intent.PutExtra(Android.App.Admin.DevicePolicyManager.ExtraDeviceAdmin, cn);
            intent.PutExtra(Android.App.Admin.DevicePolicyManager.ExtraAddExplanation,
                "青阳AI 需要设备管理器权限以提供系统级管理能力（高风险，可锁屏/清数据等）。仅在你信任时启用。");
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            Android.App.Application.Context.StartActivity(intent);
        }
        catch { }
#endif
    }

    /// <summary>解除设备管理器激活。</summary>
    public static void RemoveAdmin()
    {
#if ANDROID
        try
        {
            var dpm = (Android.App.Admin.DevicePolicyManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.DevicePolicyService);
            if (dpm == null) return;
            var cn = new Android.Content.ComponentName(Android.App.Application.Context,
                Java.Lang.Class.FromType(typeof(QyAdminReceiver)));
            if (dpm.IsAdminActive(cn))
                dpm.RemoveActiveAdmin(cn);
        }
        catch { }
#endif
    }
}


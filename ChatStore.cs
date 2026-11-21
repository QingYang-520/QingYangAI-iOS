using System.Threading;
using SQLite;

namespace 青阳AI;

/// <summary>
/// 本地统一存储：聊天消息 / 长期记忆 / 日记，全部走 SQLite（qingyang.db3）。
/// 首次启动时把旧版 chahis.qingyang 单 JSON 历史迁入并改名 .bak 保留。
/// 生成的图片落盘为文件，消息只存路径，避免历史体积膨胀。
/// </summary>
public sealed class ChatStore
{
    private static readonly Lazy<ChatStore> _lazy = new(() => new ChatStore());
    public static ChatStore Instance => _lazy.Value;

    private readonly SQLiteAsyncConnection _db;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>旧版聊天历史文件名（迁入数据库后改名 .bak）。</summary>
    private const string LegacyHistoryFile = "chahis.qingyang";

    private ChatStore()
    {
        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "qingyang.db3");
        _db = new SQLiteAsyncConnection(dbPath);
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            // 注：sqlite-net 的 CreateTableAsync 会自动为已存在的表补上新增列
            //（内部走 ALTER TABLE ADD COLUMN），所以加字段不用手写迁移脚本。
            await _db.CreateTableAsync<MsgRow>();
            await _db.CreateTableAsync<MemoryRow>();
            await _db.CreateTableAsync<DiaryRow>();
            await _db.CreateTableAsync<ReminderRow>();
            await _db.CreateTableAsync<EmotionRow>();
            await MigrateLegacyJsonAsync();
            await EnsureCompanionSinceAsync();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    // ────────────────────────── 旧版 JSON 迁移 ──────────────────────────

    private async Task MigrateLegacyJsonAsync()
    {
        try
        {
            var oldPath = Path.Combine(FileSystem.AppDataDirectory, LegacyHistoryFile);
            if (!File.Exists(oldPath)) return;

            var json = await File.ReadAllTextAsync(oldPath);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<ChatMsg>>(json);
            if (list != null)
            {
                foreach (var m in list)
                {
                    // 旧数据里 AI 生成图以 base64 内联，迁移时落盘为文件
                    if (!string.IsNullOrEmpty(m.ImageBase64) && string.IsNullOrEmpty(m.ImageUrl))
                    {
                        try
                        {
                            var bytes = Convert.FromBase64String(m.ImageBase64);
                            m.ImageUrl = await SaveImageBytesAsync(bytes, ".png");
                        }
                        catch { /* 图片损坏则丢弃图片，保留文字 */ }
                    }
                    await _db.InsertAsync(ToRow(m));
                }
            }
            File.Move(oldPath, oldPath + ".bak", true);
        }
        catch { /* 迁移失败不影响运行，下次再试 */ }
    }

    /// <summary>首次相遇时间：取库里最早的消息时间，没有则记为现在（只记一次，相伴天数从这天起算）。</summary>
    private async Task EnsureCompanionSinceAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Preferences.Default.Get("CompanionSince", ""))) return;
            var first = await _db.Table<MsgRow>().OrderBy(r => r.Timestamp).FirstOrDefaultAsync();
            var since = first?.Timestamp ?? DateTime.Now;
            Preferences.Default.Set("CompanionSince", since.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch { }
    }

    // ────────────────────────── 图片落盘 ──────────────────────────

    /// <summary>把图片字节写入 {AppData}/images/{guid}{ext}，返回文件路径。</summary>
    public static async Task<string> SaveImageBytesAsync(byte[] bytes, string ext)
    {
        var dir = Path.Combine(FileSystem.AppDataDirectory, "images");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ext);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    // ────────────────────────── 聊天消息 ──────────────────────────

    /// <summary>按入库顺序加载全部聊天消息。</summary>
    public async Task<List<ChatMsg>> LoadMessagesAsync()
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>().OrderBy(r => r.Id).ToListAsync();
        return DedupNearIdentical(rows.Select(ToMsg).ToList());
    }

    // 注：原来这里有个 GetMessagesAfterAsync，全项目没人调用，已删（瘦身）。

    /// <summary>
    /// 存消息的**串行闸**。
    ///
    /// 所有调用点都是 `_ = SaveMessageAsync(...)` 发了不管。同一条消息并发存两次时，
    /// 两次都会看到 `Id == 0`（第一次还没插完、Id 还没回写）→ **插出两条一模一样的行**。
    /// 表现就是：AI 回完消息 → 打开设置再关掉（会同步数据库）→ 多出一轮同样的对话；
    /// 生图那轮因为存得更频繁，能多出 3 张一样的图。
    /// 这里串行化，保证第一次插完把 Id 回写之后，第二次才进来（走 Update）。
    /// </summary>
    private static readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>保存一条消息：新消息插入并回写 Id，已有 Id 的更新。</summary>
    public async Task SaveMessageAsync(ChatMsg m)
    {
        await EnsureInitAsync();
        await _saveLock.WaitAsync();
        try
        {
            // 时间戳只定一次并回填到内存对象：
            // 旧写法每次都让 ToRow 现取 DateTime.Now，导致内存里的 m.Timestamp 一直是 default、
            // 与库里的值对不上 —— 去重指纹会失准，Update 时时间戳也会漂移。
            if (m.Timestamp == default) m.Timestamp = DateTime.Now;

            if (m.Id == 0)
                m.Id = await _db.InsertAsync(ToRow(m));
            else
                await _db.UpdateAsync(ToRow(m));
        }
        finally { _saveLock.Release(); }
    }

    /// <summary>
    /// 去掉「同一侧、内容完全相同、时间戳几乎一致」的重复行。
    ///
    /// 老数据里已经有并发存出来的重复行（闸门是后加的，只管新数据），
    /// 读的时候在这儿吃掉，界面上就不会再冒出重复的一轮对话 / 几张一样的图。
    /// **只读时过滤，不删库** —— 不动用户数据。
    ///
    /// 注意：必须**往前看若干条**，不能只比紧邻的上一条：
    /// 并发存出来的重复行可能是 `U, A, U', A'` 这种交错形态，
    /// `A` 夹在 `U` 和 `U'` 之间 —— 只比相邻的话 `U'` 永远抓不住。
    ///
    /// 窗口取 1 秒：并发存的是**同一个 ChatMsg 对象**，`ToRow` 直接用它的 `Timestamp`，
    /// 所以重复行的时间戳是**完全相同**的；而用户真手动连发两句一样的话，间隔起码几秒。
    /// 这样既抓得住重复，又不会误伤。
    /// </summary>
    private static List<ChatMsg> DedupNearIdentical(List<ChatMsg> msgs)
    {
        var outp = new List<ChatMsg>(msgs.Count);
        foreach (var m in msgs)
        {
            if (IsDuplicateOfRecent(outp, m)) continue;
            outp.Add(m);
        }
        return outp;
    }

    /// <summary>往前最多回看 <see cref="DedupWindow"/> 条，判断 m 是否为已输出的重复行。</summary>
    private const int DedupWindow = 16;

    private static bool IsDuplicateOfRecent(List<ChatMsg> outp, ChatMsg m)
    {
        int from = Math.Max(0, outp.Count - DedupWindow);
        for (int i = outp.Count - 1; i >= from; i--)
        {
            var prev = outp[i];
            if (prev.IsUser != m.IsUser) continue;
            if (!string.Equals(prev.Content, m.Content, StringComparison.Ordinal)) continue;
            if (!string.Equals(prev.ImageUrl, m.ImageUrl, StringComparison.Ordinal)) continue;
            if (Math.Abs((m.Timestamp - prev.Timestamp).TotalSeconds) > 1) continue;
            return true;
        }
        return false;
    }

    /// <summary>后台主动消息专用插入（不依赖 UI 的 ChatMsg 实例）。</summary>
    public async Task<int> InsertProactiveMessageAsync(string content)
    {
        await EnsureInitAsync();
        var row = new MsgRow
        {
            Timestamp = DateTime.Now,
            IsUser = false,
            Content = content,
            IsProactive = true
        };
        return await _db.InsertAsync(row);
    }

    public async Task DeleteMessagesAsync(IEnumerable<int> ids)
    {
        await EnsureInitAsync();
        foreach (var id in ids)
            await _db.DeleteAsync<MsgRow>(id);
    }

    public async Task ClearMessagesAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<MsgRow>();
    }

    /// <summary>
    /// 全文搜索聊天记录（LIKE 匹配，忽略大小写）。
    /// 结果按时间正序返回，最多 limit 条（默认 200）。
    /// </summary>
    public async Task<List<ChatMsg>> SearchMessagesAsync(string keyword, int limit = 200)
    {
        await EnsureInitAsync();
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return new List<ChatMsg>();

        // sqlite-net 的 LINQ 不支持 Contains，用参数化 SQL 走原生查询
        var rows = await _db.QueryAsync<MsgRow>(
            "SELECT * FROM messages WHERE Content LIKE ? ORDER BY Id ASC LIMIT ?",
            "%" + keyword + "%", limit);
        return DedupNearIdentical(rows.Select(ToMsg).ToList());
    }

    /// <summary>设置/取消某条消息的收藏状态。</summary>
    public async Task SetFavoriteAsync(int id, bool favorite)
    {
        await EnsureInitAsync();
        await _db.ExecuteAsync("UPDATE messages SET IsFavorite = ? WHERE Id = ?", favorite ? 1 : 0, id);
    }

    /// <summary>全部收藏的消息（新的在前）。</summary>
    public async Task<List<ChatMsg>> GetFavoritesAsync(int limit = 100)
    {
        await EnsureInitAsync();
        var rows = await _db.QueryAsync<MsgRow>(
            "SELECT * FROM messages WHERE IsFavorite = 1 ORDER BY Id DESC LIMIT ?", limit);
        return rows.Select(ToMsg).ToList();
    }

    /// <summary>取某时间段内的消息（日记生成用）。</summary>
    public async Task<List<ChatMsg>> GetMessagesBetweenAsync(DateTime start, DateTime end)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>()
            .Where(r => r.Timestamp >= start && r.Timestamp < end)
            .OrderBy(r => r.Id).ToListAsync();
        return DedupNearIdentical(rows.Select(ToMsg).ToList());
    }

    /// <summary>最近 n 条消息（时间正序，通知快捷回复生成上下文用）。</summary>
    public async Task<List<ChatMsg>> GetRecentMessagesAsync(int n)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>().OrderByDescending(r => r.Id).Take(n).ToListAsync();
        return DedupNearIdentical(rows.Select(ToMsg).Reverse().ToList());
    }

    /// <summary>插入一条用户消息（通知栏快捷回复用）。</summary>
    public async Task<int> InsertUserMessageAsync(string content)
    {
        await EnsureInitAsync();
        return await _db.InsertAsync(new MsgRow
        {
            Timestamp = DateTime.Now,
            IsUser = true,
            Content = content
        });
    }

    /// <summary>插入一条 AI 回复（通知快捷回复用，非主动消息）。</summary>
    public async Task<int> InsertAiMessageAsync(string content)
    {
        await EnsureInitAsync();
        return await _db.InsertAsync(new MsgRow
        {
            Timestamp = DateTime.Now,
            IsUser = false,
            Content = content
        });
    }

    /// <summary>最新一条消息（桌面小组件用），没有返回 null。</summary>
    public async Task<MsgRow?> GetLastMessageAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<MsgRow>().OrderByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    /// <summary>取最新一条用户消息时间（主动关心决策用）。</summary>
    public async Task<DateTime?> GetLastUserMessageTimeAsync()
    {
        await EnsureInitAsync();
        var row = await _db.Table<MsgRow>().Where(r => r.IsUser).OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        return row?.Timestamp;
    }

    /// <summary>主动消息统计：今天已发条数、最近一次主动消息时间。</summary>
    public async Task<(int todayCount, DateTime? lastProactiveAt)> GetProactiveStatsAsync(DateTime todayStart)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MsgRow>().Where(r => r.IsProactive).ToListAsync();
        var last = rows.Count > 0 ? rows.Max(r => r.Timestamp) : (DateTime?)null;
        var todayCount = rows.Count(r => r.Timestamp >= todayStart);
        return (todayCount, last);
    }

    // ────────────────────────── 长期记忆 ──────────────────────────

    /// <summary>记忆条数上限：超出时删除最早的。</summary>
    private const int MemoryCap = 100;

    /// <summary>添加一条记忆。重复内容不重复存；超出上限删最旧。</summary>
    public async Task<bool> AddMemoryAsync(string content)
    {
        await EnsureInitAsync();
        content = content.Trim();
        if (content.Length == 0 || content.Length > 200) return false;

        var exists = await _db.Table<MemoryRow>()
            .Where(r => r.Content == content).FirstOrDefaultAsync();
        if (exists != null) return false;

        await _db.InsertAsync(new MemoryRow { Content = content, CreatedAt = DateTime.Now });

        var all = await _db.Table<MemoryRow>().OrderBy(r => r.Id).ToListAsync();
        if (all.Count > MemoryCap)
        {
            foreach (var r in all.Take(all.Count - MemoryCap))
                await _db.DeleteAsync(r);
        }
        return true;
    }

    /// <summary>取最近 n 条记忆（新的在前）。</summary>
    public async Task<List<string>> GetRecentMemoriesAsync(int n)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<MemoryRow>().OrderByDescending(r => r.Id).Take(n).ToListAsync();
        return rows.Select(r => r.Content).ToList();
    }

    // ── 遗忘曲线（2026-10-26）──────────────────────────────────────────

    /// <summary>一条记忆多久没被召回就归档（天）。</summary>
    private const int ArchiveAfterDays = 90;

    /// <summary>
    /// 按「遗忘曲线」取要注入 system 的记忆。
    ///
    /// 权重 = 命中次数 × 新鲜度。规则：
    ///  · 从未召回、且创建超过 <see cref="ArchiveAfterDays"/> 天 → 归档，不再注入；
    ///  · 召回过但超过 90 天没再召回、且只命中过 1 次 → 归档；
    ///  · 其余按 命中次数 降序 + 最近命中时间 降序 排序，取前 n 条。
    ///
    /// 归档只是不再注入（面板里还在，可手动恢复），不删数据。
    /// 取用后会自动给这批记忆记一次命中（LastHitAt / HitCount++）。
    /// </summary>
    public async Task<List<string>> GetWeightedMemoriesAsync(int n)
    {
        await EnsureInitAsync();
        var now = DateTime.Now;
        var all = await _db.Table<MemoryRow>().ToListAsync();
        if (all.Count == 0) return new List<string>();

        var live = new List<MemoryRow>();
        bool anyArchived = false;

        foreach (var r in all)
        {
            if (r.Archived) continue;

            var lastTouched = r.LastHitAt ?? r.CreatedAt;
            var idleDays = (now - lastTouched).TotalDays;

            // 长期没被用到、且没被反复强化过 → 归档（淡忘，但不删除）
            if (idleDays > ArchiveAfterDays && r.HitCount <= 1)
            {
                r.Archived = true;
                await _db.UpdateAsync(r);
                anyArchived = true;
                continue;
            }
            live.Add(r);
        }

        if (anyArchived)
            MemoryService.NotifyMemoriesChanged();

        var picked = live
            .OrderByDescending(r => r.HitCount)
            .ThenByDescending(r => r.LastHitAt ?? r.CreatedAt)
            .Take(n)
            .ToList();

        // 记一次命中：反复被用到的记忆权重会自然升高
        foreach (var r in picked)
        {
            r.HitCount++;
            r.LastHitAt = now;
            await _db.UpdateAsync(r);
        }

        return picked.Select(r => r.Content).ToList();
    }

    /// <summary>把一条已归档的记忆恢复为活跃状态（记忆面板用）。</summary>
    public async Task UnarchiveMemoryAsync(int id)
    {
        await EnsureInitAsync();
        var r = await _db.FindAsync<MemoryRow>(id);
        if (r == null) return;
        r.Archived = false;
        r.LastHitAt = DateTime.Now;   // 恢复时重置新鲜度，免得下次立刻又被归档
        await _db.UpdateAsync(r);
    }

    /// <summary>已归档的记忆条数（面板上提示"已淡忘 N 条"）。</summary>
    public async Task<int> CountArchivedMemoriesAsync()
    {
        await EnsureInitAsync();
        return await _db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM memories WHERE Archived = 1");
    }

    public async Task<int> CountMemoriesAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<MemoryRow>().CountAsync();
    }

    /// <summary>全部记忆，新的在前（记忆面板用）。</summary>
    public async Task<List<MemoryRow>> GetMemoriesAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<MemoryRow>().OrderByDescending(r => r.Id).ToListAsync();
    }

    /// <summary>删除单条记忆（记忆面板用）。</summary>
    public async Task DeleteMemoryAsync(int id)
    {
        await EnsureInitAsync();
        await _db.DeleteAsync<MemoryRow>(id);
    }

    public async Task ClearMemoriesAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<MemoryRow>();
    }

    // ────────────────────────── 日记 ──────────────────────────

    /// <summary>是否已有指定日期（yyyy-MM-dd）的日记。</summary>
    public async Task<bool> HasDiaryAsync(string date)
    {
        await EnsureInitAsync();
        return await _db.FindAsync<DiaryRow>(date) != null;
    }

    public async Task SaveDiaryAsync(string date, string content)
    {
        await EnsureInitAsync();
        await _db.InsertOrReplaceAsync(new DiaryRow { Date = date, Content = content, CreatedAt = DateTime.Now });
    }

    /// <summary>全部日记，按日期倒序。</summary>
    public async Task<List<DiaryRow>> GetDiariesAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<DiaryRow>().OrderByDescending(r => r.Date).ToListAsync();
    }

    /// <summary>取最近一篇日记（注入上下文用，没有返回 null）。</summary>
    public async Task<DiaryRow?> GetLatestDiaryAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<DiaryRow>().OrderByDescending(r => r.Date).FirstOrDefaultAsync();
    }

    // ────────────────────────── 承诺提醒 ──────────────────────────

    /// <summary>新增一条提醒，返回到期时间（创建失败返回 null）。</summary>
    public async Task<ReminderRow?> AddReminderAsync(string content, DateTime dueAt)
    {
        await EnsureInitAsync();
        content = (content ?? "").Trim();
        if (content.Length == 0) return null;
        if (content.Length > 200) content = content[..200];

        var row = new ReminderRow
        {
            Content = content,
            DueAt = dueAt,
            CreatedAt = DateTime.Now,
            Done = false,
            Cancelled = false
        };
        row.Id = await _db.InsertAsync(row);
        return row;
    }

    /// <summary>
    /// 取出所有**已到期、未完成、未取消**的提醒（心跳里调）。
    /// 只取 now 之前 due 的，避免提前提醒。
    /// </summary>
    public async Task<List<ReminderRow>> GetDueRemindersAsync(DateTime now, int limit = 5)
    {
        await EnsureInitAsync();
        var rows = await _db.Table<ReminderRow>()
            .Where(r => !r.Done && !r.Cancelled && r.DueAt <= now)
            .OrderBy(r => r.DueAt)
            .Take(limit)
            .ToListAsync();
        return rows;
    }

    /// <summary>标记提醒已提醒过。</summary>
    public async Task MarkReminderDoneAsync(int id)
    {
        await EnsureInitAsync();
        await _db.ExecuteAsync("UPDATE reminders SET Done = 1 WHERE Id = ?", id);
    }

    /// <summary>取消一条提醒（用户说"不用提醒了"）。</summary>
    public async Task CancelReminderAsync(int id)
    {
        await EnsureInitAsync();
        await _db.ExecuteAsync("UPDATE reminders SET Cancelled = 1 WHERE Id = ?", id);
    }

    /// <summary>全部未完成提醒（新的在前，面板/上下文用）。</summary>
    public async Task<List<ReminderRow>> GetPendingRemindersAsync(int limit = 20)
    {
        await EnsureInitAsync();
        return await _db.Table<ReminderRow>()
            .Where(r => !r.Done && !r.Cancelled)
            .OrderBy(r => r.DueAt)
            .Take(limit)
            .ToListAsync();
    }

    /// <summary>按内容关键字找最近的未完成提醒（她说"不用提醒了"时用来定位）。</summary>
    public async Task<ReminderRow?> FindPendingReminderAsync(string keyword)
    {
        await EnsureInitAsync();
        keyword = (keyword ?? "").Trim();
        var all = await _db.Table<ReminderRow>()
            .Where(r => !r.Done && !r.Cancelled)
            .OrderBy(r => r.DueAt)
            .ToListAsync();
        if (all.Count == 0) return null;
        if (keyword.Length == 0) return all[0];

        foreach (var r in all)
            if (r.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return r;
        return null;
    }

    // ────────────────────────── 情绪与关系 ──────────────────────────

    /// <summary>记录一条情绪/关系快照（每条消息复盘时写入）。</summary>
    public async Task AddEmotionAsync(string mood, int score, string trend)
    {
        await EnsureInitAsync();
        await _db.InsertAsync(new EmotionRow
        {
            At = DateTime.Now,
            Mood = (mood ?? "").Trim(),
            Score = Math.Clamp(score, 0, 100),
            Trend = (trend ?? "").Trim()
        });

        // 只保留最近 2000 条，避免无限膨胀
        var count = await _db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM emotions");
        if (count > 2000)
        {
            await _db.ExecuteAsync(
                "DELETE FROM emotions WHERE Id IN (SELECT Id FROM emotions ORDER BY Id ASC LIMIT ?)",
                count - 2000);
        }
    }

    /// <summary>最近 n 天的情绪记录（时间正序，画曲线用）。</summary>
    public async Task<List<EmotionRow>> GetEmotionsAsync(int days = 30)
    {
        await EnsureInitAsync();
        var since = DateTime.Now.Date.AddDays(-days);
        return await _db.Table<EmotionRow>()
            .Where(r => r.At >= since)
            .OrderBy(r => r.At)
            .ToListAsync();
    }

    /// <summary>按天聚合的平均情绪分（时间正序）。</summary>
    public async Task<List<(DateTime Day, int AvgScore, string Moods)>> GetEmotionDailyAsync(int days = 30)
    {
        var rows = await GetEmotionsAsync(days);
        var result = new List<(DateTime, int, string)>();
        foreach (var g in rows.GroupBy(r => r.At.Date).OrderBy(g => g.Key))
        {
            var avg = (int)Math.Round(g.Average(r => r.Score));
            var moods = string.Join("、", g.Select(r => r.Mood)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .Take(3));
            result.Add((g.Key, avg, moods));
        }
        return result;
    }

    /// <summary>清空情绪记录（设置页"清空记忆"时一起清）。</summary>
    public async Task ClearEmotionsAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<EmotionRow>();
    }

    // ────────────────────────── 备份与恢复 ──────────────────────────

    /// <summary>导出全部数据（消息/记忆/日记）。</summary>
    public async Task<(List<MsgRow> msgs, List<MemoryRow> memories, List<DiaryRow> diaries)> ExportAllAsync()
    {
        await EnsureInitAsync();
        var msgs = await _db.Table<MsgRow>().OrderBy(r => r.Id).ToListAsync();
        var memories = await _db.Table<MemoryRow>().OrderBy(r => r.Id).ToListAsync();
        var diaries = await _db.Table<DiaryRow>().OrderByDescending(r => r.Date).ToListAsync();
        return (msgs, memories, diaries);
    }

    /// <summary>导出提醒与情绪（备份用，老备份里没有这两张表就返回空）。</summary>
    public async Task<(List<ReminderRow> reminders, List<EmotionRow> emotions)> ExportExtrasAsync()
    {
        await EnsureInitAsync();
        var reminders = await _db.Table<ReminderRow>().OrderBy(r => r.Id).ToListAsync();
        var emotions = await _db.Table<EmotionRow>().OrderBy(r => r.Id).ToListAsync();
        return (reminders, emotions);
    }

    /// <summary>覆盖式恢复：清空三张表后写入备份数据，并恢复首次相遇时间。</summary>
    public async Task RestoreAllAsync(List<MsgRow> msgs, List<MemoryRow> memories, List<DiaryRow> diaries, string? companionSince)
    {
        await EnsureInitAsync();
        await _db.RunInTransactionAsync(tr =>
        {
            tr.Execute("DELETE FROM messages");
            tr.Execute("DELETE FROM memories");
            tr.Execute("DELETE FROM diaries");
            foreach (var m in msgs) tr.Insert(m);
            foreach (var mm in memories) tr.Insert(mm);
            foreach (var d in diaries) tr.Insert(d);
        });
        if (!string.IsNullOrWhiteSpace(companionSince))
            Preferences.Default.Set("CompanionSince", companionSince);
    }

    /// <summary>恢复提醒与情绪（老备份可能为 null，跳过即可）。</summary>
    public async Task RestoreExtrasAsync(List<ReminderRow>? reminders, List<EmotionRow>? emotions)
    {
        await EnsureInitAsync();
        await _db.RunInTransactionAsync(tr =>
        {
            tr.Execute("DELETE FROM reminders");
            tr.Execute("DELETE FROM emotions");
            if (reminders != null) foreach (var r in reminders) tr.Insert(r);
            if (emotions != null) foreach (var e in emotions) tr.Insert(e);
        });
    }

    // ────────────────────────── 行映射 ──────────────────────────

    private static MsgRow ToRow(ChatMsg m) => new()
    {
        Id = m.Id,
        Timestamp = m.Timestamp == default ? DateTime.Now : m.Timestamp,
        IsUser = m.IsUser,
        Content = m.Content ?? "",
        Thinking = m.Thinking ?? "",
        TerminalTitle = m.TerminalTitle ?? "",
        TerminalExecLog = m.TerminalExecLog ?? "",
        VisionResult = m.VisionResult ?? "",
        ImageUrl = m.ImageUrl ?? "",
        ImageState = m.ImageState ?? "",
        VoicePath = m.VoicePath ?? "",
        ErrorText = m.ErrorText ?? "",
        AgentSummary = m.SavedAgentSummary ?? "",
        AgentLog = m.SavedAgentLog ?? "",
        IsProactive = m.IsProactive,
        IsFavorite = m.IsFavorite
    };

    private static ChatMsg ToMsg(MsgRow r) => new()
    {
        Id = r.Id,
        Timestamp = r.Timestamp,
        IsUser = r.IsUser,
        Content = r.Content,
        Thinking = r.Thinking,
        TerminalTitle = r.TerminalTitle,
        TerminalExecLog = r.TerminalExecLog,
        VisionResult = r.VisionResult,
        ImageUrl = r.ImageUrl,
        ImageState = r.ImageState,
        VoicePath = r.VoicePath,
        ErrorText = r.ErrorText,
        SavedAgentSummary = r.AgentSummary,
        SavedAgentLog = r.AgentLog,
        IsProactive = r.IsProactive,
        IsFavorite = r.IsFavorite
    };
}

[Table("messages")]
public class MsgRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public bool IsUser { get; set; }
    public string Content { get; set; } = "";
    public string Thinking { get; set; } = "";
    /// <summary>工具气泡标题（如 调用「屏幕使用时间」API / 调用 Shizuku 终端命令），空 = 旧默认文案。</summary>
    public string TerminalTitle { get; set; } = "";
    public string TerminalExecLog { get; set; } = "";
    public string VisionResult { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string ImageState { get; set; } = "";
    public string VoicePath { get; set; } = "";
    /// <summary>错误行的文字（出错时显示在气泡下方，**不覆盖正文**）。</summary>
    public string ErrorText { get; set; } = "";
    /// <summary>Agent 过程区的折叠摘要（重启后还能看到 Ta 当时做了什么）。</summary>
    public string AgentSummary { get; set; } = "";
    /// <summary>Agent 过程区的步骤明细。</summary>
    public string AgentLog { get; set; } = "";
    /// <summary>是否为后台主动发来的关心消息（不参与普通编辑流程）。</summary>
    public bool IsProactive { get; set; }
    /// <summary>是否被用户收藏（收藏的消息可在收藏列表里回看、可注入上下文）。</summary>
    public bool IsFavorite { get; set; }
}

[Table("memories")]
public class MemoryRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>最近一次被注入 system 的时间（遗忘曲线用；null = 从未被召回）。</summary>
    public DateTime? LastHitAt { get; set; }
    /// <summary>被召回的累计次数（反复被用到说明重要，权重会升）。</summary>
    public int HitCount { get; set; }
    /// <summary>是否已归档（长期未召回，不再注入 system，但保留在面板里可手动恢复）。</summary>
    public bool Archived { get; set; }
}

[Table("diaries")]
public class DiaryRow
{
    /// <summary>日期 yyyy-MM-dd，一天一篇。</summary>
    [PrimaryKey] public string Date { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// 她答应过用户的事（承诺提醒）。
/// 由 {remind:"时间|内容"} 指令创建，心跳时捞取到期项发通知。
/// </summary>
[Table("reminders")]
public class ReminderRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    /// <summary>提醒内容（她答应要提醒用户的事）。</summary>
    public string Content { get; set; } = "";
    /// <summary>到点时间。</summary>
    public DateTime DueAt { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>是否已提醒过（提醒后置 true，不再重复）。</summary>
    public bool Done { get; set; }
    /// <summary>是否已取消（用户说"不用提醒了"）。</summary>
    public bool Cancelled { get; set; }
}

/// <summary>
/// 情绪与关系记录：每条消息复盘的「情绪/走向」落库，用于情绪曲线。
/// 一天可有多条（每次复盘一条），统计时按天聚合。
/// </summary>
[Table("emotions")]
public class EmotionRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public DateTime At { get; set; }
    /// <summary>用户此刻情绪（复盘输出的原词，如"低落"/"开心"）。</summary>
    public string Mood { get; set; } = "";
    /// <summary>情绪分值 0~100（由 Mood 映射而来，用于画曲线）。</summary>
    public int Score { get; set; }
    /// <summary>关系走向关键词（复盘第四行）。</summary>
    public string Trend { get; set; } = "";
}

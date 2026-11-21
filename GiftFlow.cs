using System.Text.Json;

namespace 青阳AI;

// ============================================================================
// 程序员节 · 5亿字元礼（2026-11-18 接入）
//
// 后端：LoginVault 同一台服务器，端点都在 /logicrecords/api/v1/gift/ 下。
//   GET  /gift/info                       活动信息（名称/额度/是否开放 open）
//   GET  /gift/status?fingerprint=xx      某设备今天领没领
//   GET  /gift/history?fingerprint=xx     某设备历史领取记录
//   POST /gift/claim  {fingerprint}       领取（重复领 → 409 already_claimed）
//
// 复用 AccountFlow.RequestAsync：同一套 HTTPS→HTTP→HTTP:5668 静默降级 + X-API-Key 令牌。
// 身份按设备指纹（AppSettings.DeviceFingerprint）区分，每人一份。
//
// 启动弹窗：由 ChatPage.OnAppearing 调用 TryShowOnLaunchAsync —— 后台静默查
//   gift/info.open（服务端按北京时间判断，10/24 当天才 true）+ gift/status，
//   当天开放且未领过才弹。用 Preferences 记住「今天已提示过」，避免反复打扰。
//   非活动日 / 网络失败：全部静默，绝不弹窗。
// ============================================================================

internal static class GiftFlow
{
    private const string PrefPromptDate = "GiftPromptDate";   // 上次提示日期（yyyy-MM-dd，服务器日期）

    /// <summary>活动信息。open 由服务端按北京时间判断，当天 10/24 才为 true。</summary>
    internal sealed class GiftInfo
    {
        public string Name { get; set; } = "";
        public string GiftKey { get; set; } = "";
        public long Amount { get; set; }
        public bool ValidForever { get; set; }
        public string ServerDate { get; set; } = "";
        public bool Open { get; set; }
        public string Window { get; set; } = "";
    }

    /// <summary>领取状态。claimed=true 表示今天已领，Claim 里是领取记录（含凭证 token）。</summary>
    internal sealed class GiftStatus
    {
        public string Fingerprint { get; set; } = "";
        public string ServerDate { get; set; } = "";
        public string GiftKey { get; set; } = "";
        public long Amount { get; set; }
        public bool Claimed { get; set; }
        public string? Token { get; set; }
    }

    /// <summary>从 data 里宽松取字符串（兼容不同字段命名）。</summary>
    private static string Str(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        return "";
    }

    /// <summary>从 data 里宽松取长整数。</summary>
    private static long Long(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
                if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var l2)) return l2;
            }
        return 0;
    }

    /// <summary>GET /gift/info。失败返回 null（静默）。</summary>
    internal static async Task<GiftInfo?> GetInfoAsync()
    {
        try
        {
            var (ok, _, data) = await AccountFlow.RequestAsync(HttpMethod.Get, "/gift/info", null);
            if (!ok || data is null || data.Value.ValueKind != JsonValueKind.Object) return null;
            var d = data.Value;
            return new GiftInfo
            {
                Name = Str(d, "name"),
                GiftKey = Str(d, "gift_key", "giftKey"),
                Amount = Long(d, "amount"),
                ValidForever = d.TryGetProperty("valid_forever", out var vf) && vf.ValueKind == JsonValueKind.True,
                ServerDate = Str(d, "server_date", "serverDate"),
                Open = d.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True,
                Window = Str(d, "window"),
            };
        }
        catch { return null; }
    }

    /// <summary>GET /gift/status?fingerprint=xx。失败返回 null（静默）。</summary>
    internal static async Task<GiftStatus?> GetStatusAsync(string fingerprint)
    {
        try
        {
            var (ok, _, data) = await AccountFlow.RequestAsync(HttpMethod.Get,
                "/gift/status?fingerprint=" + Uri.EscapeDataString(fingerprint), null);
            if (!ok || data is null || data.Value.ValueKind != JsonValueKind.Object) return null;
            var d = data.Value;
            string token = "";
            if (d.TryGetProperty("claim", out var claim) && claim.ValueKind == JsonValueKind.Object)
                token = Str(claim, "token");
            if (string.IsNullOrEmpty(token)) token = Str(d, "token");
            return new GiftStatus
            {
                Fingerprint = Str(d, "fingerprint"),
                ServerDate = Str(d, "server_date", "serverDate"),
                GiftKey = Str(d, "gift_key", "giftKey"),
                Amount = Long(d, "amount"),
                Claimed = d.TryGetProperty("claimed", out var c) && c.ValueKind == JsonValueKind.True,
                Token = token,
            };
        }
        catch { return null; }
    }

    /// <summary>POST /gift/claim {fingerprint}。成功返回凭证 token；失败返回 null（静默）。</summary>
    internal static async Task<string?> ClaimAsync(string fingerprint)
    {
        try
        {
            var (ok, _, data) = await AccountFlow.RequestAsync(HttpMethod.Post,
                "/gift/claim", new Dictionary<string, object> { ["fingerprint"] = fingerprint });
            if (!ok || data is null) return null;
            var d = data.Value;

            // 领取成功的响应可能是 {token:"QYGIFT-..."} 或整个领取记录；宽松解析
            if (d.ValueKind == JsonValueKind.Object)
            {
                var t = Str(d, "token", "gift_token", "giftToken");
                if (!string.IsNullOrEmpty(t)) return t;
                if (d.TryGetProperty("claim", out var claim) && claim.ValueKind == JsonValueKind.Object)
                {
                    var ct = Str(claim, "token");
                    if (!string.IsNullOrEmpty(ct)) return ct;
                }
            }
            // 成功但没解析到 token：回查 status 取凭证
            var st = await GetStatusAsync(fingerprint);
            return st?.Token;
        }
        catch { return null; }
    }

    /// <summary>
    /// 启动时调用：后台静默判断今天是否开放且未领，符合条件才弹窗一次。
    /// 非活动日、网络失败、已提示过 → 全部静默返回。
    /// </summary>
    internal static async Task TryShowOnLaunchAsync(Page host)
    {
        try
        {
            // 1) 查活动是否开放（服务端按北京时间判断）
            var info = await GetInfoAsync();
            if (info is null || !info.Open) return;   // 非活动日：静默

            var date = string.IsNullOrEmpty(info.ServerDate)
                ? DateTime.Now.ToString("yyyy-MM-dd") : info.ServerDate;

            // 2) 今天已提示过 → 不再打扰
            if (Preferences.Default.Get(PrefPromptDate, "") == date) return;

            // 3) 查该设备今天领没领
            var fp = AppSettings.DeviceFingerprint;
            var status = await GetStatusAsync(fp);
            if (status is not null && status.Claimed) return;   // 已领过：静默

            // 4) 标记今天已提示（无论领没领，避免反复弹）
            Preferences.Default.Set(PrefPromptDate, date);

            // 5) 弹窗询问是否领取（切回主线程）
            var amountText = info.Amount > 0 ? info.Amount.ToString("N0") : "5亿";
            var title = string.IsNullOrEmpty(info.Name) ? "活动上线" : info.Name;
            bool go = false;
            await host.Dispatcher.DispatchAsync(async () =>
            {
                go = await host.DisplayAlertAsync(title,
                    $"今天可领取 {amountText} 字元（永久有效）。\n{info.Window}\n\n现在领取吗？",
                    "领取", "稍后");
            });
            if (!go) return;

            // 6) 领取并回显凭证（领取走后台，回显切主线程）
            var token = await ClaimAsync(fp);
            await host.Dispatcher.DispatchAsync(async () =>
            {
                if (token is null)
                    await host.DisplayAlertAsync("领取失败", "服务暂时不可用，请稍后再试。", "好");
                else
                    await host.DisplayAlertAsync("领取成功",
                        $"已领取 {amountText} 字元。\n凭证：{token}\n（永久有效，可在服务端查询）", "好");
            });
        }
        catch { /* 活动检查绝不干扰正常使用：任何异常都静默 */ }
    }
}

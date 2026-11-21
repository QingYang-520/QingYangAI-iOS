using System.Text;
using Microsoft.Maui.Controls;

namespace 青阳AI;

// ============================================================================
// 青阳账户登录流程（2026-11-16 新增；2026-11-17 接入 LoginVault 真实 API）
//
// 后端：LoginVault，地址 https://qingyang.religongsi.com/logicrecords/api/v1
//   · 内网直连 HTTP 5668；外网 HTTPS。客户端按 HTTPS → HTTP → HTTP:5668
//     依次**静默降级**（不弹提示），取第一个能通过 /health 探测的基址。
//   · 令牌认证：请求头 X-API-Key: <令牌>（令牌存 AppSettings.LoginApiToken）。
//   · 响应封装：{"ok":bool,"data":...,"error":{"code","message"}|null}。
//
// 账户按设备指纹存（LoginVault = 账号保险库）：
//   · 注册设备  PUT    /devices/{设备指纹}  body{note,metadata}
//   · 存账号    POST   /devices/{设备指纹}/accounts  body{account,username,note,metadata}
//   · 设备指纹 = Android 设备 ID（AppSettings.DeviceFingerprint），重装不变。
//
// 一个界面（用户要求「输入手机号 + 账号 + 密码 就 OK」）：
//   LoginAccountPage  入口：手机号 + 账号 + 密码 + 保存并登录 + 跳过
//
// 统一约定：
//   · 输入框一律用 FloatingLabelEntry —— 标签浮动、帮助文字常显、**失焦后才校验**；
//   · 错误提示放在框下方，写清楚怎么改，不只说「格式错误」。
// ============================================================================

/// <summary>登录流程的共享辅助（真实后端 + 跳转）。</summary>
internal static class AccountFlow
{
    /// <summary>候选 API 基址，按优先级：HTTPS → HTTP → HTTP:5668。降级在后台静默完成。</summary>
    private static readonly string[] CandidateBases =
    {
        "https://qingyang.religongsi.com/logicrecords/api/v1",
        "http://qingyang.religongsi.com/logicrecords/api/v1",
        "http://qingyang.religongsi.com:5668/logicrecords/api/v1",
    };

    private static string? _resolvedBase;
    private static Task<string>? _baseTask;
    private static readonly object _baseLock = new();

    /// <summary>
    /// 取当前生效的 API 基址（缓存）。首次调用在后台静默探测 /health，
    /// 不阻塞 UI；并发调用共享同一次探测任务。
    /// </summary>
    public static Task<string> GetApiBaseAsync()
    {
        if (_resolvedBase != null) return Task.FromResult(_resolvedBase);
        lock (_baseLock)
        {
            if (_resolvedBase != null) return Task.FromResult(_resolvedBase);
            _baseTask ??= ResolveBaseAsync();
            return _baseTask;
        }
    }

    /// <summary>
    /// 静默探测 /health，返回第一个能连通的基址；全失败时返回最后一个候选
    /// （调用方会得到网络异常，界面统一提示，不在后台弹窗）。
    /// </summary>
    private static async Task<string> ResolveBaseAsync()
    {
        foreach (var b in CandidateBases)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var resp = await Http.Short.GetAsync(b + "/health", cts.Token);
                string raw = await resp.Content.ReadAsStringAsync(cts.Token);
                if (raw.Contains("\"ok\":true") || raw.Contains("\"ok\": true"))
                {
                    _resolvedBase = b;
                    return b;
                }
            }
            catch { /* 该基址不通，静默尝试下一个 */ }
        }
        _resolvedBase = CandidateBases[^1];
        return _resolvedBase;
    }

    /// <summary>
    /// 向登录 API 发请求（带 X-API-Key 令牌），解析响应封装。
    /// 登录与活动（GiftFlow）共用这套静默降级链 + 令牌 + 响应解析。
    /// 返回 (ok, errorMessage, data)。失败不抛异常，由调用方决定提示。
    /// </summary>
    internal static async Task<(bool ok, string? error, System.Text.Json.JsonElement? data)> RequestAsync(HttpMethod method, string path, object? body)
    {
        var token = AppSettings.LoginApiToken;
        if (string.IsNullOrWhiteSpace(token))
            return (false, "登录 API 令牌未配置（AppSettings.LoginApiToken）", null);

        try
        {
            string baseUrl = await GetApiBaseAsync();
            using var req = new HttpRequestMessage(method, baseUrl + path);
            req.Headers.TryAddWithoutValidation("X-API-Key", token.Trim());
            if (body != null) req.Content = Http.JsonContent(body);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var resp = await Http.Short.SendAsync(req, cts.Token);
            string raw = await resp.Content.ReadAsStringAsync(cts.Token);

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            bool ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == System.Text.Json.JsonValueKind.True;
            System.Text.Json.JsonElement? data = root.TryGetProperty("data", out var dEl) ? dEl : null;
            string? err = null;
            if (root.TryGetProperty("error", out var eEl) && eEl.ValueKind == System.Text.Json.JsonValueKind.Object
                && eEl.TryGetProperty("message", out var mEl)) err = mEl.GetString();
            if (!ok && err == null) err = $"登录接口返回失败（HTTP {(int)resp.StatusCode}）";
            return (ok, err, data);
        }
        catch (Exception ex)
        {
            return (false, $"无法连接登录服务：{ex.Message}", null);
        }
    }

    /// <summary>
    /// 一步保存登录：注册设备（PUT /devices/{指纹}）+ 存账号（POST /devices/{指纹}/accounts）。
    /// 手机号 → 联系方式；账号 → 用户名；密码 → 账号数据里的密码。
    /// 失败抛 InvalidOperationException（带原因）。
    /// </summary>
    public static async Task SaveLoginAsync(string phone, string account, string password)
    {
        var fp = AppSettings.DeviceFingerprint;
        var acct = account?.Trim() ?? "";

        // 1) 注册设备（PUT /devices/{指纹}，body 带 note、metadata）
        var devBody = new Dictionary<string, object>
        {
            ["note"] = phone,
            ["metadata"] = new Dictionary<string, object> { ["type"] = "phone", ["contact"] = phone },
        };
        var (ok, err, _) = await RequestAsync(HttpMethod.Put, "/devices/" + fp, devBody);
        if (!ok) throw new InvalidOperationException(err ?? "设备注册失败");

        // 2) 存账号（POST /devices/{指纹}/accounts，body {account, username, note, metadata}）
        var accBody = new Dictionary<string, object>
        {
            ["username"] = string.IsNullOrEmpty(acct) ? phone : acct,
            ["account"] = new Dictionary<string, object>
            {
                ["contact"] = phone,
                ["account"] = acct,
                ["password"] = password,
                ["type"] = "phone",
            },
            ["note"] = "青阳AI 账户",
            ["metadata"] = new Dictionary<string, object> { ["app"] = "青阳AI" },
        };
        var (ok2, err2, _) = await RequestAsync(HttpMethod.Post, "/devices/" + fp + "/accounts", accBody);
        if (!ok2) throw new InvalidOperationException(err2 ?? "存账号失败");

        AppSettings.AccountPhone = phone;
        AppSettings.AccountName = acct;
        AppSettings.AccountSetupDone = true;
    }

    /// <summary>手机号校验：返回错误文案，null = 通过。**失焦后才调用**。</summary>
    public static string? ValidatePhone(string? v)
    {
        var d = new string((v ?? "").Where(char.IsDigit).ToArray());
        if (d.Length == 0) return "请输入手机号";
        if (d.Length != 11) return $"手机号应为 11 位数字，当前 {d.Length} 位";
        if (!d.StartsWith("1")) return "手机号应以 1 开头（国内号码）";
        return null;
    }

    /// <summary>邮箱校验：返回错误文案，null = 通过。</summary>
    public static string? ValidateEmail(string? v)
    {
        var s = (v ?? "").Trim();
        if (s.Length == 0) return "请输入邮箱地址";
        int at = s.IndexOf('@');
        if (at <= 0) return "邮箱需包含 @，且 @ 前要有内容（如 name@example.com）";
        int dot = s.LastIndexOf('.');
        if (dot < at + 2 || dot == s.Length - 1) return "邮箱域名格式不对（如 example.com）";
        return null;
    }

    /// <summary>密码校验：至少 8 位、含字母和数字。</summary>
    public static string? ValidatePassword(string? v)
    {
        var s = v ?? "";
        if (s.Length < 8) return $"密码至少 8 位，当前 {s.Length} 位";
        if (!s.Any(char.IsLetter)) return "密码需包含至少一个字母";
        if (!s.Any(char.IsDigit)) return "密码需包含至少一个数字";
        return null;
    }

    /// <summary>昵称校验：1~16 个字符。</summary>
    public static string? ValidateName(string? v)
    {
        var s = (v ?? "").Trim();
        if (s.Length == 0) return "请给自己起个昵称";
        if (s.Length > 16) return $"昵称最多 16 个字，当前 {s.Length} 个";
        return null;
    }

    /// <summary>进入登录流程（从外部调用）。包 NavigationPage，否则 PushAsync 失效。</summary>
    public static void Start()
    {
        var nav = Application.Current?.Windows[0].Page as NavigationPage;
        if (nav is null)
        {
            Application.Current!.Windows[0].Page = new NavigationPage(new LoginAccountPage());
            return;
        }
        _ = nav.PushAsync(new LoginAccountPage());
    }

    /// <summary>登录流程全部完成 → 进聊天页。</summary>
    public static void FinishToChat()
    {
        Application.Current!.Windows[0].Page = new NavigationPage(new ChatPage());
    }
}

// ────────────── ① 登录入口：手机号 + 账号 + 密码 ──────────────
public class LoginAccountPage : ContentPage
{
    private readonly FloatingLabelEntry _phone = new();
    private readonly FloatingLabelEntry _account = new();
    private readonly FloatingLabelEntry _password = new();
    private readonly Button _save = new();

    public LoginAccountPage()
    {
        Title = "登录青阳账户";
        BackgroundColor = UiKit.PageBg;
        UiKit.InitPage(this);

        _phone.LabelText = "手机号";
        _phone.HelperText = "请输入 11 位手机号（以 1 开头）";
        _phone.Keyboard = Keyboard.Telephone;
        _phone.Validator = AccountFlow.ValidatePhone;

        _account.LabelText = "账号";
        _account.HelperText = "请输入账号（昵称或用户名）";

        _password.LabelText = "密码";
        _password.HelperText = "请输入密码（至少 6 位）";
        _password.IsPassword = true;

        _save.Text = "保存并登录";
        _save.TextColor = Colors.White;
        _save.FontAttributes = FontAttributes.Bold;
        _save.CornerRadius = 12;
        _save.Padding = new Thickness(0, 12);
        _save.Clicked += async (_, _) => await OnSaveAsync();

        var skip = new Button
        {
            Text = "跳过（暂不登录）",
            TextColor = UiKit.TextMuted,
            BackgroundColor = Colors.Transparent,
            BorderColor = Color.FromArgb("#2B2524"),
            BorderWidth = 1,
            CornerRadius = 12,
            Padding = new Thickness(0, 10),
        };
        skip.Clicked += (_, _) =>
        {
            AppSettings.AccountSetupDone = true;
            AccountFlow.FinishToChat();
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 40),
                Spacing = 16,
                Children =
                {
                    new Label
                    {
                        Text = "登录青阳账户",
                        FontSize = 26, FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White,
                    },
                    new Label
                    {
                        Text = "手机号 + 账号 + 密码，保存到云端保险库，多设备同步。",
                        FontSize = 13, TextColor = UiKit.TextMuted,
                    },
                    _phone,
                    _account,
                    _password,
                    _save,
                    skip,
                }
            }
        };
    }

    private async Task OnSaveAsync()
    {
        // 手动校验（用户可能没失焦就点按钮）
        var err = AccountFlow.ValidatePhone(_phone.Text);
        if (err != null) { _phone.ErrorText = err; return; }

        var account = _account.Text?.Trim() ?? "";
        if (account.Length == 0) { _account.ErrorText = "请输入账号"; return; }

        var password = _password.Text ?? "";
        if (password.Length < 6) { _password.ErrorText = "密码至少 6 位"; return; }

        var phone = new string(_phone.Text.Where(char.IsDigit).ToArray());

        _save.IsEnabled = false;
        _save.Text = "保存中…";
        try
        {
            await AccountFlow.SaveLoginAsync(phone, account, password);
        }
        catch (Exception ex)
        {
            _save.IsEnabled = true;
            _save.Text = "保存并登录";
            await DisplayAlertAsync("保存失败", ex.Message, "好");
            return;
        }
        _save.IsEnabled = true;
        _save.Text = "保存并登录";
        await DisplayAlertAsync("已保存", "登录信息已保存到云端，欢迎回来！", "好");
        AccountFlow.FinishToChat();
    }
}

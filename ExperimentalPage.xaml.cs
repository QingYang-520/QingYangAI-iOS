using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace 青阳AI;

/// <summary>
/// 实验性功能页（设置页底部入口进入）。
///
/// 收纳两类尚未稳定 / 面向进阶用户的功能，各自一个下拉框：
///   1. 本地部署大模型 —— 2026-10-17 从设置页「模型与配置」搬出，2026-10-20 随本页独立
///   2. 替换原机AI助手 —— 长按电源键唤醒青阳AI + 唤醒光效
///
/// 注意：本地部署的整块逻辑（约 1100 行）从 SettingsPage 原样搬来，未改行为。
///    SettingsPage 里仍保留 AppSettings.LocalModel* 的读写（聊天页要用），
///    但界面控件与部署流程已全部移到这里。
/// </summary>
public partial class ExperimentalPage : ContentPage
{
    private static readonly Easing QuadraticEaseOut = new(t => 1 - (1 - t) * (1 - t));

    public ExperimentalPage()
    {
        InitializeComponent();
        NavigationPage.SetHasNavigationBar(this, true);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            ThemeManager.Apply(this);
            JellyAnim.EnterPage(Content);
            InitDeployUi();
            RefreshAssistantState();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExperimentalPage OnAppearing] {ex.Message}");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        SaveLocalModelConfig();   // 离开前把地址/模型名/Key 落盘（否则用户改了白改）
        TeardownDeployUi();
    }

    /// <summary>
    /// 把本地模型配置从界面写回 AppSettings。
    ///
    /// 注意：这段原来在 SettingsPage.CommitSettings() 里；界面搬到本页后必须由本页负责，
    /// 否则用户在「服务地址 / 模型名 / API Key」里填的东西根本不会生效（只读不写的真 bug）。
    /// 地址非法时不覆盖旧值，避免把聊天请求打到无效地址。
    /// </summary>
    private void SaveLocalModelConfig()
    {
        try
        {
            var url = entLocalUrl.Text?.Trim() ?? "";
            if (url.Length == 0 || AppSettings.IsValidLocalApiUrl(url))
                AppSettings.LocalModelApiUrl = url;

            AppSettings.LocalModelName = entLocalModel.Text?.Trim() ?? "";
            AppSettings.LocalModelApiKey = entLocalKey.Text?.Trim() ?? "";
            AppSettings.UseLocalModel = swUseLocalModel.IsToggled;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SaveLocalModelConfig] {ex.Message}");
        }
    }

    // ══════════════ 下拉框：本地部署大模型 ══════════════

    /// <summary>输入框失焦即落盘（用户不离开页面也不会丢配置）。</summary>
    private void OnLocalModelEntryChanged(object? sender, FocusEventArgs e)
        => SaveLocalModelConfig();

    private void OnToggleLocalModel(object? sender, EventArgs e)
        => ToggleBubble(localModelBubbleContent, localModelArrow);

    // ══════════════ 下拉框：替换原机AI助手 ══════════════

    private void OnToggleAssistant(object? sender, EventArgs e)
        => ToggleBubble(assistantBubbleContent, assistantArrow);

    /// <summary>
    /// 气泡展开/收起：高度展开/收拢 + 淡入淡出，全程二次方 ease-out（先快后慢）。
    /// 与设置页 ToggleBubble 同款规格（展开 260ms / 淡入 220ms；收起 200ms / 淡出 180ms）。
    /// </summary>
    private async void ToggleBubble(VerticalStackLayout content, MorphIcon arrow)
    {
        bool expand = !content.IsVisible;
        arrow.Icon = expand ? "ChevronDown" : "ChevronRight";

        if (expand)
        {
            content.Opacity = 0;
            content.HeightRequest = 0;
            content.IsVisible = true;

            double targetH = MeasureBubbleHeight(content);

            content.Animate("bubbleHeight", v => content.HeightRequest = v, 0d, targetH, 16, 260, QuadraticEaseOut);
            await content.FadeToAsync(1, 220, QuadraticEaseOut);
            await Task.Delay(60);
            content.HeightRequest = -1;
        }
        else
        {
            double fromH = content.Height > 0 ? content.Height : content.HeightRequest;
            if (fromH <= 0) fromH = 200;

            content.Animate("bubbleHeight", v => content.HeightRequest = Math.Max(0, v), fromH, 0d, 16, 200, QuadraticEaseOut);
            await content.FadeToAsync(0, 180, QuadraticEaseOut);
            await Task.Delay(40);
            content.IsVisible = false;
            content.Opacity = 1;
            content.HeightRequest = -1;
        }
    }

    /// <summary>测量气泡内容的目标高度（以父容器宽度为约束，加余量防裁切）。</summary>
    private double MeasureBubbleHeight(VisualElement content)
    {
        double width = content.Width;
        if (width <= 0) width = (content.Parent as VisualElement)?.Width ?? -1;
        if (width <= 0) width = this.Width - 60;
        if (width <= 0) width = 320;

        try
        {
            var size = content.Measure(width, double.PositiveInfinity);
            var h = size.Height;
            if (double.IsNaN(h) || double.IsInfinity(h) || h <= 0) h = 200;
            return h + 2;
        }
        catch { return 200; }
    }

    // ══════════════ 替换原机AI助手 ══════════════

    /// <summary>刷新助手卡片状态（每次进页 / 从系统设置返回时调）。</summary>
    private void RefreshAssistantState()
    {
        try
        {
            if (AssistantReplacer.IsUsDefaultAssistant())
            {
                lblAssistState.Text = "已完成：青阳AI 已是默认助手，长按电源键即可唤醒。";
                lblAssistState.TextColor = Color.FromArgb("#B9D5C4");
                btnAssistReplace.Text = "已替换（点击重新确认）";
            }
            else
            {
                lblAssistState.Text = $"当前助手：{AssistantReplacer.CurrentAssistantName()}";
                lblAssistState.TextColor = Color.FromArgb("#D5C2C1");
                btnAssistReplace.Text = "设青阳AI为默认助手";
            }

            swAssistantGlow.IsToggled = AppSettings.AssistantGlowEnabled;

            bool ov = AssistantReplacer.HasOverlayPermission();
            lblOverlayState.Text = ov ? "悬浮窗：已开启" : "悬浮窗：未开启";
            lblOverlayState.TextColor = Color.FromArgb(ov ? "#B9D5C4" : "#F2B8B5");
            btnOverlayPerm.IsVisible = !ov;

            lblAssistDiag.Text = AssistantReplacer.Diagnose();
        }
        catch (Exception ex)
        {
            lblAssistDiag.Text = "状态读取失败：" + ex.Message;
        }
    }

    private async void OnReplaceAssistantClicked(object? sender, EventArgs e)
    {
        if (!AssistantReplacer.RoleApiSupported)
        {
            bool go = await DisplayAlertAsync("系统版本过低",
                "本机系统低于 Android 10，没有「默认助手」角色接口，无法一键替换。\n\n可以到「设置 → 默认应用」里手动找找。",
                "打开设置", "取消");
            if (go) AssistantReplacer.OpenDefaultAppsSettings();
            return;
        }

        bool ok = AssistantReplacer.RequestRole();
        if (!ok)
        {
            bool go = await DisplayAlertAsync("没法自动弹出",
                "系统没有响应助手角色请求（部分定制系统屏蔽了这个弹窗）。\n\n点「打开设置」去系统里手动选择。",
                "打开设置", "取消");
            if (go) AssistantReplacer.OpenDefaultAppsSettings();
            return;
        }

        await Task.Delay(900);
        RefreshAssistantState();
    }

    private void OnOpenAssistSettingsClicked(object? sender, EventArgs e)
        => AssistantReplacer.OpenDefaultAppsSettings();

    private void OnAssistantGlowToggled(object? sender, ToggledEventArgs e)
        => AppSettings.AssistantGlowEnabled = e.Value;

    private void OnOverlayPermClicked(object? sender, EventArgs e)
        => AssistantReplacer.OpenOverlaySettings();

    /// <summary>预览光效：免装免唤醒，直接放一遍看效果。</summary>
    private async void OnPreviewGlowClicked(object? sender, EventArgs e)
    {
#if ANDROID
        if (!AssistantReplacer.HasOverlayPermission())
        {
            bool go = await DisplayAlertAsync("需要悬浮窗权限",
                "光效要画在屏幕最上层，需要「显示在其他应用上层」权限。\n\n不开启也能正常替换助手，只是没有这个动画。",
                "去开启", "取消");
            if (go) AssistantReplacer.OpenOverlaySettings();
            return;
        }
        bool played = AssistantGlowOverlay.Play(Android.App.Application.Context);
        if (!played)
            await DisplayAlertAsync("播放失败", "悬浮层没能挂上去，可能是系统限制了。", "好");
#else
        await DisplayAlertAsync("仅 Android", "此功能只在 Android 上可用。", "好");
#endif
    }

    // ══════════════ 本地部署大模型（从 SettingsPage 整块搬来） ══════════════

    private List<LocalModelCatalog.Item> _deployItems = new();
    private bool _deployListLoading;

    /// <summary>打开本页时刷新部署相关 UI（已部署列表 + 进度订阅 + 配置回填）。</summary>
    private void InitDeployUi()
    {
        try
        {
            LocalModelDownloader.RestoreDeployed();
            LocalModelDownloader.ProgressChanged -= OnDeployProgress;
            LocalModelDownloader.ProgressChanged += OnDeployProgress;
            RefreshDeployedList();
            RefreshDeployInfo();

            // 回填本地模型配置（这些值由本页维护）
            entLocalUrl.Text = AppSettings.LocalModelApiUrl;
            entLocalModel.Text = AppSettings.LocalModelName;
            entLocalKey.Text = AppSettings.LocalModelApiKey;
            swUseLocalModel.IsToggled = AppSettings.UseLocalModel;
            UpdateLocalModelHint();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[InitDeployUi] {ex.Message}"); }
    }

    /// <summary>离开本页时退订，避免内存泄漏。</summary>
    private void TeardownDeployUi()
    {
        try { LocalModelDownloader.ProgressChanged -= OnDeployProgress; } catch { }
    }

    /// <summary>本地模型的开关（切换即落盘）。</summary>
    private void OnUseLocalModelToggled(object? sender, ToggledEventArgs e)
    {
        if (AppSettings.UseLocalModel == e.Value) return;
        AppSettings.UseLocalModel = e.Value;
        UpdateLocalModelHint();
    }

    private async void OnFetchDeployListClicked(object? sender, EventArgs e)
    {
        if (_deployListLoading) return;
        _deployListLoading = true;
        try
        {
            lblDeployInfo.Text = "正在从国内加速源拉取模型列表…";
            lblDeployInfo.TextColor = Color.FromArgb("#9E8D8C");
            btnFetchDeployList.IsEnabled = false;

            var items = await LocalModelCatalog.FetchAsync(force: true);
            _deployItems = items;

            picDeployModel.ItemsSource = items.Select(x => x.Display).ToList();
            if (items.Count > 0)
            {
                // 如果 SelectedIndex 本来就是 0，直接赋 0 不会触发 SelectedIndexChanged，
                //    信息行会停留在"共 N 个"而看不到推荐内存 —— 这里显式刷一次。
                if (picDeployModel.SelectedIndex == 0) OnDeployModelChanged(picDeployModel, EventArgs.Empty);
                else picDeployModel.SelectedIndex = 0;
            }

            lblDeployInfo.Text = $"共 {items.Count} 个可部署模型（均为 Q4_K_M 量化、参数 ≤ 8B、走国内加速源）。";
            lblDeployInfo.TextColor = Color.FromArgb("#B9D5C4");
        }
        catch (Exception ex)
        {
            lblDeployInfo.Text = "获取失败：" + ex.Message;
            lblDeployInfo.TextColor = Color.FromArgb("#F2B8B5");
        }
        finally
        {
            _deployListLoading = false;
            btnFetchDeployList.IsEnabled = true;
        }
    }

    /// <summary>选中模型时刷新信息行（推荐内存 / 来源）。</summary>
    private void OnDeployModelChanged(object? sender, EventArgs e)
    {
        try
        {
            var item = CurrentDeployItem();
            if (item == null) return;
            var src = item.Source == "hf-mirror" ? "在线拉取" : "内置清单";
            lblDeployInfo.Text = $"{item.Name}　·　{item.Quant}　·　推荐 {item.RamGB:0.#}GB 内存　·　来源：{src}";
            lblDeployInfo.TextColor = Color.FromArgb("#9E8D8C");
            btnDeployModel.Text = LocalModelDownloader.IsDeployed(item.FileName) ? "重新部署" : "部署模型";
        }
        catch { }
    }

    /// <summary>「检查本机服务」：探测本机有没有 Ollama / 有没有服务在跑（需要 Shizuku 才能探本机命令）。</summary>
    private async void OnCheckLocalServiceClicked(object? sender, EventArgs e)
    {
        btnCheckLocalSvc.IsEnabled = false;
        lblLocalSvcStatus.Text = "正在检测本机服务…";
        lblLocalSvcStatus.TextColor = Color.FromArgb("#9E8D8C");
        try
        {
            var p = await OllamaLocalService.DetectAsync();
            switch (p.Kind)
            {
                case OllamaLocalService.ProbeKind.CliFound:
                    lblLocalSvcStatus.Text = $"已完成：{p.Message}";
                    lblLocalSvcStatus.TextColor = Color.FromArgb("#B9D5C4");
                    break;
                case OllamaLocalService.ProbeKind.ServerRunning:
                    lblLocalSvcStatus.Text = $"已完成：{p.Message}";
                    lblLocalSvcStatus.TextColor = Color.FromArgb("#B9D5C4");
                    break;
                case OllamaLocalService.ProbeKind.NoPermission:
                    lblLocalSvcStatus.Text = "注意：" + p.Message;
                    lblLocalSvcStatus.TextColor = Color.FromArgb("#F5D6A0");
                    await DisplayAlertAsync("无法检测本机服务",
                        "检测本机是否安装/运行 Ollama 需要 Shizuku 权限。\n\n"
                      + "没有 Shizuku 时的替代做法：\n"
                      + "① 在电脑上装 Ollama（官网 ollama.com），手机填电脑的局域网 IP；\n"
                      + "② 或者你自己已经在手机上跑着服务，直接在下面「服务地址」里填本机地址即可。",
                        "知道了");
                    break;
                default:
                    lblLocalSvcStatus.Text = "本机未检测到 Ollama。点「安装/启动服务」试试。";
                    lblLocalSvcStatus.TextColor = Color.FromArgb("#F5D6A0");
                    break;
            }
        }
        catch (Exception ex)
        {
            lblLocalSvcStatus.Text = "检测出错：" + ex.Message;
            lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");
        }
        finally
        {
            btnCheckLocalSvc.IsEnabled = true;
        }
    }

    /// <summary>
    /// 「安装/启动服务」：有 Shizuku → 走国内镜像装 Ollama 并启动；没 Shizuku → 明确提示无法使用。
    /// Ollama 官方无 Android 版，装的是 Linux(glibc) 包，很可能跑不起来 —— 装完会真实验证，
    ///    失败就如实说明并给替代方案，绝不假装成功。
    /// </summary>
    /// <summary>
    /// Termux 部署指引。
    ///
    /// 2026-10-05 新增：用户反馈「ollama 一直下载失败」，排查后发现两个叠加问题 ——
    /// ① Android 不自带 curl（旧下载命令直接 not found）；② 就算下下来，
    /// 官方 Linux(glibc) 二进制在 Android(bionic) 上也起不来。
    /// 所以除了修下载器，还得给一条**真正能跑通**的路：Termux + proot-distro 造 glibc 环境。
    /// </summary>
    private async void OnTermuxGuideClicked(object? sender, EventArgs e)
    {
        var installed = TermuxGuide.IsInstalled;

        var choice = await DisplayActionSheetAsync(
            "Termux 部署指引",
            "关闭",
            null,
            "复制全部命令",
            installed ? "打开 Termux" : "复制 Termux 下载地址",
            "查看分步说明");

        switch (choice)
        {
            case "复制全部命令":
                await Clipboard.Default.SetTextAsync(TermuxGuide.CopyText);
                await DisplayAlertAsync("已复制",
                    "命令已复制到剪贴板。\n\n"
                  + "请在 Termux 里按「第 1 步 → 第 2 步 → 第 3 步 → 第 4 步」分段粘贴，"
                  + "不要一次全贴 —— 第 2 步会进入交互式环境，后面的命令不会自动接着跑。", "好");
                break;

            case "打开 Termux":
                if (!TermuxGuide.Launch())
                    await DisplayAlertAsync("打不开 Termux", "没能拉起 Termux，请从桌面手动打开。", "好");
                break;

            case "复制 Termux 下载地址":
                await Clipboard.Default.SetTextAsync("https://f-droid.org/packages/com.termux/");
                await DisplayAlertAsync("已复制",
                    "Termux 官方下载地址已复制。\n\n"
                  + "请从 F-Droid 或 GitHub 下载；应用商店里的版本多已停止更新。", "好");
                break;

            case "查看分步说明":
                await DisplayAlertAsync("Termux 部署步骤",
                    (installed
                        ? "检测到本机已安装 Termux。\n\n"
                        : $"未检测到 Termux，请先安装：{TermuxGuide.DownloadHint}\n\n")
                  + TermuxGuide.Steps, "好");
                break;
        }
    }

    private async void OnSetupLocalServiceClicked(object? sender, EventArgs e)
    {
        // ① 没 Shizuku：直接告诉用户这条路走不通（用户明确要求的行为）
        if (!ShizukuRunner.Available())
        {
            var online = ShizukuHelper.IsOnline;
            lblLocalSvcStatus.Text = online
                ? "失败：检测到 Shizuku 在线，但未授权本 App。"
                : "不可用：无 Shizuku，无法自动安装/启动本地服务。";
            lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");

            if (online)
            {
                var go = await DisplayAlertAsync("需要 Shizuku 授权",
                    "Shizuku 在线，但还没给「青阳AI」授权。\n\n去授权后就能自动安装/启动本地服务。",
                    "去授权", "取消");
                if (go)
                {
                    ShizukuHelper.RequestPermission();
                    await DisplayAlertAsync("已请求授权",
                        "请在系统弹窗里允许「青阳AI」使用 Shizuku，然后重试。", "好");
                }
            }
            else
            {
                await DisplayAlertAsync("无 Shizuku，无法使用",
                    "自动安装 / 启动本机推理服务需要 Shizuku（adb 权限）。\n\n"
                  + "当前情况：\n"
                  + "· 没检测到 Shizuku 在运行\n\n"
                  + "有两个选择：\n"
                  + "① 【推荐】在电脑上装 Ollama，手机填电脑的局域网 IP —— 不需要 Shizuku\n"
                  + "② 先装好 Shizuku 并授权本 App，再回来点这个按钮",
                    "知道了");
            }
            return;
        }

        // ② 有 Shizuku：先看是否已经装好/在跑，避免重复折腾
        btnSetupLocalSvc.IsEnabled = false;
        lblLocalSvcStatus.Text = "正在检测…";
        try
        {
            var probe = await OllamaLocalService.DetectAsync();

            if (probe.Kind == OllamaLocalService.ProbeKind.ServerRunning)
            {
                lblLocalSvcStatus.Text = "已完成：服务已在运行，无需安装。";
                lblLocalSvcStatus.TextColor = Color.FromArgb("#B9D5C4");
                return;
            }

            if (probe.Kind != OllamaLocalService.ProbeKind.CliFound)
            {
                // 需要先安装。给两个选择：直接下官方包装，或走 Termux（后者才是真能跑通的路）
                var pick = await DisplayActionSheetAsync("安装 Ollama",
                    "取消", null,
                    "下载安装官方包（约 1.5 GB）",
                    "看 Termux 部署指引（推荐）");

                if (pick == "看 Termux 部署指引（推荐）")
                {
                    OnTermuxGuideClicked(null, EventArgs.Empty);
                    return;
                }
                if (pick != "下载安装官方包（约 1.5 GB）") return;

                lblLocalSvcStatus.Text = "正在下载安装包…";
                var (ok, msg) = await OllamaLocalService.InstallAsync(s =>
                {
                    lblLocalSvcStatus.Text = s;
                });

                if (!ok)
                {
                    lblLocalSvcStatus.Text = "安装失败";
                    lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");
                    var toTermux = await DisplayAlertAsync("安装失败",
                        msg + "\n\n要看 Termux 部署指引吗？（手机上真正能跑通的那条路）",
                        "看指引", "知道了");
                    if (toTermux) OnTermuxGuideClicked(null, EventArgs.Empty);
                    return;
                }

                lblLocalSvcStatus.Text = "已完成：" + msg;
                lblLocalSvcStatus.TextColor = Color.FromArgb("#B9D5C4");
            }

            // ③ 启动服务
            lblLocalSvcStatus.Text = "正在启动服务…";
            var (sok, smsg) = await OllamaLocalService.StartServerAsync();
            if (sok)
            {
                lblLocalSvcStatus.Text = "已完成：" + smsg;
                lblLocalSvcStatus.TextColor = Color.FromArgb("#B9D5C4");
                // 顺手把地址填成本机
                entLocalUrl.Text = $"http://127.0.0.1:{OllamaLocalService.DefaultPort}";
                UpdateLocalModelHint();
                await DisplayAlertAsync("服务已就绪", smsg + "\n\n已把服务地址填为 http://127.0.0.1:11434。\n"
                    + "接下来点「拉取模型名」确认能列到模型，再去聊天页选它。", "好");
            }
            else
            {
                lblLocalSvcStatus.Text = "启动失败";
                lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");
                await DisplayAlertAsync("启动失败",
                    smsg + "\n\n如果反复失败，说明这个 Linux 版二进制在 Android 上跑不起来。\n"
                  + "建议改用在电脑上跑 Ollama 的方式。", "知道了");
            }
        }
        catch (Exception ex)
        {
            lblLocalSvcStatus.Text = "出错：" + ex.Message;
            lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");
        }
        finally
        {
            btnSetupLocalSvc.IsEnabled = true;
        }
    }

    /// <summary>
    /// 部署模型前的**服务检测闸门**（用户 2026-10-04 需求：下载模型前先检测有没有本地 ollama 服务）。
    ///
    /// 逻辑：
    ///   · 有 Shizuku 且本机已有命令/服务在跑 → 放行（true）
    ///   · 有 Shizuku 但没装 → 问用户要不要现在自动安装（走国内镜像）；装上并启动成功才放行
    ///   · **没有 Shizuku → 直接提示「无 Shizuku，无法使用」，不放行**（用户明确要求的行为）
    ///   · 用户已经填了可用的服务地址（在电脑上跑的那种）→ 也放行（毕竟模型是喂给它用的）
    /// </summary>
    private async Task<bool> EnsureLocalServiceBeforeDeployAsync()
    {
        // 已经填了一个非回环的服务地址（多半是电脑上的）→ 视为已有服务，直接放行
        var raw = entLocalUrl.Text?.Trim() ?? "";
        if (AppSettings.IsValidLocalApiUrl(raw) && !IsLoopbackUrl(raw))
            return true;

        // 没有 Shizuku → 明确提示无法使用（这是用户点名要的行为）
        if (!ShizukuRunner.Available())
        {
            lblLocalSvcStatus.Text = "不可用：无 Shizuku：无法自动检测/安装本机服务。";
            lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");

            var goPc = await DisplayAlertAsync("无 Shizuku，无法使用",
                "「下载后自动用本机服务加载」这条路需要 Shizuku 权限，当前设备没有可用的 Shizuku。\n\n"
              + "但模型还是可以先下着 —— 只是下完之后，需要你**在电脑上跑 Ollama** 来加载它。\n\n"
              + "· 若选「继续下载」：App 会把 .gguf 下到手机上，之后你在电脑上用它即可\n"
              + "· 若选「先不下载」：先装好 Shizuku 或先在电脑上备好服务，再回来",
                "继续下载", "先不下载");
            return goPc;
        }

        // 有 Shizuku：探一下
        btnDeployModel.IsEnabled = false;
        lblLocalSvcStatus.Text = "正在检测本机服务…";
        lblLocalSvcStatus.TextColor = Color.FromArgb("#9E8D8C");
        try
        {
            var probe = await OllamaLocalService.DetectAsync();
            if (probe.Kind is OllamaLocalService.ProbeKind.CliFound or OllamaLocalService.ProbeKind.ServerRunning)
            {
                lblLocalSvcStatus.Text = "已完成：" + probe.Message;
                lblLocalSvcStatus.TextColor = Color.FromArgb("#B9D5C4");
                return true;
            }

            // 没装 → 问要不要现在装
            var go = await DisplayAlertAsync("本机没有可用服务",
                "检测到本机还没装 Ollama。要现在下载安装吗？（约 1.5 GB）\n\n"
              + "Ollama 官方没有 Android 版，下的是 Linux(glibc) 包，在 Android 上起不来。\n"
              + "真想让它跑起来，得走 Termux 方案（设置页有指引）。\n\n"
              + "装不上也不影响下载模型 —— 你仍然可以在电脑上用它。",
                "尝试安装", "跳过，直接下载");
            if (!go)
            {
                lblLocalSvcStatus.Text = "已跳过自动安装，直接下载模型。";
                lblLocalSvcStatus.TextColor = Color.FromArgb("#F5D6A0");
                return true;
            }

            lblLocalSvcStatus.Text = "正在下载安装包…";
            var (ok, msg) = await OllamaLocalService.InstallAsync(s => lblLocalSvcStatus.Text = s);
            if (!ok)
            {
                lblLocalSvcStatus.Text = "安装失败（模型仍会继续下载）";
                lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");
                var keep = await DisplayAlertAsync("自动安装失败",
                    msg + "\n\n是否仍然继续下载模型？（可稍后在电脑上加载）", "继续下载", "取消");
                return keep;
            }

            lblLocalSvcStatus.Text = "正在启动服务…";
            var (sok, smsg) = await OllamaLocalService.StartServerAsync();
            if (sok)
            {
                lblLocalSvcStatus.Text = "已完成：" + smsg;
                lblLocalSvcStatus.TextColor = Color.FromArgb("#B9D5C4");
                entLocalUrl.Text = $"http://127.0.0.1:{OllamaLocalService.DefaultPort}";
                UpdateLocalModelHint();
            }
            else
            {
                lblLocalSvcStatus.Text = "服务启动失败（模型仍会继续下载）";
                lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");
            }
            return true;
        }
        catch (Exception ex)
        {
            lblLocalSvcStatus.Text = "检测出错：" + ex.Message;
            lblLocalSvcStatus.TextColor = Color.FromArgb("#F2B8B5");
            return true;   // 检测本身出错不拦下载
        }
        finally
        {
            btnDeployModel.IsEnabled = true;
        }
    }

    /// <summary>判断地址是不是回环（手机自己）。</summary>
    private static bool IsLoopbackUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
            var h = u.Host.ToLowerInvariant();
            return h is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0";
        }
        catch { return false; }
    }

    /// <summary>「部署模型」：下载 + 安装（后台不中断，通知栏与 App 内同步显示进度）。</summary>
    private async void OnDeployModelClicked(object? sender, EventArgs e)
    {
        var item = CurrentDeployItem();
        if (item == null)
        {
            await DisplayAlertAsync("先选模型", "还没有可部署的模型。先点「获取模型列表」。", "好");
            return;
        }

        // 重点：部署前先检测本机有没有可用服务（用户 2026-10-04 需求）
        //   有服务 → 直接下；没服务但能装 → 问一下；没服务也没权限 → 明确提示
        if (!await EnsureLocalServiceBeforeDeployAsync()) return;

        var go = await DisplayAlertAsync("部署模型",
            $"将下载并安装：\n{item.Display}\n\n来源：{item.DownloadUrl}\n\n下载期间可以切到后台，进度会显示在通知栏。", "开始部署", "取消");
        if (!go) return;

        btnDeployModel.IsEnabled = false;
        deployProgressBox.IsVisible = true;
        pbDeploy.Progress = 0;
        lblDeployProgress.Text = "准备中…";

        try
        {
            var state = await LocalModelDownloader.DownloadAsync(item);
            ApplyDeployState(state);

            if (state.Status == "已完成")
            {
                // 部署成功 → 刷新已部署列表
                RefreshDeployedList();
                RefreshDeployInfo();
                UpdateLocalModelHint();

                // 重点：自动用 Ollama 加载这个 gguf（用户 2026-10-04 需求）
                //   只在「本机有可用服务」时才做；没有就落到原来的使用引导
                var loaded = await TryAutoLoadWithOllamaAsync(item, state);

                if (!loaded) await ShowDeployDoneGuideAsync(item, state);
            }
            else
            {
                await DisplayAlertAsync("部署失败", state.Error ?? state.Status, "好");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("部署出错", ex.Message, "好");
        }
        finally
        {
            btnDeployModel.IsEnabled = true;
            RefreshDeployInfo();
        }
    }

    /// <summary>
    /// 下载完成后，尝试**自动用 Ollama 加载这个 gguf**（用户 2026-10-04 需求）。
    /// 返回 true = 已成功加载（调用方不用再弹手动引导）；false = 没条件自动加载，交给原来的引导。
    /// </summary>
    private async Task<bool> TryAutoLoadWithOllamaAsync(LocalModelCatalog.Item item, LocalModelDownloader.TaskState state)
    {
        try
        {
            if (!ShizukuRunner.Available()) return false;

            var probe = await OllamaLocalService.DetectAsync();
            if (probe.Kind is not (OllamaLocalService.ProbeKind.CliFound or OllamaLocalService.ProbeKind.ServerRunning))
                return false;

            // 只装了命令、服务没跑 → 先起服务
            if (probe.Kind == OllamaLocalService.ProbeKind.CliFound)
                await OllamaLocalService.StartServerAsync();

            var modelName = LocalModelDownloader.FileNameToModelName(state.FileName);
            var go = await DisplayAlertAsync("加载到本机服务",
                $"模型已下载：{item.Name}\n\n"
              + $"要现在用 Ollama 把它加载成一个叫「{modelName}」的模型吗？\n"
              + "加载后就能在聊天页直接选它。",
                "加载", "稍后手动加载");
            if (!go) return false;

            lblDeployProgress.Text = "正在让 Ollama 加载模型…";
            var (ok, msg) = await OllamaLocalService.LoadGgufAsync(state.TargetPath, modelName);
            if (ok)
            {
                lblDeployProgress.Text = "已完成：" + msg;
                entLocalModel.Text = modelName;
                if (string.IsNullOrWhiteSpace(entLocalUrl.Text) || IsLoopbackUrl(entLocalUrl.Text))
                    entLocalUrl.Text = $"http://127.0.0.1:{OllamaLocalService.DefaultPort}";
                UpdateLocalModelHint();

                await DisplayAlertAsync("加载成功",
                    $"{msg}\n\n已自动填好模型名「{modelName}」。\n"
                  + "打开下面「启用模型」开关，就能在聊天页选它聊天了。", "好");
                return true;
            }

            lblDeployProgress.Text = "加载失败，改为手动引导。";
            await DisplayAlertAsync("自动加载失败", msg, "知道了");
            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TryAutoLoadWithOllama] {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 部署完成后的**使用引导**。
    /// 这一步很重要：模型文件下到手机 ≠ 能用了 —— 真正的推理由用户自己跑的服务完成。
    /// 所以要把「文件在哪、去服务里怎么加载、名字怎么填」一次说清楚，否则用户会卡在这里。
    /// </summary>
    private async Task ShowDeployDoneGuideAsync(LocalModelCatalog.Item item, LocalModelDownloader.TaskState state)
    {
        var filePath = state.TargetPath;
        var note = state.Exported
            ? "文件已放到「可被其它程序读取」的目录。"
            : "当前系统只允许 App 读这个文件（没拿到对外目录），"
              + "外部推理服务可能读不到 —— 见下方说明。";

        var msg =
            $"{item.Name} 已下载完成。\n\n" +
            $"存放位置：\n{filePath}\n\n" +
            $"{note}\n\n" +
            "━━━ 怎么让它真正跑起来 ━━━\n\n" +
            "① 在电脑（或手机）上装好推理服务，比如 Ollama。\n\n" +
            "② 让服务加载这个文件：\n" +
            "   • Ollama：把上面的 .gguf 放进 Modelfile 所在目录，写一个 Modelfile：\n" +
            "       FROM ./" + System.IO.Path.GetFileName(filePath) + "\n" +
            "     然后执行  ollama create 模型名 -f Modelfile\n" +
            "   • llama.cpp server：\n" +
            "       llama-server -m <上面的文件路径> --port 8080\n\n" +
            "③ 回到这里，填好「服务地址」，点「测试连接」，\n" +
            "   能连上后从列表里挑一个模型名。\n\n" +
            "④ 打开「启用模型」开关 → 回聊天页发一条消息试试。";

        await DisplayAlertAsync("部署完成 · 接下来这样用", msg, "我知道了");
    }

    /// <summary>下载进度事件 → App 内进度条（与通知栏同一份数据）。</summary>
    private void OnDeployProgress(LocalModelDownloader.TaskState s)
    {
        try
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                var item = CurrentDeployItem();
                // 只显示当前选中模型的进度；否则显示任意一个活动任务
                if (item != null && !string.Equals(item.FileName, s.FileName, StringComparison.OrdinalIgnoreCase))
                    return;
                ApplyDeployState(s);
            });
        }
        catch { }
    }

    private void ApplyDeployState(LocalModelDownloader.TaskState s)
    {
        deployProgressBox.IsVisible = true;
        pbDeploy.Progress = s.Fraction;
        lblDeployProgress.Text = s.Status switch
        {
            "下载中" => $"下载中 {s.ProgressText}",
            "已完成" => $"已完成 · 文件：{s.FileName}",
            "已取消" => "已取消",
            _ => $"失败：{s.Error}",
        };
        lblDeployProgress.TextColor = s.Status switch
        {
            "已完成" => Color.FromArgb("#B9D5C4"),
            "失败" => Color.FromArgb("#F2B8B5"),
            _ => Color.FromArgb("#D5C2C1"),
        };
    }

    private LocalModelCatalog.Item? CurrentDeployItem()
    {
        var i = picDeployModel.SelectedIndex;
        return i >= 0 && i < _deployItems.Count ? _deployItems[i] : null;
    }

    /// <summary>已部署模型列表：每行可「启用 / 删除」。</summary>
    private void RefreshDeployedList()
    {
        try
        {
            deployedList.Children.Clear();
            var keys = LocalModelDownloader.Deployed.Keys.ToList();
            if (keys.Count == 0)
            {
                deployedList.Children.Add(new Label
                {
                    Text = "还没有已部署的本地模型。",
                    FontSize = 11.5,
                    TextColor = Color.FromArgb("#9E8D8C"),
                });
                return;
            }

            foreach (var key in keys)
            {
                var modelKey = key;
                var name = LocalModelDownloader.FileNameToModelName(modelKey);
                var isCur = string.Equals(AppSettings.LocalModelName, name, StringComparison.OrdinalIgnoreCase);
                LocalModelDownloader.Deployed.TryGetValue(modelKey, out var filePath);
                var isPrivate = false;
                try
                {
                    isPrivate = !string.IsNullOrEmpty(filePath) &&
                                System.IO.Path.GetFullPath(System.IO.Path.GetDirectoryName(filePath) ?? "")
                                    == System.IO.Path.GetFullPath(LocalModelDownloader.ModelsDir);
                }
                catch { }

                var card = new VerticalStackLayout { Spacing = 4 };

                // 第一行：名字 + 启用 / 导出 / 删除
                var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)) };

                row.Add(new Label
                {
                    Text = (isCur ? "● " : "○ ") + name,
                    FontSize = 12.5,
                    TextColor = isCur ? AppSettings.ThemeColor : Color.FromArgb("#D5C2C1"),
                    VerticalOptions = LayoutOptions.Center,
                }, 0, 0);

                var btnUse = new Button
                {
                    Text = "启用",
                    FontSize = 11,
                    Padding = new Thickness(10, 3),
                    BackgroundColor = Color.FromArgb("#3A3333"),
                    TextColor = Colors.White,
                };
                btnUse.Clicked += (_, _) =>
                {
                    if (string.IsNullOrWhiteSpace(AppSettings.LocalModelApiUrl))
                        AppSettings.LocalModelApiUrl = AppSettings.DefaultLocalApiUrl;
                    AppSettings.LocalModelName = name;
                    AppSettings.UseLocalModel = true;
                    entLocalUrl.Text = AppSettings.LocalModelApiUrl;
                    entLocalModel.Text = name;
                    swUseLocalModel.IsToggled = true;
                    UpdateLocalModelHint();
                    RefreshDeployedList();
                };
                row.Add(btnUse, 1, 0);

                // 「导出」只在文件还在私有目录时出现（早期版本下到私有目录的补救入口）
                if (isPrivate)
                {
                    var btnExport = new Button
                    {
                        Text = "导出",
                        FontSize = 11,
                        Padding = new Thickness(10, 3),
                        BackgroundColor = Color.FromArgb("#3A3333"),
                        TextColor = Color.FromArgb("#F5D6A0"),
                    };
                    btnExport.Clicked += async (_, _) =>
                    {
                        var dst = LocalModelDownloader.ExportExisting(modelKey);
                        if (dst == null)
                        {
                            await DisplayAlertAsync("导出失败", "复制文件时出错，可能是空间不足。", "好");
                        }
                        else
                        {
                            await DisplayAlertAsync("已导出", "文件已复制到：\n\n" + dst
                                + "\n\n现在外部推理服务可以读到了。", "好");
                        }
                        RefreshDeployedList();
                        RefreshDeployInfo();
                    };
                    row.Add(btnExport, 2, 0);
                }

                var btnDel = new Button
                {
                    Text = "删除",
                    FontSize = 11,
                    Padding = new Thickness(10, 3),
                    BackgroundColor = Color.FromArgb("#3A3333"),
                    TextColor = Color.FromArgb("#F2B8B5"),
                };
                btnDel.Clicked += async (_, _) =>
                {
                    var ok = await DisplayAlertAsync("删除模型", $"确定删除 {name} 吗？文件会从本地移除。", "删除", "取消");
                    if (!ok) return;
                    LocalModelDownloader.Delete(modelKey);
                    RefreshDeployedList();
                    RefreshDeployInfo();
                };
                row.Add(btnDel, 3, 0);

                card.Add(row);

                // 第二行：真实路径（让用户能照着路径去服务里填）
                var pathLabel = new Label
                {
                    Text = string.IsNullOrEmpty(filePath) ? "（文件缺失）" : filePath,
                    FontSize = 10,
                    TextColor = Color.FromArgb("#9E8D8C"),
                    LineBreakMode = LineBreakMode.MiddleTruncation,
                };
                var pathTap = new TapGestureRecognizer();
                var pathToCopy = filePath ?? "";
                pathTap.Tapped += async (_, _) =>
                {
                    try
                    {
                        await Clipboard.Default.SetTextAsync(pathToCopy);
                        await DisplayAlertAsync("已复制路径", pathToCopy, "好");
                    }
                    catch { }
                };
                pathLabel.GestureRecognizers.Add(pathTap);
                card.Add(pathLabel);

                if (isPrivate)
                {
                    card.Add(new Label
                    {
                        Text = "在 App 私有目录，外部服务读不到 —— 点「导出」复制一份出去",
                        FontSize = 10,
                        TextColor = Color.FromArgb("#F5D6A0"),
                        LineBreakMode = LineBreakMode.WordWrap,
                    });
                }

                deployedList.Children.Add(card);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RefreshDeployedList] {ex.Message}"); }
    }

    /// <summary>刷新部署区信息文案（是否有已部署模型等）。</summary>
    private void RefreshDeployInfo()
    {
        try
        {
            if (_deployItems.Count == 0)
            {
                lblDeployInfo.Text = "点右上角「获取模型列表」，从国内加速源拉取可部署的轻量模型（≤8B、Q4_K_M）。";
                lblDeployInfo.TextColor = Color.FromArgb("#9E8D8C");
            }
            var deployed = LocalModelDownloader.Deployed.Count;
            if (deployed > 0)
            {
                var dir = LocalModelDownloader.ExportDir;
                lblLocalHint.Text = LocalModelDownloader.ExportDirIsPrivate
                    ? $"已部署 {deployed} 个模型，存放在 App 私有目录（外部服务读不到，可点「导出」）。"
                    : $"已部署 {deployed} 个模型，存放在：{dir}";
                lblLocalHint.TextColor = LocalModelDownloader.ExportDirIsPrivate
                    ? Color.FromArgb("#F5D6A0")
                    : Color.FromArgb("#B9D5C4");
            }
        }
        catch { }
    }


    // ═══════════ 本地服务地址快捷填充 / 测试 / 拉取模型名 ═══════════

    /// <summary>「本机服务」：填本机回环地址（服务就跑在手机上时用）。</summary>
    private void OnLocalUrlThisPhoneClicked(object? sender, EventArgs e)
    {
        entLocalUrl.Text = AppSettings.DefaultLocalApiUrl;
        UpdateLocalModelHint();
    }

    /// <summary>「电脑 IP 模板」：填一个可编辑的局域网模板（最常见的情形 —— 服务跑在电脑上）。</summary>
    private async void OnLocalUrlPcClicked(object? sender, EventArgs e)
    {
        try
        {
            var ip = await DisplayPromptAsync("电脑的局域网 IP",
                "填你电脑的局域网 IP（电脑上执行 ipconfig 看 IPv4 地址，一般是 192.168.x.x）。\n"
                + "只填 IP 也行，端口默认用 Ollama 的 11434。",
                "确定", "取消", initialValue: "192.168.1.", keyboard: Keyboard.Numeric);
            if (string.IsNullOrWhiteSpace(ip)) return;

            ip = ip.Trim();
            var url = ip.Contains(':')
                ? $"http://{ip}"
                : $"http://{ip}:11434";
            entLocalUrl.Text = AppSettings.NormalizeLocalApiUrl(url);
            UpdateLocalModelHint();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OnLocalUrlPcClicked] {ex.Message}"); }
    }

    /// <summary>
    /// 「测试连接」：拿当前填的地址去打一次模型列表接口。
    /// 这是让用户确认「到底通没通」的关键 —— 不通就明确说原因，别让人瞎猜。
    ///
    /// 容错策略（针对真机上常见的几种"连接失败"）：
    ///   1. 输入的是 127.0.0.1 / localhost → 直接点破：手机上这是"手机自己"，不是电脑；
    ///   2. 先试 OpenAI 兼容的 `/v1/models`；404/失败再试 Ollama 原生 `/api/tags`；
    ///   3. 报错文案把 Android 的原始 Java 异常（如 "Connection failure"）翻译成可执行建议，
    ///      并带上实际请求的地址，方便用户对照。
    /// </summary>
    private async void OnLocalTestClicked(object? sender, EventArgs e)
    {
        var raw = entLocalUrl.Text?.Trim() ?? "";
        if (!AppSettings.IsValidLocalApiUrl(raw))
        {
            await DisplayAlertAsync("地址不对", "服务地址要先填一个合法的 http/https 地址（如 http://192.168.1.100:11434）。", "好");
            return;
        }

        var probe = BuildLocalProbe(raw);

        // ① 回环地址预警：手机上 127.0.0.1 指手机自己，除非服务就跑在这台手机上，否则必然连不上
        if (probe.IsLoopback)
        {
            var go = await DisplayAlertAsync("地址可能填错了",
                "你填的是 127.0.0.1 / localhost —— 在手机上这指**手机自己**。\n\n"
                + "· 如果推理服务（Ollama 等）跑在电脑上：请改成电脑的局域网 IP，"
                + "例如 http://192.168.1.100:11434（可用「电脑 IP 模板」按钮）。\n"
                + "· 如果服务确实就跑在这台手机上：那地址没错，继续测。\n\n"
                + "要按当前地址继续测试吗？", "继续测试", "我去改");
            if (!go) return;
        }

        btnLocalTest.IsEnabled = false;
        lblLocalHint.Text = "正在测试…";
        lblLocalHint.TextColor = Color.FromArgb("#9E8D8C");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var key = entLocalKey.Text?.Trim() ?? "";
            if (key.Length > 0) http.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");

            // ② 依次尝试若干候选端点，取第一个成功的
            var tried = new List<string>();
            string? okUrl = null;
            string? okJson = null;
            int lastStatus = 0;

            foreach (var url in probe.CandidateUrls)
            {
                tried.Add(url);
                try
                {
                    using var resp = await http.GetAsync(url);
                    if (resp.IsSuccessStatusCode)
                    {
                        okUrl = url;
                        okJson = await resp.Content.ReadAsStringAsync();
                        break;
                    }
                    lastStatus = (int)resp.StatusCode;
                }
                catch (TaskCanceledException) { throw; }              // 超时 → 交给外层统一处理
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[本地测试] {url} → {ex.Message}"); }
            }

            if (okUrl is null)
            {
                var msg = tried.Count == 1
                    ? $"请求 {tried[0]} 没成功（HTTP {lastStatus}）。"
                    : $"试了 {tried.Count} 个地址都没通：\n" + string.Join("\n", tried);
                lblLocalHint.Text = lastStatus == 0
                    ? "连不上：服务没响应。检查服务是否在跑、是否同一 WiFi、防火墙是否放行。"
                    : $"连上了，但服务返回 HTTP {lastStatus}（地址或 Key 可能不对）。";
                lblLocalHint.TextColor = Color.FromArgb("#F2B8B5");
                await DisplayAlertAsync("测试失败", msg + "\n\n常见原因：\n"
                    + "① 推理服务没启动\n② 手机和电脑不在同一 WiFi\n③ 地址填错（别用 127.0.0.1）\n"
                    + "④ 电脑防火墙拦了 11434 端口\n⑤ Ollama 默认只监听 127.0.0.1，需要设成监听 0.0.0.0", "好");
                return;
            }

            var names = ParseModelNames(okJson);

            // 顺手把补全后的端点写回输入框，用户不用自己纠结要不要加 /v1
            entLocalUrl.Text = probe.ChatUrl;

            if (names.Count == 0)
            {
                lblLocalHint.Text = "连接成功！但服务没返回模型列表，请手动填模型名。";
                lblLocalHint.TextColor = Color.FromArgb("#F5D6A0");
                await DisplayAlertAsync("连接成功", "服务通了，但没解析到模型名。\n请手动填模型名（如 qwen2.5:7b）。", "好");
                return;
            }

            lblLocalHint.Text = $"连接成功，服务上有 {names.Count} 个模型。";
            lblLocalHint.TextColor = Color.FromArgb("#B9D5C4");

            // 有多个就让用户挑一个
            var pick = await DisplayActionSheetAsync("选择模型", "取消", null, names.ToArray());
            if (!string.IsNullOrWhiteSpace(pick) && pick != "取消")
            {
                entLocalModel.Text = pick;
                UpdateLocalModelHint();
            }
        }
        catch (TaskCanceledException)
        {
            lblLocalHint.Text = "连接超时。检查：服务是否在跑、手机和电脑是否同一 WiFi、防火墙是否放行、地址是否写错。";
            lblLocalHint.TextColor = Color.FromArgb("#F2B8B5");
            await DisplayAlertAsync("连接超时",
                $"等了 12 秒没响应。\n\n请求地址：{probe.ChatUrl}\n\n请确认：\n"
                + "① 推理服务真的在运行\n② 地址是电脑的局域网 IP（不是 127.0.0.1）\n"
                + "③ Ollama 需要设置成监听所有网卡（否则只能本机访问）", "好");
        }
        catch (Exception ex)
        {
            lblLocalHint.Text = "连不上：" + FriendlyNetError(ex);
            lblLocalHint.TextColor = Color.FromArgb("#F2B8B5");
            await DisplayAlertAsync("连接失败",
                $"错误：{ex.Message}\n\n请求地址：{probe.ChatUrl}\n\n{NetErrorHint(ex)}", "好");
        }
        finally
        {
            btnLocalTest.IsEnabled = true;
        }
    }

    /// <summary>
    /// 「拉取模型名」：直接拿服务上已有的模型名填进来（不用手打）。
    /// 与「测试连接」共用同一套容错（回环预警 + 多端点回退 + 友好报错）。
    /// </summary>
    private async void OnLocalFetchModelsClicked(object? sender, EventArgs e)
    {
        var raw = entLocalUrl.Text?.Trim() ?? "";
        if (!AppSettings.IsValidLocalApiUrl(raw))
        {
            await DisplayAlertAsync("先填地址", "先填好服务地址（如 http://192.168.1.100:11434），再来拉模型名。", "好");
            return;
        }

        var probe = BuildLocalProbe(raw);
        btnLocalFetchModels.IsEnabled = false;
        lblLocalHint.Text = "正在拉取模型名…";
        lblLocalHint.TextColor = Color.FromArgb("#9E8D8C");

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var key = entLocalKey.Text?.Trim() ?? "";
            if (key.Length > 0) http.DefaultRequestHeaders.Add("Authorization", $"Bearer {key}");

            List<string> names = new();
            var tried = new List<string>();

            foreach (var url in probe.CandidateUrls)
            {
                tried.Add(url);
                try
                {
                    using var resp = await http.GetAsync(url);
                    if (!resp.IsSuccessStatusCode) continue;
                    var json = await resp.Content.ReadAsStringAsync();
                    names = ParseModelNames(json);
                    if (names.Count > 0) break;
                }
                catch (TaskCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[拉取模型名] {url} → {ex.Message}"); }
            }

            if (names.Count == 0)
            {
                lblLocalHint.Text = "没拉到模型名。地址或服务可能不对。";
                lblLocalHint.TextColor = Color.FromArgb("#F2B8B5");
                await DisplayAlertAsync("没拉到",
                    "服务没返回可用的模型列表。\n\n已尝试：\n" + string.Join("\n", tried)
                    + "\n\n请确认地址正确、服务在运行、且手机与电脑在同一 WiFi。", "好");
                return;
            }

            entLocalUrl.Text = probe.ChatUrl;
            lblLocalHint.Text = $"拉到 {names.Count} 个模型。";
            lblLocalHint.TextColor = Color.FromArgb("#B9D5C4");

            var pick = await DisplayActionSheetAsync("选择模型", "取消", null, names.ToArray());
            if (!string.IsNullOrWhiteSpace(pick) && pick != "取消")
            {
                entLocalModel.Text = pick;
                UpdateLocalModelHint();
            }
        }
        catch (TaskCanceledException)
        {
            lblLocalHint.Text = "拉取超时。检查服务是否在跑、地址是否正确。";
            lblLocalHint.TextColor = Color.FromArgb("#F2B8B5");
        }
        catch (Exception ex)
        {
            lblLocalHint.Text = "拉取失败：" + FriendlyNetError(ex);
            lblLocalHint.TextColor = Color.FromArgb("#F2B8B5");
            await DisplayAlertAsync("拉取失败", $"{ex.Message}\n\n请求地址：{probe.ChatUrl}\n\n{NetErrorHint(ex)}", "好");
        }
        finally
        {
            btnLocalFetchModels.IsEnabled = true;
        }
    }

    /// <summary>一次本地探测要用到的地址信息（聊天端点 / 候选模型列表端点 / 是否回环）。</summary>
    private readonly record struct LocalProbe(string ChatUrl, List<string> CandidateUrls, bool IsLoopback);

    /// <summary>
    /// 由用户填的地址推导出：规整后的聊天端点、若干「模型列表」候选端点、以及是否指向本机回环。
    /// 候选端点按「先 OpenAI 兼容、后 Ollama 原生」的顺序排列，命中一个即可。
    /// </summary>
    private static LocalProbe BuildLocalProbe(string raw)
    {
        var chatUrl = AppSettings.NormalizeLocalApiUrl(raw);

        // 取到 host 部分（去掉 /v1/chat/completions 之类）
        var root = chatUrl;
        if (root.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            root = root[..^"/chat/completions".Length];      // → .../v1
        else
            root = root.TrimEnd('/');

        var candidates = new List<string>();
        // ① OpenAI 兼容：/v1 前面 + /models
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(root + "/models");
            candidates.Add(root[..^"/v1".Length] + "/api/tags");   // ② Ollama 原生
        }
        else
        {
            candidates.Add(root + "/v1/models");
            candidates.Add(root + "/api/tags");
        }

        bool loopback = false;
        try
        {
            if (Uri.TryCreate(chatUrl, UriKind.Absolute, out var u))
            {
                var host = u.Host.ToLowerInvariant();
                loopback = host is "127.0.0.1" or "localhost" or "::1" or "0.0.0.0";
            }
        }
        catch { }

        return new LocalProbe(chatUrl, candidates, loopback);
    }

    /// <summary>
    /// 把 Android 抛出的网络异常翻译成一句人话（原始信息常是英文 Java 异常，如 "Connection failure"）。
    /// </summary>
    private static string FriendlyNetError(Exception ex)
    {
        var m = (ex.Message ?? "").ToLowerInvariant();
        if (m.Contains("connection failure") || m.Contains("failed to connect") || m.Contains("econnrefused") || m.Contains("connection refused"))
            return "服务没响应（连接被拒绝）。多半是服务没开、或地址/端口不对。";
        if (m.Contains("timeout") || m.Contains("timed out"))
            return "连接超时。多半是地址不对、不同 WiFi、或防火墙拦截。";
        if (m.Contains("no route") || m.Contains("unreachable") || m.Contains("network is unreachable"))
            return "网络不可达。检查手机是否联网、地址是否是局域网内可达的 IP。";
        if (m.Contains("cleartext") || m.Contains("not permitted"))
            return "明文 HTTP 被系统拦了（这台设备的安全策略较严）。";
        if (m.Contains("unknown host") || m.Contains("nodename nor servname"))
            return "域名解析失败。地址可能写错了。";
        if (m.Contains("ssl") || m.Contains("handshake") || m.Contains("certificate") || m.Contains("trust anchor"))
            return "HTTPS 握手失败。本地推理服务基本都是 http，请把地址开头的 https:// 改成 http://。";
        if (m.Contains("connectexception") || m.Contains("socketexception") || m.Contains("java.net"))
            return "连接失败（网络层）。多半是服务没开、地址/端口不对、或不在同一网络。";
        return ex.Message;
    }

    /// <summary>针对不同异常类型给出可执行的排查建议（用于弹窗正文）。</summary>
    private static string NetErrorHint(Exception ex)
    {
        var m = (ex.Message ?? "").ToLowerInvariant();
        if (m.Contains("ssl") || m.Contains("handshake") || m.Contains("certificate") || m.Contains("trust anchor"))
            return "排查建议：把地址改成 http:// 开头（本地推理服务默认不启用 HTTPS）。";
        if (m.Contains("connection failure") || m.Contains("refused") || m.Contains("failed to connect") || m.Contains("java.net"))
            return "排查建议：\n① 确认推理服务已经启动\n② 确认地址是运行服务那台设备的局域网 IP\n"
                 + "③ 若服务跑在电脑上，Ollama 默认只允许本机访问，需设置 OLLAMA_HOST=0.0.0.0 后重启\n"
                 + "④ 确认手机和电脑连的是同一个 WiFi\n⑤ 电脑防火墙放行 11434 端口";
        if (m.Contains("timeout") || m.Contains("timed out"))
            return "排查建议：\n① 电脑防火墙放行 11434 端口\n② 确认手机与电脑在同一网段\n"
                 + "③ 关掉电脑上的代理/VPN 再试";
        if (m.Contains("no route") || m.Contains("unreachable"))
            return "排查建议：确认手机已联网，且填的是局域网内可达的 IP（不是外网地址）。";
        return "排查建议：\n① 确认服务已启动\n② 确认地址与端口正确\n③ 确认手机与电脑同一 WiFi";
    }

    /// <summary>从 /models 的响应里解析出模型名（兼容 OpenAI / Ollama / LM Studio 等）。</summary>
    private static List<string> ParseModelNames(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != System.Text.Json.JsonValueKind.Array)
                return list;

            foreach (var m in data.EnumerateArray())
            {
                if (m.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                // OpenAI 风格是 "id"；Ollama 的 /v1/models 也是 "id"
                if (m.TryGetProperty("id", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var s = id.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && !list.Contains(s)) list.Add(s!);
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>刷新本地模型状态提示：配置完不完整、有没有真启用。</summary>
    private void UpdateLocalModelHint()
    {
        try
        {
            var deployed = LocalModelDownloader.Deployed.Count;
            if (!AppSettings.LocalModelConfigured)
            {
                lblLocalHint.Text = deployed > 0
                    ? $"已部署 {deployed} 个模型但还没配置完：把服务地址和模型名填上。"
                    : "还没配置完：地址和模型名都填上才算配好。也可以先在上面「获取模型列表」部署一个。";
                lblLocalHint.TextColor = Color.FromArgb("#F2B8B5");
            }
            else if (AppSettings.UseLocalModel)
            {
                lblLocalHint.Text = $"已启用。聊天请求会打到 {AppSettings.LocalModelApiUrl}，模型 {AppSettings.LocalModelName}。";
                lblLocalHint.TextColor = Color.FromArgb("#B9D5C4");
            }
            else
            {
                lblLocalHint.Text = "已配置但未启用。打开上面的开关，聊天就会走本地模型。";
                lblLocalHint.TextColor = Color.FromArgb("#9E8D8C");
            }
        }
        catch { }
    }
}
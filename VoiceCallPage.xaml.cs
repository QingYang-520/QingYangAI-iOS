using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace 青阳AI;

/// <summary>
/// 语音通话模式（全屏）。
///
/// 和「按住说话」的差别：这里麦克风是**一直开着**的，靠 PcmRecorder 的能量 VAD 自动断句，
/// 说一句 → 自动转写 → 自动发送 → 流式收回复 → TTS 播报 → 自动回到监听。
/// 用户不需要点任何按钮，全程只在想挂断时点一下。
///
/// ─────────────────── 状态机 ───────────────────
///   Listening（监听中，绿色呼吸）
///     └ 检测到人声 → Speaking（跟随音量波动）
///          └ 静音 800ms 断句 → Transcribing（转圈）
///               └ 转写成功 → Thinking（流式收回复中）
///                    └ 收到首字 → Speaking（她开始播报）
///                         └ 播报完 → Listening（延迟 300ms 再开麦）
///   任何环节出错 → 回到 Listening（状态文字给一句人话），绝不把页面卡死。
///
/// ─────────────────── 打断逻辑（重点）───────────────────
/// 难点：她播报的声音会从扬声器漏进麦克风，如果照常做 VAD，就会「自己打断自己」。
/// 做法是两套门限 + 两个独立事件：
///   · 播报期间 PcmRecorder 进「抑制态」：一个字都不累积（不会把自己的声音送去转写），
///     但继续读麦克风，用更高的 InterruptRmsThreshold 判「用户是不是在说话」；
///   · 一旦判定用户开口 → PcmRecorder.Interrupted → 立刻 TtsPlayer.Stop()，
///     然后退出抑制态，重新开始正常听（用户刚说的那句从头录）。
///   · 正常监听期间开口 → PcmRecorder.SpeechStarted → 同样先 Stop 掉可能还在播的 TTS 尾巴。
/// 这样既满足「播报时暂停录音」，又满足「用户开口能打断播报」。
/// </summary>
public partial class VoiceCallPage : ContentPage
{
    // ────────────────────────── 常量 ──────────────────────────

    /// <summary>播报结束后延迟多久再开麦（毫秒）——给扬声器和系统音频回路一点余量。</summary>
    private const int ResumeListenDelayMs = 300;

    /// <summary>单轮对话最多等多久（秒），超时就当失败，回到监听。</summary>
    private const int TurnTimeoutSec = 90;

    // ────────────────────────── 依赖 ──────────────────────────

    private static readonly HttpClient _http = CreateHttpClient();

    private readonly PcmRecorder _recorder = new();

    /// <summary>页面是否已经关闭（关闭后所有回调都要立刻退出，不能再碰 UI / 麦克风）。</summary>
    private volatile bool _closed;

    /// <summary>是否正在处理一轮对话（转写/请求/播报）。true 时不接受新的断句结果。</summary>
    private volatile bool _busy;

    /// <summary>是否已经拿到麦克风权限并成功开麦。</summary>
    private bool _micReady;

    /// <summary>本次通话的对话历史（只用于请求上下文，入库仍走 ChatStore）。</summary>
    private readonly List<(bool IsUser, string Content)> _turns = new();

    /// <summary>呼吸动画的取消标记（切状态时换一个新的）。</summary>
    private CancellationTokenSource? _breathCts;

    /// <summary>大圆环的绘制器（颜色/音量/旋转相位由状态机写入）。</summary>
    private readonly RingPainter _ring;

    /// <summary>圆环重绘计时器（转圈/呼吸要靠它推进相位，30fps 够顺也不费电）。</summary>
    private IDispatcherTimer? _ringTimer;

    /// <summary>「等播报念完」的可取消标记：用户开口打断时取消它，立刻恢复监听。</summary>
    private CancellationTokenSource? _playbackCts;

    /// <summary>当前状态（驱动动画与文案）。后台线程也会读它，所以标 volatile。</summary>
    private volatile CallState _state = CallState.Connecting;

    /// <summary>通话状态。</summary>
    private enum CallState
    {
        Connecting,    // 准备中
        Listening,     // 监听中
        Speaking,      // 用户正在说
        Transcribing,  // 转写中
        Thinking,      // 等回复
        AiSpeaking,    // 她在播报
        Error,         // 出错了（短暂显示后回监听）
    }

    // ────────────────────────── 构造 / 生命周期 ──────────────────────────

    public VoiceCallPage()
    {
        InitializeComponent();

        // 全屏沉浸：不显示系统导航栏
        NavigationPage.SetHasNavigationBar(this, false);

        try { ThemeManager.Apply(this); } catch { }

        // 呼吸圆环：交给自绘的 Painter（GraphicsView 直接实现 IDrawable 会撞方法隐藏，所以拆个类）
        _ring = new RingPainter();
        breathView.Drawable = _ring;

        LoadAvatar();
        LoadMood();

        // 录音事件挂上（都在后台线程触发，UI 一律走 MainThread）
        _recorder.SpeechStarted += OnSpeechStarted;
        _recorder.SpeechEnded += OnSpeechEnded;
        _recorder.VolumeChanged += OnVolumeChanged;
        _recorder.Interrupted += OnUserInterrupted;

        Loaded += (_, _) =>
        {
            try { JellyAnim.EnterPage(Content); } catch { }
            StartRingTimer();
            _ = StartAsync();
        };
    }

    /// <summary>
    /// 页面消失就收工：停录音、停 TTS。
    /// 注意：必须两处都处理：OnDisappearing 在「返回上一页」时触发；
    /// OnNavigatedFrom 兜底那些不走 Disappearing 的机型（华为/荣耀部分 ROM 实测会漏）。
    /// </summary>
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        Shutdown();
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        Shutdown();
    }

    /// <summary>彻底收工：幂等，重复调用安全。</summary>
    private void Shutdown()
    {
        if (_closed) return;
        _closed = true;

        try { _breathCts?.Cancel(); } catch { }
        CancelPlaybackWait();
        StopRingTimer();

        try
        {
            _recorder.SpeechStarted -= OnSpeechStarted;
            _recorder.SpeechEnded -= OnSpeechEnded;
            _recorder.VolumeChanged -= OnVolumeChanged;
            _recorder.Interrupted -= OnUserInterrupted;
            _recorder.Stop();
            _recorder.Dispose();
        }
        catch { }

        // 页面退出必须掐掉播报，否则退出通话页还在念
        try { TtsPlayer.Stop(); } catch { }
    }

    // ────────────────────────── 启动 ──────────────────────────

    /// <summary>申请权限 → 开麦 → 进入监听。</summary>
    private async Task StartAsync()
    {
        try
        {
            SetState(CallState.Connecting, "正在准备麦克风…");

            var status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlertAsync("语音通话", "没有麦克风权限，无法通话。请到系统设置里允许录音权限。", "好");
                SetState(CallState.Error, "没有麦克风权限");
                return;
            }

            // 没有配听觉模型时，说完了也转写不出来 —— 提前说清楚，别让用户对空气说话
            if (!AppSettings.AudioEnabled)
            {
                await DisplayAlertAsync("语音通话",
                    "还没配置「听觉模型」（设置 → 模型配置 → 语音识别）。\n没有它，你说的话转不成文字。",
                    "好");
            }

            if (_closed) return;

            if (!_recorder.Start())
            {
                SetState(CallState.Error, "麦克风打不开（可能被其他应用占用）");
                return;
            }

            _micReady = true;
            SetState(CallState.Listening, "我在听，你说话就好");
        }
        catch (Exception ex)
        {
            SetState(CallState.Error, "启动失败：" + ex.Message);
        }
    }

    // ────────────────────────── 录音事件回调 ──────────────────────────

    /// <summary>用户开口：先把可能还在播的 TTS 尾巴掐掉（这就是"打断"）。</summary>
    private void OnSpeechStarted()
    {
        if (_closed) return;

        // 正在播报 / 播报尾巴还在 → 立刻打断
        try { TtsPlayer.Stop(); } catch { }
        CancelPlaybackWait();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_closed) return;
            if (_busy) return;   // 正在转写/请求时不要抢状态文字
            SetState(CallState.Speaking, "我在听…");
        });
    }

    /// <summary>一句话说完了：攒够时长就进入一轮处理。</summary>
    private void OnSpeechEnded(byte[] pcm)
    {
        if (_closed || _busy) return;
        if (!_micReady) return;                         // 麦克风没起来（权限/占用）就别处理
        if (pcm == null || pcm.Length < 2000) return;   // 太短，噪声

        _busy = true;
        _ = Task.Run(() => HandleUtteranceAsync(pcm));
    }

    /// <summary>实时音量：只在"用户在说"时驱动圆环大小，其余状态由动画自己管。</summary>
    private void OnVolumeChanged(double volume)
    {
        if (_closed) return;
        if (_state != CallState.Speaking) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_closed || _state != CallState.Speaking) return;
            try
            {
                // 0~1 → 1.0~1.35 的缩放，跟着音量轻微起伏（不做夸张的大起大落）
                breathView.Scale = 1.0 + Math.Clamp(volume, 0, 1) * 0.35;

                // 同时让圆环本身也随音量起伏（内圈半径跟着变，视觉上更"活"）
                _ring.Volume = Math.Clamp(volume, 0, 1);
                SafeInvalidate();
            }
            catch { }
        });
    }

    /// <summary>播报期间用户开口打断（PcmRecorder 的高门限判定）。</summary>
    private void OnUserInterrupted()
    {
        if (_closed) return;

        // 立刻闭嘴
        try { TtsPlayer.Stop(); } catch { }
        CancelPlaybackWait();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_closed) return;
            // 打断后她"话没说完"，这一轮就算结束了：回监听，让用户把话说完
            if (_state == CallState.AiSpeaking)
                SetState(CallState.Speaking, "嗯？你说…");
        });
    }

    /// <summary>
    /// 取消「等播报念完」的等待（用户打断时调）。
    /// 不取消的话，HandleUtteranceAsync 会傻等完估算时长才恢复录音，
    /// 用户打断后要愣好几秒才听得到人说话。
    /// </summary>
    private void CancelPlaybackWait()
    {
        try { _playbackCts?.Cancel(); } catch { }
    }

    // ────────────────────────── 一轮对话 ──────────────────────────

    /// <summary>
    /// 处理一句完整的话：转写 → 入库 → 请求 → 播报 → 回监听。
    /// 全程在后台线程，UI 操作都走 MainThread。
    /// </summary>
    private async Task HandleUtteranceAsync(byte[] pcm)
    {
        string wavPath = "";
        try
        {
            // ① 转写
            SetStateOnUi(CallState.Transcribing, "让我听听你说了什么…");

            wavPath = Path.Combine(FileSystem.CacheDirectory ?? ".",
                $"call_{Guid.NewGuid():N}.wav");
            await PcmRecorder.SaveWavAsync(pcm, wavPath);

            var text = await TranscribeAsync(wavPath);
            if (_closed) return;

            if (string.IsNullOrWhiteSpace(text))
            {
                // 没听清：不打扰用户，回监听让他重说
                SetStateOnUi(CallState.Listening, "没听清，你再说一遍？");
                return;
            }

            text = text.Trim();

            // ② 入库（用户这条）
            _turns.Add((true, text));
            try { await ChatStore.Instance.InsertUserMessageAsync(text); } catch { }

            ShowLastLineOnUi("你：" + text);

            // ③ 请求回复（流式）
            SetStateOnUi(CallState.Thinking, "我想想…");
            var reply = await RequestReplyAsync(text);
            if (_closed) return;

            if (string.IsNullOrWhiteSpace(reply))
            {
                SetStateOnUi(CallState.Listening, "我走神了，你再说一次？");
                return;
            }

            reply = reply.Trim();

            // ④ 入库（她这条）
            _turns.Add((false, reply));
            try { await ChatStore.Instance.InsertAiMessageAsync(reply); } catch { }

            ShowLastLineOnUi("她：" + reply);

            // ⑤ 播报：播报期间抑制录音（防止把自己念的话录进去）
            if (AppSettings.TtsEnabled)
            {
                SetStateOnUi(CallState.AiSpeaking, "…");
                _recorder.Pause();      // 抑制态：不累积，但能听出用户打断

                bool playing = false;
                try
                {
                    await TtsPlayer.PlayAsync(reply);
                    playing = true;
                }
                catch { /* 播报失败不影响下一轮 */ }

                if (_closed) return;

                if (playing)
                {
                    // 注意：TtsPlayer.PlayAsync 只等到「开始播放」就返回了（音频还在放）。
                    //    所以这里按字数估算它念完要多久，等完了才恢复录音；
                    //    用可取消的等待，用户开口打断时能立刻中断这段等待。
                    var playCts = new CancellationTokenSource();
                    _playbackCts = playCts;
                    try
                    {
                        await Task.Delay(EstimateSpeechMs(reply), playCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 用户打断了播报：不再等，直接往下恢复监听
                    }
                    finally
                    {
                        _playbackCts = null;
                        try { playCts.Dispose(); } catch { }
                    }
                }

                if (_closed) return;

                // 播完再等一下，避免扬声器余音被当成用户开口
                try { await Task.Delay(ResumeListenDelayMs); } catch { }
                if (_closed) return;

                _recorder.Resume();
            }

            // ⑥ 回监听
            SetStateOnUi(CallState.Listening, "我在听，你说话就好");
        }
        catch (Exception ex)
        {
            if (!_closed)
                SetStateOnUi(CallState.Listening, "出了点问题：" + ShortMsg(ex.Message));
        }
        finally
        {
            // 抑制态一定要退出，否则下一轮永远听不到人说话
            try { if (!_closed) _recorder.Resume(); } catch { }
            _busy = false;

            try { if (File.Exists(wavPath)) File.Delete(wavPath); } catch { }
        }
    }

    /// <summary>
    /// 估算这段文字念完要多久（毫秒）。
    ///
    /// 为什么要估：TtsPlayer.PlayAsync 内部只等到 MediaPlayer 开始播就返回了，
    /// 并没有"播完"的回调给我们用。不估的话，我们会在她刚开口时就恢复录音，
    /// 结果把她念的话全录进去送去转写 —— 这是语音通话最典型的自激 bug。
    ///
    /// 估算口径：中文正常语速约 4~5 字/秒，取 4.5 字/秒（≈222ms/字），
    /// 再加 600ms 的接口/解码余量。宁可多等一会儿，也不要录到自己的声音。
    /// </summary>
    private static int EstimateSpeechMs(string text)
    {
        if (string.IsNullOrEmpty(text)) return 800;
        int chars = text.Count(c => !char.IsWhiteSpace(c));
        // 222ms/字 + 600ms 余量，上限 40 秒（TTS 单次输入也有上限，不会无限长）
        return Math.Clamp(chars * 222 + 600, 800, 40000);
    }

    // ────────────────────────── 转写 ──────────────────────────

    /// <summary>
    /// 调听觉模型 /audio/transcriptions 转写（OpenAI 兼容 multipart）。
    /// 写法对齐 ChatPage.TranscribeWithAudioModelAsync，只是音频换成 WAV。
    /// </summary>
    private async Task<string?> TranscribeAsync(string wavPath)
    {
        if (!AppSettings.AudioEnabled)
            throw new Exception("未配置听觉模型");

        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.AudioApiUrl);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AppSettings.AudioApiKey}");

        var fileContent = new StreamContent(File.OpenRead(wavPath));
        // 注意：这里必须是 audio/wav（PcmRecorder 封的是 WAV，不是 m4a）
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent();
        form.Add(fileContent, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent(
            string.IsNullOrWhiteSpace(AppSettings.AudioModel) ? "whisper-1" : AppSettings.AudioModel), "model");
        req.Content = form;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var resp = await _http.SendAsync(req, cts.Token);
        await EnsureOkAsync(resp);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("text", out var t) ? t.GetString() : null;
    }

    // ────────────────────────── 请求回复（流式）──────────────────────────

    /// <summary>
    /// 流式请求对话模型，返回完整回复文本。
    /// 语音场景只要最终文本（要拿去 TTS），所以流式在这里的价值是**更早拿到内容**、
    /// 以及和聊天页共用同一套端点/鉴权/思考档位配置。
    /// </summary>
    private async Task<string> RequestReplyAsync(string userText)
    {
        if (string.IsNullOrWhiteSpace(AppSettings.EffectiveChatApiUrl))
            throw new Exception("还没配置 API 地址");

        // 通话也带上感知上下文，让她知道"此刻"的情况（和聊天页一致的注入方式）
        var system = AppSettings.BuildSystemPrompt();
        try
        {
            var device = await DeviceContextService.BuildAsync();
            if (!string.IsNullOrWhiteSpace(device))
                system += "\n\n【你此刻能看到的用户状态】\n" + device;
        }
        catch { }

        // 通话模式的额外约束：这是"说话"不是"打字"，必须短、必须口语
        system += "\n\n【当前是语音通话】\n"
                + "你现在是在跟TA打电话，你说的话会被直接念出来（TTS）。所以：\n"
                + "1. 极短：一句话，最多两句话，20 字以内为佳。别念列表、别念符号、别念 Markdown。\n"
                + "2. 口语：像真人打电话那样自然，可以有「嗯」「诶」「那个」这种语气词。\n"
                + "3. 不要在正文里输出任何 {xxx:\"...\"} 指令 —— 通话模式下指令不生效，念出来会很怪。\n"
                + "4. 数字、英文缩写尽量口语化（把「3.14」说成「三点一四」这种念法）。";

        var messages = new List<object> { new { role = "system", content = system } };

        // 带最近几轮通话上下文（不要全量，通话场景够用且省 token）。
        // 注意：必须跳过**最后一条**：它就是本轮刚说过的这句（调用前已经 add 进 _turns 了），
        //    下面会作为 userText 单独追加，不跳过就会在请求里出现两遍「用户：xxx」。
        var history = new List<(bool IsUser, string Content)>();
        if (_turns.Count > 1) history.AddRange(_turns.Take(_turns.Count - 1).TakeLast(10));
        foreach (var (isUser, content) in history)
            messages.Add(new { role = isUser ? "user" : "assistant", content });
        messages.Add(new { role = "user", content = userText });

        object body = AppSettings.UseLocalModel && AppSettings.LocalModelConfigured
            ? new { model = AppSettings.LocalModelName, messages, stream = true }
            : new
            {
                model = AppSettings.ResolveChatModel(AppSettings.ForceThinking),
                messages,
                stream = true,
                max_tokens = 400,
                reasoning_effort = AppSettings.ForceThinking ? "high" : "none"
            };

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, AppSettings.EffectiveChatApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var key = AppSettings.EffectiveChatApiKey?.Trim() ?? "";
        if (key.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TurnTimeoutSec));
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await EnsureOkAsync(resp);

        var sb = new StringBuilder();
        using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);

        while (!_closed)
        {
            string? line;
            try { line = await reader.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            if (line == null) break;

            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("choices", out var choices)) continue;
                if (choices.GetArrayLength() == 0) continue;
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var c))
                {
                    var piece = c.GetString();
                    if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
                }
            }
            catch { /* 单块解析失败不影响整体 */ }
        }

        // 去掉可能漏出来的隐藏指令（通话模式下不执行指令，但要防止被念出来）
        var text = InstructionParser.RemoveInstructions(sb.ToString());
        return text;
    }

    // ────────────────────────── 状态与动画 ──────────────────────────

    /// <summary>切状态 + 文案（主线程安全）。</summary>
    private void SetStateOnUi(CallState state, string status)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_closed) return;
            SetState(state, status);
        });
    }

    /// <summary>切状态：更新文案、圆环颜色、动画形态。</summary>
    private void SetState(CallState state, string status)
    {
        _state = state;

        try
        {
            lblStatus.Text = status;

            // 圆环主色：监听绿 / 说话主题色 / 转写蓝 / 播报粉 / 出错红
            var color = state switch
            {
                CallState.Listening => Color.FromArgb("#4ADE80"),   // 绿：在听
                CallState.Speaking => AppSettings.ThemeColor,        // 主题色：跟着你的声音
                CallState.Transcribing => Color.FromArgb("#60A5FA"), // 蓝：在想
                CallState.Thinking => Color.FromArgb("#60A5FA"),
                CallState.AiSpeaking => Color.FromArgb("#FBB5B2"),   // 粉：她在说
                CallState.Error => Color.FromArgb("#E5484D"),        // 红
                _ => Color.FromArgb("#9E8D8C")
            };

            // 告诉绘制器当前形态（决定画实心圆、转圈弧、还是静止）
            _ring.Color = color;
            _ring.Mode = state switch
            {
                CallState.Transcribing or CallState.Thinking => RingPainter.RingMode.Spinning,
                CallState.AiSpeaking => RingPainter.RingMode.Steady,
                CallState.Speaking => RingPainter.RingMode.Volume,
                CallState.Error => RingPainter.RingMode.Steady,
                _ => RingPainter.RingMode.Breathing
            };
            _ring.Volume = 0;
            SafeInvalidate();

            StartBreathAnimation(state);
        }
        catch { }
    }

    /// <summary>重绘圆环（异常吞掉：画崩了也不能影响通话）。</summary>
    private void SafeInvalidate()
    {
        try { breathView.Invalidate(); } catch { }
    }

    /// <summary>
    /// 启动圆环重绘计时器。相位靠每帧 Invalidate 推进 —— GraphicsView 不会自己动，
    /// 没人叫它重绘，Spinning 的弧就永远停在原处（和 DotMotionLoader 一个道理）。
    /// </summary>
    private void StartRingTimer()
    {
        if (_ringTimer != null) return;
        try
        {
            _ringTimer = Dispatcher.CreateTimer();
            _ringTimer.Interval = TimeSpan.FromMilliseconds(33);   // ~30fps
            _ringTimer.Tick += (_, _) =>
            {
                if (_closed) { StopRingTimer(); return; }
                try
                {
                    _ring.Phase = (_ring.Phase + 0.02) % 1.0;
                    breathView.Invalidate();
                }
                catch { }
            };
            _ringTimer.Start();
        }
        catch { }
    }

    /// <summary>停掉圆环重绘（页面关闭时）。</summary>
    private void StopRingTimer()
    {
        try { _ringTimer?.Stop(); } catch { }
        _ringTimer = null;
    }

    /// <summary>
    /// 大圆环动画：按状态给不同"呼吸"。
    /// 全部用 ...Async 版本（MAUI 10 里 FadeTo/ScaleTo/TranslateTo 的同步版已废弃）。
    /// </summary>
    private void StartBreathAnimation(CallState state)
    {
        // 先掐掉上一个动画
        try { _breathCts?.Cancel(); } catch { }
        var cts = new CancellationTokenSource();
        _breathCts = cts;
        var token = cts.Token;

        try { breathView.Scale = 1.0; } catch { }

        _ = Task.Run(async () =>
        {
            try
            {
                switch (state)
                {
                    case CallState.Listening:
                        // 缓慢呼吸：1.0 ↔ 1.12，2 秒一个来回
                        await BreathLoopAsync(token, 1.0, 1.12, 1000);
                        break;

                    case CallState.Speaking:
                        // 跟随音量：由 OnVolumeChanged 直接写 Scale，这里只做兜底呼吸
                        await BreathLoopAsync(token, 1.02, 1.08, 600);
                        break;

                    case CallState.Transcribing:
                    case CallState.Thinking:
                        // 转圈感：快速小幅度脉动
                        await BreathLoopAsync(token, 0.96, 1.04, 320);
                        break;

                    case CallState.AiSpeaking:
                        // 静止（她在说话，圆环不抢戏）
                        await MainThread.InvokeOnMainThreadAsync(() =>
                        {
                            if (!token.IsCancellationRequested) breathView.Scale = 1.0;
                        });
                        break;

                    default:
                        await BreathLoopAsync(token, 0.98, 1.02, 1400);
                        break;
                }
            }
            catch { }
        });
    }

    /// <summary>呼吸循环：在 from ↔ to 之间来回缩放，直到取消。</summary>
    private async Task BreathLoopAsync(CancellationToken token, double from, double to, uint ms)
    {
        while (!token.IsCancellationRequested && !_closed)
        {
            try
            {
                // 用 async lambda 包一层：明确落到 InvokeOnMainThreadAsync(Func<Task>) 重载，
                // 而且这样 await 会真的等到动画播完（ScaleToAsync 返回的 Task 在动画结束时完成）
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await breathView.ScaleToAsync(to, ms, Easing.SinInOut);
                });
                if (token.IsCancellationRequested) return;

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    await breathView.ScaleToAsync(from, ms, Easing.SinInOut);
                });
            }
            catch { return; }
        }
    }

    // ────────────────────────── UI 小工具 ──────────────────────────

    /// <summary>顶部头像：有自画像就显示，没有就留主题色占位 + 名字首字。</summary>
    private void LoadAvatar()
    {
        try
        {
            var src = AvatarService.Load();
            if (src != null)
            {
                imgAvatar.Source = src;
                imgAvatar.IsVisible = true;
                lblAvatarFallback.IsVisible = false;
            }
            else
            {
                var name = AppSettings.AiName;
                lblAvatarFallback.Text = string.IsNullOrWhiteSpace(name) ? "青" : name[..1];
            }

            lblName.Text = AppSettings.AiName;

            // 头像外框跟着主题色
            avatarFrame.Stroke = new SolidColorBrush(AppSettings.ThemeColor);
        }
        catch { }
    }

    /// <summary>心情：直接读 InnerLifeService（和聊天页标题栏同源，纯文字不用 emoji）。</summary>
    private void LoadMood()
    {
        try
        {
            var mood = InnerLifeService.Mood;
            lblMood.Text = string.IsNullOrWhiteSpace(mood) ? "" : mood;
        }
        catch { lblMood.Text = ""; }
    }

    /// <summary>底部最近一轮对话回显。</summary>
    private void ShowLastLineOnUi(string text)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_closed) return;
            try { lblLastLine.Text = text; } catch { }
        });
    }

    // ────────────────────────── 挂断 ──────────────────────────

    private async void OnHangupTapped(object? sender, EventArgs e)
    {
        Shutdown();
        try
        {
            if (Navigation.NavigationStack.Count > 1) await Navigation.PopAsync();
        }
        catch { }
    }

    // ────────────────────────── 网络小工具 ──────────────────────────

    private static HttpClient CreateHttpClient()
    {
        try
        {
            if (AppSettings.UseManagedHttpStack)
                return new HttpClient(new System.Net.Http.SocketsHttpHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.All,
                    ConnectTimeout = TimeSpan.FromSeconds(30)
                })
                { Timeout = TimeSpan.FromMinutes(5) };
        }
        catch { }
        return new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    /// <summary>非 2xx 时捞服务商正文再抛（和聊天页一致）。</summary>
    private static Task EnsureOkAsync(HttpResponseMessage resp) => ApiErrors.EnsureOkAsync(resp);

    /// <summary>错误文案压缩成一行，别把状态栏撑爆。</summary>
    private static string ShortMsg(string msg)
    {
        msg = (msg ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return msg.Length > 60 ? msg[..60] + "…" : msg;
    }

    // ══════════════════════ 大圆环绘制器 ══════════════════════

    /// <summary>
    /// 通话页中间那个大圆环。
    ///
    /// 为什么单独写个 IDrawable 而不让 GraphicsView 自己实现：
    /// GraphicsView 已经实现了 IDrawable，直接 `Drawable = this` 会撞上方法隐藏（CS0108），
    /// 项目里 DotMotionLoader 也踩过这个坑，所以照它的做法拆成独立 Painter。
    ///
    /// 四种形态：
    ///   Breathing 监听中 —— 三层半透明同心圆，外圈缓慢扩张
    ///   Volume    录音中 —— 内圈半径跟随音量 0~1 涨落
    ///   Spinning  转写中 —— 外圈画一段缺口弧，绕圈转（"在思考"的通用语汇）
    ///   Steady    播报中 —— 静止的实心圆，不抢戏
    /// </summary>
    private sealed class RingPainter : IDrawable
    {
        public enum RingMode { Breathing, Volume, Spinning, Steady }

        /// <summary>主色（由状态机写入）。</summary>
        public Color Color { get; set; } = Color.FromArgb("#4ADE80");

        /// <summary>当前形态。</summary>
        public RingMode Mode { get; set; } = RingMode.Breathing;

        /// <summary>实时音量 0~1（仅 Volume 形态用）。</summary>
        public double Volume { get; set; }

        /// <summary>动画相位 0~1（由外层计时器每帧推进，Spinning 用它转圈）。</summary>
        public double Phase { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            try
            {
                float size = Math.Min(rect.Width, rect.Height);
                if (size <= 4) return;

                float cx = rect.X + rect.Width / 2f;
                float cy = rect.Y + rect.Height / 2f;
                float maxR = size / 2f - 4f;

                // 相位由外层计时器统一推进（见 StartRingTimer），这里只负责画

                switch (Mode)
                {
                    case RingMode.Spinning:
                        DrawSpinning(canvas, cx, cy, maxR);
                        break;
                    case RingMode.Volume:
                        DrawVolume(canvas, cx, cy, maxR);
                        break;
                    case RingMode.Steady:
                        DrawSteady(canvas, cx, cy, maxR);
                        break;
                    default:
                        DrawBreathing(canvas, cx, cy, maxR);
                        break;
                }
            }
            catch { /* 画崩了也不该影响通话 */ }
        }

        /// <summary>监听中：三层同心圆，越外越淡，外圈随相位缓慢扩张。</summary>
        private void DrawBreathing(ICanvas canvas, float cx, float cy, float maxR)
        {
            // 呼吸系数：0.9 ~ 1.0 缓慢来回（用 sin 保证平滑）
            float breath = 0.9f + 0.1f * (float)((Math.Sin(Phase * Math.PI * 2) + 1) / 2);

            // ① 最外层光晕
            canvas.FillColor = Color.WithAlpha(0.10f);
            canvas.FillCircle(cx, cy, maxR * breath);

            // ② 中间层
            canvas.FillColor = Color.WithAlpha(0.20f);
            canvas.FillCircle(cx, cy, maxR * 0.72f * breath);

            // ③ 核心实心圆
            canvas.FillColor = Color.WithAlpha(0.85f);
            canvas.FillCircle(cx, cy, maxR * 0.42f);

            // ④ 中心留个深色小孔，看起来像"呼吸的球"而不是一坨色块
            canvas.FillColor = Color.FromArgb("#141420");
            canvas.FillCircle(cx, cy, maxR * 0.18f);
        }

        /// <summary>录音中：内圈半径跟随音量 0~1 涨落。</summary>
        private void DrawVolume(ICanvas canvas, float cx, float cy, float maxR)
        {
            float v = (float)Math.Clamp(Volume, 0, 1);

            // 外圈：固定的淡色轮廓，给个"容器"感
            canvas.StrokeColor = Color.WithAlpha(0.28f);
            canvas.StrokeSize = 2f;
            canvas.DrawCircle(cx, cy, maxR * 0.92f);

            // 光晕：跟着音量涨
            canvas.FillColor = Color.WithAlpha(0.12f + 0.18f * v);
            canvas.FillCircle(cx, cy, maxR * (0.62f + 0.30f * v));

            // 核心圆：音量越大越亮越大
            canvas.FillColor = Color.WithAlpha(0.70f + 0.30f * v);
            canvas.FillCircle(cx, cy, maxR * (0.30f + 0.22f * v));
        }

        /// <summary>转写/思考中：外圈一段缺口弧绕圈转。</summary>
        private void DrawSpinning(ICanvas canvas, float cx, float cy, float maxR)
        {
            float r = maxR * 0.88f;

            // ① 底圈：极淡的完整圆，表示"这里有个圈"
            canvas.StrokeColor = Color.WithAlpha(0.18f);
            canvas.StrokeSize = 5f;
            canvas.DrawCircle(cx, cy, r);

            // ② 转动的弧：占 1/4 圆周，随 Phase 绕圈
            canvas.StrokeColor = Color.WithAlpha(0.95f);
            canvas.StrokeSize = 5f;
            canvas.StrokeLineCap = LineCap.Round;

            float startAngle = (float)(Phase * 360.0);
            canvas.DrawArc(cx - r, cy - r, r * 2, r * 2,
                startAngle, startAngle + 90,
                false, false);

            // ③ 中心小实心圆
            canvas.FillColor = Color.WithAlpha(0.75f);
            canvas.FillCircle(cx, cy, maxR * 0.16f);
        }

        /// <summary>播报中：静止实心圆（她说话时圆环不抢戏）。</summary>
        private void DrawSteady(ICanvas canvas, float cx, float cy, float maxR)
        {
            canvas.FillColor = Color.WithAlpha(0.16f);
            canvas.FillCircle(cx, cy, maxR * 0.90f);

            canvas.FillColor = Color.WithAlpha(0.55f);
            canvas.FillCircle(cx, cy, maxR * 0.55f);

            canvas.FillColor = Color.WithAlpha(0.90f);
            canvas.FillCircle(cx, cy, maxR * 0.26f);
        }
    }
}

using System.Diagnostics;

namespace 青阳AI;

/// <summary>
/// 实时录音 + 能量 VAD 断句器（语音通话模式专用）。
///
/// 和「点一下录一段」的录音不同，通话模式要的是**永不停止的听**：
/// 一直读麦克风 → 算能量 → 检测到人声就开始攒 → 说完了（连续静音）就吐出一整句 PCM。
/// 上层拿到一段完整的话就去转写，转写期间本类继续听（被打断也不丢）。
///
/// 格式固定 16000Hz / 单声道 / 16bit PCM —— 这是 whisper 系列转写模型最稳的输入格式，
/// 直接封个 WAV 头就能上传，不用再转码（省一次 ffmpeg 依赖）。
///
/// 注意：只支持 Android：AudioRecord 是平台 API，非 Android 平台本类为空实现。
/// 注意：本文件在项目根目录（不是 Platforms/Android），所以必须自己用 #if ANDROID 隔离，
///    否则 Windows 目标编译时找不到 Android.Media.* 。
/// 注意：Platforms/Android 的老坑同样适用：这里一律写 global::Android.* 全限定名，
///    因为 AudioRecord 之外的 View/Paint/Color/Application 会和 MAUI 撞名。
/// </summary>
public sealed class PcmRecorder : IDisposable
{
    // ────────────────────────── 音频参数 ──────────────────────────

    /// <summary>采样率（Hz）。</summary>
    public const int SampleRate = 16000;

    /// <summary>声道数（单声道够转写用，还省一半带宽）。</summary>
    public const int Channels = 1;

    /// <summary>位深。</summary>
    public const int BitsPerSample = 16;

    /// <summary>每块读取时长（毫秒）——也是 VAD 的判定粒度。</summary>
    private const int BlockMs = 100;

    /// <summary>单块字节数：16000Hz × 2字节 × 1声道 × 0.1秒 = 3200 字节。</summary>
    private const int BlockBytes = SampleRate * (BitsPerSample / 8) * Channels * BlockMs / 1000;

    /// <summary>太短的片段当噪声丢掉（毫秒）——避免咳嗽一声就去转写一次。</summary>
    private const int MinSpeechMs = 300;

    /// <summary>音量归一化的参考 RMS：约等于正常说话的中等音量，到它就显示满格。</summary>
    private const double VolumeRefRms = 6000.0;

    // ────────────────────────── 可调参数 ──────────────────────────

    /// <summary>静音能量阈值（16bit PCM 的 RMS）。低于它算静音，环境嘈杂时可调高。</summary>
    public int SilenceRmsThreshold { get; set; } = 500;

    /// <summary>
    /// 播报抑制期间的「打断门限」（RMS）。TTS 外放的声音会被麦克风收到，
    /// 用普通阈值判定会把她的声音当成用户在说话 → 永远自我打断。
    /// 所以抑制期要求能量**明显高于**回声水平才算真打断（默认是静音阈值的 3 倍）。
    /// </summary>
    public int InterruptRmsThreshold { get; set; } = 1500;

    /// <summary>连续多少块超过打断门限才认定是"人在说话"（滤掉一两个尖峰噪声）。</summary>
    private const int InterruptBlocks = 2;

    /// <summary>连续静音多久判定「这句话说完了」（毫秒）。</summary>
    public int SilenceMsToEnd { get; set; } = 800;

    /// <summary>单句话最长多久强制断句（毫秒），防止一直说 / 环境噪声不断导致永不结束。</summary>
    public int MaxSpeechMs { get; set; } = 15000;

    /// <summary>预卷时长（毫秒）：把开口前的这一点点也带上，避免第一声被切掉半个字。</summary>
    public int PreRollMs { get; set; } = 300;

    // ────────────────────────── 事件 ──────────────────────────

    /// <summary>检测到人声开口（上层用它打断正在播的 TTS）。</summary>
    public event Action? SpeechStarted;

    /// <summary>一句话说完了，参数是这一整句的裸 PCM（16kHz/单声道/16bit）。</summary>
    public event Action<byte[]>? SpeechEnded;

    /// <summary>实时音量 0~1（画呼吸动画用），每 100ms 一次。</summary>
    public event Action<double>? VolumeChanged;

    /// <summary>
    /// 播报抑制期间检测到「用户开口打断」。TTS 正在播时靠它立刻闭嘴。
    /// 抑制期**不累积句子**（所以不会把自己的声音送去转写），只做打断判定。
    /// </summary>
    public event Action? Interrupted;

    // ────────────────────────── 状态 ──────────────────────────

#if ANDROID
    private global::Android.Media.AudioRecord? _record;
    private Thread? _thread;

    /// <summary>读循环是否该继续跑。</summary>
    private volatile bool _running;

    /// <summary>暂停中：照读麦克风但丢弃数据（避免 AudioRecord 缓冲溢出，效果等同暂停录音）。</summary>
    private volatile bool _paused;
#endif

    /// <summary>是否正在采集。</summary>
    public bool IsRunning
    {
        get
        {
#if ANDROID
            return _running;
#else
            return false;
#endif
        }
    }

    /// <summary>是否处于暂停状态（TTS 播报期间会暂停，避免把自己的声音录进去）。</summary>
    public bool IsPaused
    {
        get
        {
#if ANDROID
            return _paused;
#else
            return false;
#endif
        }
    }

    // ────────────────────────── 生命周期 ──────────────────────────

    /// <summary>
    /// 开始采集。调用前必须已拿到 RECORD_AUDIO 权限。
    /// 返回 false 表示麦克风打不开（被占用 / 无权限 / 设备不支持）。
    /// </summary>
    public bool Start()
    {
#if ANDROID
        if (_running) return true;
        try
        {
            // 缓冲区给足 0.8 秒，避免主线程偶尔卡顿造成采集丢块
            int minBuf = global::Android.Media.AudioRecord.GetMinBufferSize(
                SampleRate,
                global::Android.Media.ChannelIn.Mono,
                global::Android.Media.Encoding.Pcm16bit);
            if (minBuf <= 0) minBuf = BlockBytes * 4;
            int bufSize = Math.Max(minBuf, BlockBytes * 8);

            var rec = new global::Android.Media.AudioRecord(
                global::Android.Media.AudioSource.Mic,
                SampleRate,
                global::Android.Media.ChannelIn.Mono,
                global::Android.Media.Encoding.Pcm16bit,
                bufSize);

            if (rec.State != global::Android.Media.State.Initialized)
            {
                try { rec.Release(); } catch { }
                try { rec.Dispose(); } catch { }
                Debug.WriteLine("[PcmRecorder] AudioRecord 初始化失败");
                return false;
            }

            rec.StartRecording();
            if (rec.RecordingState != global::Android.Media.RecordState.Recording)
            {
                try { rec.Release(); } catch { }
                try { rec.Dispose(); } catch { }
                Debug.WriteLine("[PcmRecorder] StartRecording 失败（麦克风被占用？）");
                return false;
            }

            _record = rec;
            _paused = false;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PcmRecorder" };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[PcmRecorder] Start 异常: " + ex.Message);
            ReleaseRecord();
            return false;
        }
#else
        return false;
#endif
    }

    /// <summary>停止采集并释放麦克风（必须调，否则麦克风会一直被占用）。</summary>
    public void Stop()
    {
#if ANDROID
        _running = false;
        var t = _thread;
        _thread = null;

        // 先 Stop 唤醒阻塞在 Read 里的读线程，再 Join，最后 Release —— 顺序不能换
        try { _record?.Stop(); } catch { }

        if (t != null && t.IsAlive)
        {
            try { t.Join(800); } catch { }
        }
        ReleaseRecord();
        _paused = false;
#endif
    }

    /// <summary>
    /// 进入「播报抑制」：TTS 播报期间调这个。
    ///
    /// 为什么不能简单 Stop 掉麦克风：任务要求「播报时暂停录音」+「用户开口要能打断播报」，
    /// 这两条看似矛盾——停录了就听不到人声。所以这里**继续读麦克风、但一个字都不累积**：
    ///   · 不累积 → 不会把自己的播报声录进去送去转写（等于"暂停录音"）；
    ///   · 继续读 + 更高门限 → 仍能听出「用户真的在说话」，触发 <see cref="Interrupted"/> 去掐掉 TTS。
    /// 门限抬到 <see cref="InterruptRmsThreshold"/> 就是为了滤掉扬声器的回声。
    /// </summary>
    public void Pause()
    {
#if ANDROID
        _paused = true;
#endif
    }

    /// <summary>退出「播报抑制」，恢复正常的 VAD 断句。</summary>
    public void Resume()
    {
#if ANDROID
        _paused = false;
#endif
    }

    public void Dispose() => Stop();

#if ANDROID
    /// <summary>释放底层 AudioRecord（Stop + Release + Dispose 三重保险）。</summary>
    private void ReleaseRecord()
    {
        var r = _record;
        _record = null;
        if (r == null) return;
        try { r.Stop(); } catch { }
        try { r.Release(); } catch { }
        try { r.Dispose(); } catch { }
    }

    // ────────────────────────── 读循环 + VAD 状态机 ──────────────────────────

    /// <summary>
    /// 后台读循环。状态机很朴素但够用：
    ///   监听态 → (能量 ≥ 阈值) → 说话态（带上预卷）→ (连续静音够久 / 超长) → 吐出一句 → 回监听态。
    ///
    /// 「播报抑制」期间走另一条更简单的分支：只算能量、只判打断，不累积、不吐句。
    /// </summary>
    private void Loop()
    {
        var buffer = new byte[BlockBytes];
        var preRoll = new Queue<byte[]>();      // 开口前的环形预卷
        var speech = new MemoryStream();        // 正在说的这一句

        bool inSpeech = false;
        int silentBlocks = 0;
        int speechBlocks = 0;
        int loudBlocks = 0;                     // 抑制期：连续超过打断门限的块数

        int blocksToEnd = Math.Max(1, SilenceMsToEnd / BlockMs);
        int maxBlocks = Math.Max(1, MaxSpeechMs / BlockMs);
        int preRollBlocks = Math.Max(0, PreRollMs / BlockMs);
        int minSpeechBytes = SampleRate * (BitsPerSample / 8) * Channels * MinSpeechMs / 1000;

        while (_running)
        {
            int read;
            try
            {
                var rec = _record;
                if (rec == null) break;
                read = rec.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[PcmRecorder] Read 异常: " + ex.Message);
                break;
            }

            if (!_running) break;
            if (read <= 0) { Thread.Sleep(10); continue; }

            int rms = ComputeRms(buffer, read);

            // ── 播报抑制期（TTS 正在播）──
            // 不累积句子（不会录到自己的声音），只用更高的门限判「用户是否在打断」。
            if (_paused)
            {
                if (inSpeech)
                {
                    inSpeech = false;
                    speech.SetLength(0);
                }
                silentBlocks = 0;
                speechBlocks = 0;
                preRoll.Clear();

                try { VolumeChanged?.Invoke(Math.Clamp(rms / VolumeRefRms, 0.0, 1.0)); } catch { }

                // 连续两块都明显超过打断门限 → 认定用户在说话，喊上层掐 TTS
                if (rms >= InterruptRmsThreshold)
                {
                    loudBlocks++;
                    if (loudBlocks == InterruptBlocks)
                    {
                        loudBlocks = 0;
                        try { Interrupted?.Invoke(); } catch { }
                    }
                }
                else
                {
                    loudBlocks = 0;
                }
                continue;
            }

            loudBlocks = 0;
            bool isVoice = rms >= SilenceRmsThreshold;

            // 音量 0~1：监听态也报，呼吸动画才一直有东西可动
            try { VolumeChanged?.Invoke(Math.Clamp(rms / VolumeRefRms, 0.0, 1.0)); } catch { }

            if (!inSpeech)
            {
                // 监听态：维护预卷
                if (preRollBlocks > 0)
                {
                    var copy = new byte[read];
                    Buffer.BlockCopy(buffer, 0, copy, 0, read);
                    preRoll.Enqueue(copy);
                    while (preRoll.Count > preRollBlocks) preRoll.Dequeue();
                }

                if (isVoice)
                {
                    // 开口了：先把预卷（开口前那 300ms）灌进去，避免第一声被切掉半个字
                    inSpeech = true;
                    silentBlocks = 0;
                    speechBlocks = 0;
                    speech.SetLength(0);
                    while (preRoll.Count > 0)
                    {
                        var pre = preRoll.Dequeue();
                        speech.Write(pre, 0, pre.Length);
                    }
                    try { SpeechStarted?.Invoke(); } catch { }
                }
                else
                {
                    continue;
                }
            }

            // 说话态（开口的那一块也要进缓冲）
            if (read > 0) speech.Write(buffer, 0, read);
            speechBlocks++;

            if (isVoice) silentBlocks = 0;
            else silentBlocks++;

            bool tooLong = speechBlocks >= maxBlocks;
            bool finished = silentBlocks >= blocksToEnd;

            if (tooLong || finished)
            {
                var pcm = speech.ToArray();
                speech.SetLength(0);
                inSpeech = false;
                silentBlocks = 0;
                speechBlocks = 0;

                // 太短的当噪声（咳嗽/碰麦/环境响动）丢掉，不进转写
                if (pcm.Length >= minSpeechBytes)
                {
                    try { SpeechEnded?.Invoke(pcm); } catch { }
                }
            }
        }

        // 退出前把半句也交出去，别让用户"说到一半挂断"就白说了
        if (inSpeech)
        {
            var pcm = speech.ToArray();
            if (pcm.Length >= minSpeechBytes)
            {
                try { SpeechEnded?.Invoke(pcm); } catch { }
            }
        }
        try { speech.Dispose(); } catch { }
    }

    /// <summary>算一块 16bit 小端 PCM 的 RMS 能量。</summary>
    private static int ComputeRms(byte[] data, int count)
    {
        if (count < 2) return 0;
        long sum = 0;
        int samples = count / 2;
        for (int i = 0; i + 1 < count; i += 2)
        {
            short s = (short)(data[i] | (data[i + 1] << 8));   // 小端：低字节在前
            sum += (long)s * s;
        }
        if (samples == 0) return 0;
        return (int)Math.Sqrt((double)sum / samples);
    }
#endif

    // ────────────────────────── WAV 封装（跨平台，转写上传要用）──────────────────────────

    /// <summary>
    /// 把裸 PCM 封成 WAV 文件存到磁盘，返回路径。
    /// 转写接口（/audio/transcriptions）要的是"能被识别的音频容器"，
    /// 裸 PCM 直接传会被拒；加 44 字节 RIFF 头就是标准 WAV，whisper 系列都认。
    /// </summary>
    public static async Task<string> SaveWavAsync(byte[] pcm, string path)
    {
        var header = BuildWavHeader(pcm.Length);
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await fs.WriteAsync(header, 0, header.Length);
            await fs.WriteAsync(pcm, 0, pcm.Length);
        }
        return path;
    }

    /// <summary>生成 44 字节的标准 PCM WAV 头。</summary>
    private static byte[] BuildWavHeader(int dataLength)
    {
        int byteRate = SampleRate * Channels * BitsPerSample / 8;
        int blockAlign = Channels * BitsPerSample / 8;
        var h = new byte[44];

        void Ascii(int offset, string s)
        {
            for (int i = 0; i < s.Length; i++) h[offset + i] = (byte)s[i];
        }
        void I32(int offset, int v)
        {
            h[offset] = (byte)(v & 0xFF);
            h[offset + 1] = (byte)((v >> 8) & 0xFF);
            h[offset + 2] = (byte)((v >> 16) & 0xFF);
            h[offset + 3] = (byte)((v >> 24) & 0xFF);
        }
        void I16(int offset, int v)
        {
            h[offset] = (byte)(v & 0xFF);
            h[offset + 1] = (byte)((v >> 8) & 0xFF);
        }

        Ascii(0, "RIFF");
        I32(4, 36 + dataLength);          // 文件长度 - 8
        Ascii(8, "WAVE");
        Ascii(12, "fmt ");
        I32(16, 16);                      // fmt 块长度
        I16(20, 1);                       // 1 = PCM
        I16(22, Channels);
        I32(24, SampleRate);
        I32(28, byteRate);
        I16(32, blockAlign);
        I16(34, BitsPerSample);
        Ascii(36, "data");
        I32(40, dataLength);
        return h;
    }
}

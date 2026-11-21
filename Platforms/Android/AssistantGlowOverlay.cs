#if ANDROID
using Android.Animation;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using Java.Lang;

namespace 青阳AI;

/// <summary>
/// 助手唤醒光效（悬浮层）。
///
/// 效果设计（按用户 2026-10-17 描述实现）：
///   1. 从屏幕右侧（电源键所在侧）出现一根竖直小白条，与电源键平行；
///   2. 白条定格后「迸射」—— 像铁水从浇口倒出来，从白条的上下两端同时向外冲出，
///      沿屏幕四边流动，把 0.5cm 厚的一圈边框「灌满」；
///   3. 三色（蓝 / 粉 / 紫）混合的跑马灯在四周最浓，向屏幕内侧逐渐变淡、透明。
///
/// 关键：**不是瞬间出现、也不是整块渐显**，而是用 PathMeasure 沿边框路径推进
/// 「流动前沿」，画出自「浇口 → 跑满全框」的连续流动过程。
///
/// 注意：类型命名注意：Platforms/Android 下 MAUI 的全局 using 会和 Android 类型冲突
///    （View / Paint / Path / Color），本文件一律用 global:: 全限定名。
/// </summary>
public class AssistantGlowOverlay
{
    private static AssistantGlowOverlay? _current;

    private readonly Context _ctx;
    private readonly global::Android.Views.IWindowManager? _wm;
    private readonly GlowView? _root;

    private AssistantGlowOverlay(Context ctx)
    {
        _ctx = ctx;
        _wm = ctx.GetSystemService(Context.WindowService) as global::Android.Views.IWindowManager;
        _root = new GlowView(ctx);
    }

    /// <summary>播放一次唤醒光效。没有悬浮窗权限时返回 false（调用方负责引导用户开启）。</summary>
    public static bool Play(Context ctx)
    {
        if (!AssistantReplacer.HasOverlayPermission()) return false;
        try
        {
            Dismiss(); // 同一时刻只保留一层
            var ov = new AssistantGlowOverlay(ctx);
            if (!ov.Show()) return false;
            _current = ov;
            return true;
        }
        catch { return false; }
    }

    /// <summary>关掉当前光效（如果正在放）。</summary>
    public static void Dismiss()
    {
        try { _current?.Hide(); } catch { }
        _current = null;
    }

    private bool Show()
    {
        if (_wm == null || _root == null) return false;
        try
        {
            var flags = WindowManagerFlags.NotFocusable
                      | WindowManagerFlags.NotTouchable
                      | WindowManagerFlags.LayoutInScreen
                      | WindowManagerFlags.LayoutNoLimits;

            var lp = new WindowManagerLayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent,
                WindowManagerTypes.ApplicationOverlay,
                flags,
                global::Android.Graphics.Format.Transparent);

            lp.Gravity = GravityFlags.Top | GravityFlags.Start;
            lp.X = 0;
            lp.Y = 0;

            // 尽量铺满，包括刘海 / 导航栏区域
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.P)
                lp.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;

            _wm.AddView(_root, lp);
            return true;
        }
        catch
        {
            try { _wm.RemoveView(_root); } catch { }
            return false;
        }
    }

    private void Hide()
    {
        try
        {
            _root?.Stop();
            if (_wm != null && _root != null) _wm.RemoveView(_root);
        }
        catch { }
    }

    /// <summary>实际绘制浇口、流动前沿与跑马灯的 View。</summary>
    private class GlowView : global::Android.Views.View
    {
        // ── 时间轴（毫秒）──
        private const long BarGrowMs = 260;    // 小白条从电源键位置长出
        private const long BurstMs = 380;      // 从白条上下两端迸射到屏幕顶/底
        private const long FlowMs = 700;       // 沿四边跑满整圈
        private const long GlowMs = 320;       // 跑满后整圈亮起 + 向内渐隐加深
        private const long HoldMs = 520;       // 定格停留
        private const long FadeMs = 420;       // 整体淡出

        private const float BandDp = 19f;      // 边框厚度 ≈ 0.5cm（19dp 左右，按屏幕密度换算）

        private long _start;
        private bool _running;
        private readonly ValueAnimator? _anim;

        private float _w, _h, _band;
        private float _frameLen;
        private float[]? _framePos;            // 缓存边框采样点，避免每帧重算

        private int _cBlue, _cPink, _cPurple;

        public GlowView(Context ctx) : base(ctx)
        {
            SetWillNotDraw(false);
            SetLayerType(LayerType.Hardware, null);
            SetBackgroundColor(global::Android.Graphics.Color.Transparent);
            Focusable = false;
            Clickable = false;

            _cBlue = global::Android.Graphics.Color.Rgb(0x5B, 0x8D, 0xFF);
            _cPink = global::Android.Graphics.Color.Rgb(0xFF, 0x7A, 0xB6);
            _cPurple = global::Android.Graphics.Color.Rgb(0xA9, 0x6B, 0xFF);

            _anim = ValueAnimator.OfFloat(0f, 1f);
            _anim.SetDuration(BarGrowMs + BurstMs + FlowMs + GlowMs + HoldMs + FadeMs);
            _anim.SetInterpolator(new global::Android.Views.Animations.LinearInterpolator());
            _anim.Update += (_, _) => Invalidate();
            _anim.AnimationEnd += (_, _) => { _running = false; Finish(); };

            _start = global::Android.OS.SystemClock.UptimeMillis();
            _running = true;
            _anim.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _anim?.Cancel(); } catch { }
        }

        private void Finish()
        {
            try
            {
                var ctx = Context;
                if (ctx != null && AssistantReplacer.HasOverlayPermission())
                {
                    var svc = ctx.GetSystemService(Context.WindowService) as global::Android.Views.IWindowManager;
                    svc?.RemoveView(this);
                }
            }
            catch { }
        }

        protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
        {
            base.OnSizeChanged(w, h, oldw, oldh);
            _w = w;
            _h = h;
            _band = BandDp * Resources!.DisplayMetrics!.Density;
            BuildFrameSamples();
        }

        /// <summary>采样整圈边框中心线，供流动前沿按距离索引取点。</summary>
        private void BuildFrameSamples()
        {
            if (_w <= 0 || _h <= 0) return;
            float inset = _band / 2f;
            var p = new global::Android.Graphics.Path();
            p.AddRoundRect(new global::Android.Graphics.RectF(inset, inset, _w - inset, _h - inset),
                _band * 0.6f, _band * 0.6f, global::Android.Graphics.Path.Direction.Cw);

            using var pm = new global::Android.Graphics.PathMeasure(p, false);
            _frameLen = pm.Length;
            if (_frameLen <= 0) { _framePos = null; return; }

            int n = System.Math.Max(64, (int)(_frameLen / (2 * Resources!.DisplayMetrics!.Density)));
            _framePos = new float[n * 2];
            var pos = new float[2];
            for (int i = 0; i < n; i++)
            {
                float d = _frameLen * i / n;
                pm.GetPosTan(d, pos, null);
                _framePos[i * 2] = pos[0];
                _framePos[i * 2 + 1] = pos[1];
            }
        }

        protected override void OnDraw(global::Android.Graphics.Canvas canvas)
        {
            base.OnDraw(canvas);
            if (!_running || _w <= 0 || _h <= 0) return;

            long t = global::Android.OS.SystemClock.UptimeMillis() - _start;
            float cx = _w;              // 电源键在右侧 → 浇口取右边缘
            float cy = _h * 0.5f;

            // ── 阶段 1：小白条从右侧长出（与电源键平行）──
            if (t < BarGrowMs)
            {
                DrawSeedBar(canvas, cx, cy, EaseOut(t / (float)BarGrowMs));
                return;
            }

            long t2 = t - BarGrowMs;

            // ── 阶段 2/3：迸射 + 沿边流动 ──
            if (t2 < BurstMs + FlowMs)
            {
                float p = t2 / (float)(BurstMs + FlowMs);
                DrawFlowing(canvas, p, cx, cy);
                return;
            }

            long t3 = t2 - BurstMs - FlowMs;

            // ── 阶段 4：跑满后整圈亮起 + 向内渐隐加深 ──
            if (t3 < GlowMs)
            {
                DrawGlowFrame(canvas, EaseOut(t3 / (float)GlowMs));
                return;
            }

            long t4 = t3 - GlowMs;

            // ── 阶段 5：定格 ──
            if (t4 < HoldMs)
            {
                DrawGlowFrame(canvas, 1f);
                return;
            }

            // ── 阶段 6：整体淡出 ──
            long t5 = t4 - HoldMs;
            float fade = 1f - System.Math.Clamp(t5 / (float)FadeMs, 0f, 1f);
            DrawGlowFrame(canvas, fade);
        }

        /// <summary>阶段 1：右侧竖小白条，从中心向上下两端长出。</summary>
        private void DrawSeedBar(global::Android.Graphics.Canvas canvas, float cx, float cy, float e)
        {
            float barH = _h * 0.18f * e;
            float barW = System.Math.Max(3f, _band * 0.22f);

            var paint = new global::Android.Graphics.Paint(global::Android.Graphics.PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.White
            };

            // 柔光
            paint.SetMaskFilter(new global::Android.Graphics.BlurMaskFilter(_band,
                global::Android.Graphics.BlurMaskFilter.Blur.Normal));
            paint.Alpha = 120;
            canvas.DrawRoundRect(new global::Android.Graphics.RectF(
                cx - barW * 1.6f, cy - barH * 0.55f, cx + barW * 0.6f, cy + barH * 0.55f),
                barW, barW, paint);

            // 实心白条
            paint.SetMaskFilter(null);
            paint.Alpha = 255;
            canvas.DrawRoundRect(new global::Android.Graphics.RectF(
                cx - barW, cy - barH * 0.5f, cx, cy + barH * 0.5f),
                barW * 0.5f, barW * 0.5f, paint);
        }

        /// <summary>阶段 2/3：「铁水」从浇口向上下两端迸射，再沿四边流动灌满。</summary>
        private void DrawFlowing(global::Android.Graphics.Canvas canvas, float p, float cx, float cy)
        {
            if (_framePos == null || _frameLen <= 0) return;

            // 迸射在前 35% 完成，之后沿边框推进
            float burst = System.Math.Clamp(p / 0.35f, 0f, 1f);
            float flow = System.Math.Clamp((p - 0.25f) / 0.75f, 0f, 1f);
            float reach = 0.06f + EaseInOutCubic(flow) * 0.94f;   // 已灌满的比例

            var paint = new global::Android.Graphics.Paint(global::Android.Graphics.PaintFlags.AntiAlias)
            {
                StrokeWidth = _band
            };
            paint.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
            paint.SetShader(BuildTriColorShader());

            DrawFlowFront(canvas, paint, reach, burst);
        }

        /// <summary>沿边框采样序列画出「已到达」的一段 + 两端流动头部高亮。</summary>
        private void DrawFlowFront(global::Android.Graphics.Canvas canvas,
            global::Android.Graphics.Paint paint, float reach, float burst)
        {
            if (_framePos == null || _frameLen <= 0) return;
            int n = _framePos.Length / 2;

            int seedIdx = NearestIndex(_w, _h * 0.5f, n);
            int spread = (int)(n * 0.5f * reach);
            int headIdx = seedIdx + spread;
            int tailIdx = seedIdx - spread;

            var path = new global::Android.Graphics.Path();
            bool started = false;

            for (int k = -n; k <= n; k++)
            {
                int idx = seedIdx + k;
                if (idx < tailIdx || idx > headIdx) continue;
                int wi = ((idx % n) + n) % n;
                float x = _framePos[wi * 2], y = _framePos[wi * 2 + 1];
                if (!started) { path.MoveTo(x, y); started = true; }
                else path.LineTo(x, y);
            }
            if (started) canvas.DrawPath(path, paint);

            if (reach < 0.995f)
            {
                DrawHead(canvas, headIdx, burst);
                DrawHead(canvas, tailIdx, burst);
            }
        }

        private void DrawHead(global::Android.Graphics.Canvas canvas, int idx, float burst)
        {
            if (_framePos == null) return;
            int n = _framePos.Length / 2;
            int wi = ((idx % n) + n) % n;
            float x = _framePos[wi * 2], y = _framePos[wi * 2 + 1];

            var p = new global::Android.Graphics.Paint(global::Android.Graphics.PaintFlags.AntiAlias)
            {
                Color = global::Android.Graphics.Color.White
            };
            p.SetMaskFilter(new global::Android.Graphics.BlurMaskFilter(_band * 0.9f,
                global::Android.Graphics.BlurMaskFilter.Blur.Normal));
            p.Alpha = (int)(210 * System.Math.Clamp(burst * 1.4f, 0.2f, 1f));
            canvas.DrawCircle(x, y, _band * 0.75f, p);
        }

        /// <summary>阶段 4/5/6：整圈三色跑马灯，四周最浓、向内渐隐。</summary>
        private void DrawGlowFrame(global::Android.Graphics.Canvas canvas, float a)
        {
            a = System.Math.Clamp(a, 0f, 1f);
            if (a <= 0.001f) return;

            // 主边框带（跑马灯）
            var paint = new global::Android.Graphics.Paint(global::Android.Graphics.PaintFlags.AntiAlias)
            {
                StrokeWidth = _band
            };
            paint.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
            paint.Alpha = (int)(255 * a);
            paint.SetShader(BuildTriColorShader());
            canvas.DrawRoundRect(new global::Android.Graphics.RectF(
                _band / 2, _band / 2, _w - _band / 2, _h - _band / 2),
                _band * 0.6f, _band * 0.6f, paint);

            // 向内渐隐：多圈递减厚度 + alpha，营造"从边缘往里变浅"
            paint.SetShader(null);
            for (int i = 1; i <= 4; i++)
            {
                float w = _band * (1f - i * 0.22f);
                if (w <= 0.5f) break;
                int al = (int)(70 * a / i);
                if (al <= 0) break;
                paint.Alpha = al;
                paint.StrokeWidth = w;
                paint.SetColor(BlendTriColorAt(i / 5f));
                float inset = _band * 0.5f + i * _band * 0.28f;
                canvas.DrawRoundRect(new global::Android.Graphics.RectF(
                    inset, inset, _w - inset, _h - inset),
                    w * 0.5f, w * 0.5f, paint);
            }
        }

        /// <summary>三色循环渐变，靠相位位移产生「跑马灯」流动感。</summary>
        private global::Android.Graphics.Shader BuildTriColorShader()
        {
            long t = global::Android.OS.SystemClock.UptimeMillis() - _start;
            float phase = (t % 1800) / 1800f;

            var colors = new int[] { _cBlue, _cPurple, _cPink, _cBlue };
            var pos = new float[] { 0f, 0.33f, 0.66f, 1f };

            float span = System.Math.Max(_w, _h) * 1.2f;
            float off = -span * 0.35f + span * phase;
            return new global::Android.Graphics.LinearGradient(
                -span * 0.35f + off, 0, span * 0.65f + off, _h,
                colors, pos, global::Android.Graphics.Shader.TileMode.Mirror);
        }

        private int BlendTriColorAt(float k)
        {
            if (k < 0.5f) return Lerp(_cBlue, _cPurple, k * 2);
            return Lerp(_cPurple, _cPink, (k - 0.5f) * 2);
        }

        private static int Lerp(int c1, int c2, float t)
        {
            t = System.Math.Clamp(t, 0f, 1f);
            int r = (int)(global::Android.Graphics.Color.GetRedComponent(c1) * 255 * (1 - t)
                        + global::Android.Graphics.Color.GetRedComponent(c2) * 255 * t);
            int g = (int)(global::Android.Graphics.Color.GetGreenComponent(c1) * 255 * (1 - t)
                        + global::Android.Graphics.Color.GetGreenComponent(c2) * 255 * t);
            int b = (int)(global::Android.Graphics.Color.GetBlueComponent(c1) * 255 * (1 - t)
                        + global::Android.Graphics.Color.GetBlueComponent(c2) * 255 * t);
            return global::Android.Graphics.Color.Rgb(r, g, b);
        }

        private int NearestIndex(float x, float y, int n)
        {
            if (_framePos == null) return 0;
            int best = 0;
            float bd = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float dx = _framePos[i * 2] - x, dy = _framePos[i * 2 + 1] - y;
                float d = dx * dx + dy * dy;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        private static float EaseOut(float t) => 1f - (1f - t) * (1f - t);

        private static float EaseInOutCubic(float t)
            => t < 0.5f ? 4f * t * t * t : 1f - (float)System.Math.Pow(-2f * t + 2f, 3f) / 2f;
    }
}
#endif

using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;

namespace 青阳AI;

// ============================================================================
// 自定义控件（2026-10-29 整合）
//
// 合并自原来的四个文件：
//   ShimmerLabel.cs     —— 流光文字
//   MorphIcon.cs        —— 矢量图标（路径描边）
//   StatusBadge.cs      —— 在线状态徽章（呼吸光晕，代际令牌防竞态）
//   DotMotionLoader.cs  —— 点阵加载动画
// ============================================================================

// ────────────── 原 ShimmerLabel.cs ──────────────
/// <summary>
/// 文字流光 Label：高光光带沿 45° 对角斜向扫描文字，只作用于文字本体（TextView Paint 渐变着色器）。
/// 动效：左上→右下→左上 来回滚动，位移用二次方 EaseInOut 曲线（慢→快→慢）。
/// 通过 IsActive 控制启停（如思考中/终端运行中才流光，完成即停）。
/// </summary>
public class ShimmerLabel : Label
{
    private IDispatcherTimer? _timer;
    private DateTime _start;

    /// <summary>是否开启流光（true 启动，false 停止并清除）。</summary>
    public static readonly BindableProperty IsActiveProperty =
        BindableProperty.Create(nameof(IsActive), typeof(bool), typeof(ShimmerLabel), false,
            propertyChanged: (b, o, n) =>
            {
                var label = (ShimmerLabel)b;
                if (n is true) label.StartShimmer();
                else label.StopShimmer();
            });

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>高光色（可配置）。未设置时自动向白色提亮。</summary>
    public static readonly BindableProperty HighlightColorProperty =
        BindableProperty.Create(nameof(HighlightColor), typeof(Color), typeof(ShimmerLabel), null);

    public Color? HighlightColor
    {
        get => (Color?)GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (IsActive && Handler?.PlatformView != null)
            StartShimmer();
    }

    /// <summary>启动流光动画（无碍重复调用）。</summary>
    public void StartShimmer()
    {
        if (_timer != null) return;
#if ANDROID
        _start = DateTime.Now;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += OnTick;
        _timer.Start();
#endif
    }

    /// <summary>停止流光动画并清除着色器。</summary>
    public void StopShimmer()
    {
        if (_timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
#if ANDROID
        if (Handler?.PlatformView is Android.Widget.TextView tv)
        {
            tv.Paint.SetShader(null);
            tv.Invalidate();
        }
#endif
    }

    /// <summary>二次方缓出（先快后慢）：速度比例 = 1-(1-t)^2，无阻尼系数。</summary>
    private static double QuadEaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return 1 - (1 - t) * (1 - t);
    }

    private void OnTick(object? sender, EventArgs e)
    {
#if ANDROID
        if (Handler?.PlatformView is not Android.Widget.TextView tv) return;
        var c = TextColor;
        var text = tv.Text ?? Text ?? "";
        float textW = Math.Max(1f, tv.Paint.MeasureText(text));
        var fm = new Android.Graphics.Paint.FontMetrics();
        tv.Paint.GetFontMetrics(fm);
        float textH = Math.Max(1f, fm.Bottom - fm.Top);
        const float bandW = 90f;           // 高光光带宽
        const double cycleMs = 2400;       // 单程时长 2.4s（来回 4.8s）

        // 0..2 来回相位：0→1 正向（左上→右下），1→2 反向（右下→左上）
        double phase = (DateTime.Now - _start).TotalMilliseconds / cycleMs % 2;
        double p = phase <= 1 ? phase : 2 - phase;
        // 每段单独用二次方缓出：先快后慢（正向、反向都一样）
        float pos = (float)(QuadEaseOut(p) * (textW + textH)); // 对角扫描位置

        // 高光色：配置优先，否则向白色提亮
        Color hl = HighlightColor ?? Color.FromRgba(
            Math.Min(1.0, c.Red + 0.5),
            Math.Min(1.0, c.Green + 0.5),
            Math.Min(1.0, c.Blue + 0.5), 1);

        int baseArgb = c.ToInt();
        int hlArgb = hl.ToInt();

        // 45° 对角渐变：光带沿左上→右下方向
        using var shader = new Android.Graphics.LinearGradient(
            pos - bandW, pos - bandW, pos, pos,
            new[] { baseArgb, hlArgb, baseArgb },
            new[] { 0f, 0.5f, 1f },
            Android.Graphics.Shader.TileMode.Clamp!);

        tv.Paint.SetShader(shader);
        tv.Invalidate();
#endif
    }
}

// ────────────── 原 MorphIcon.cs ──────────────
    public class MorphIcon : ContentView
    {
        static readonly Dictionary<string, string> Paths = new()
        {
            ["ChevronRight"] = "M9 18 L15 12 L9 6",
            ["ChevronLeft"]  = "M15 18 L9 12 L15 6",
            ["ChevronDown"]  = "M6 9 L12 15 L18 9",
            ["ChevronUp"]    = "M18 15 L12 9 L6 15",
            ["X"]            = "M18 6 L6 18 M6 6 L18 18",
            ["Menu"]         = "M4 6 H20 M4 12 H20 M4 18 H20",
            ["Mic"]          = "M12 2 A3 3 0 0 0 9 5 V11 A3 3 0 0 0 15 11 V5 A3 3 0 0 0 12 2 Z M19 10 V12 A7 7 0 0 1 5 12 V10 M12 19 V22",
            ["Image"]        = "M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M9 7 A2 2 0 1 0 9 11 A2 2 0 0 0 9 7 Z M21 15 L17.914 11.914 A2 2 0 0 0 15.086 11.914 L6 21",
            ["Send"]         = "M22 2 L11 13 M22 2 L15 22 L11 13 L2 9 Z",
            ["Stop"]         = "M9 7 H15 A2 2 0 0 1 17 9 V15 A2 2 0 0 1 15 17 H9 A2 2 0 0 1 7 15 V9 A2 2 0 0 1 9 7 Z",
            ["Trash"]        = "M3 6 H21 M8 6 V4 A2 2 0 0 1 10 2 H14 A2 2 0 0 1 16 4 V6 M19 6 L18 20 A2 2 0 0 1 16 22 H8 A2 2 0 0 1 6 20 L5 6 M10 11 V17 M14 11 V17",
            ["Speaker"]      = "M11 5 L6 9 L2 9 L2 15 L6 15 L11 19 Z M15.5 8.5 A5 5 0 0 1 15.5 15.5 M18.8 5.6 A9 9 0 0 1 18.8 18.4",
            ["Book"]         = "M5 3 L17 3 A2 2 0 0 1 19 5 L19 19 A2 2 0 0 1 17 21 L5 21 Z M9 3 L9 21 M12.5 8 L16 8 M12.5 12 L16 12",
            ["Settings"]     = "M12.22 2 H11.78 A2 2 0 0 0 9.78 4 V4.18 A2 2 0 0 1 8.78 5.91 L8.35 6.16 A2 2 0 0 1 6.35 6.16 L6.2 6.08 A2 2 0 0 0 3.47 6.81 L3.25 7.19 A2 2 0 0 0 3.98 9.92 L4.13 10.02 A2 2 0 0 1 5.13 11.74 V12.25 A2 2 0 0 1 4.13 13.97 L3.98 14.06 A2 2 0 0 0 3.25 16.79 L3.47 17.17 A2 2 0 0 0 6.2 17.9 L6.35 17.82 A2 2 0 0 1 8.35 17.82 L8.78 18.07 A2 2 0 0 1 9.78 19.8 V20 A2 2 0 0 0 11.78 22 H12.22 A2 2 0 0 0 14.22 20 V19.82 A2 2 0 0 1 15.22 18.09 L15.65 17.84 A2 2 0 0 1 17.65 17.84 L17.8 17.92 A2 2 0 0 0 20.53 17.19 L20.75 16.81 A2 2 0 0 0 20.02 14.08 L19.87 13.98 A2 2 0 0 1 18.87 12.26 V11.75 A2 2 0 0 1 19.87 10.03 L20.02 9.94 A2 2 0 0 0 20.75 7.21 L20.53 6.83 A2 2 0 0 0 17.8 6.1 L17.65 6.18 A2 2 0 0 1 15.65 6.18 L15.22 5.93 A2 2 0 0 1 14.22 4.2 V4 A2 2 0 0 0 12.22 2 Z M12 9 A3 3 0 1 0 12 15 A3 3 0 0 0 12 9 Z",
            ["Globe"]        = "M12 3 A9 9 0 1 0 12 21 A9 9 0 0 0 12 3 Z M3 12 H21 M12 3 A15 15 0 0 1 12 21 M12 3 A15 15 0 0 0 12 21",
            ["Wifi"]         = "M5 12 A11 11 0 0 1 19 12 M8.5 15.5 A6.5 6.5 0 0 1 15.5 15.5 M12 19 H12.01",
            ["ArrowLeft"]    = "M19 12 H5 M12 19 L5 12 L12 5",
            ["ArrowRight"]   = "M5 12 H19 M12 5 L19 12 L12 19",
            ["RotateCw"]     = "M21 12 A9 9 0 1 1 12 3 M21 3 V9 H15",
            ["MessageCircle"]= "M21 11.5 A8.5 8.5 0 0 1 5.7 17.5 L3 20 L3 13 A8.5 8.5 0 0 1 21 11.5 Z",
            ["MoreVertical"] = "M12 5 A1.5 1.5 0 1 0 12 8 A1.5 1.5 0 0 0 12 5 Z M12 11 A1.5 1.5 0 1 0 12 14 A1.5 1.5 0 0 0 12 11 Z M12 17 A1.5 1.5 0 1 0 12 20 A1.5 1.5 0 0 0 12 17 Z",
            ["Home"]         = "M3 11 L12 3 L21 11 M5 10 V20 A1 1 0 0 0 6 21 H10 V14 H14 V21 H18 A1 1 0 0 0 19 20 V10",
            ["Search"]       = "M10 4 A6 6 0 1 0 10 16 A6 6 0 0 0 10 4 Z M15 15 L20 20",
            ["Shield"]       = "M12 2 L20 6 V12 C20 17 16.5 20.5 12 22 C7.5 20.5 4 17 4 12 V6 Z",
            ["ShieldCheck"]  = "M12 2 L20 6 V12 C20 17 16.5 20.5 12 22 C7.5 20.5 4 17 4 12 V6 Z M8.5 12 L11 14.5 L15.5 10",
            ["File"]         = "M14 2 H6 A2 2 0 0 0 4 4 V20 A2 2 0 0 0 6 22 H18 A2 2 0 0 0 20 20 V8 Z M14 2 V8 H20",
            ["ShieldAlert"]  = "M12 2 L20 6 V12 C20 17 16.5 20.5 12 22 C7.5 20.5 4 17 4 12 V6 Z M12 8 V13 M12 16 H12.01",
            // 2026-10-28 新增：替换工作区面板/收藏页里原先的彩色 emoji
            ["Pencil"]       = "M12 20 H21 M16.5 3.5 A2.121 2.121 0 0 1 19.5 6.5 L7 19 L3 20 L4 16 Z",
            ["Folder"]       = "M3 7 A2 2 0 0 1 5 5 H9 L11 7 H19 A2 2 0 0 1 21 9 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 Z",
            ["Download"]     = "M12 3 V15 M7 10 L12 15 L17 10 M4 20 H20",
            ["Unlock"]       = "M6 11 H18 A2 2 0 0 1 20 13 V20 A2 2 0 0 1 18 22 H6 A2 2 0 0 1 4 20 V13 A2 2 0 0 1 6 11 Z M8 11 V7 A4 4 0 0 1 15.5 5",
            ["Heart"]        = "M12 21 C12 21 3 15 3 9 A5 5 0 0 1 12 6 A5 5 0 0 1 21 9 C21 15 12 21 12 21 Z",
            ["HardDrive"]    = "M3 12 H21 M5 12 L7 5 H17 L19 12 V19 A1 1 0 0 1 18 20 H6 A1 1 0 0 1 5 19 Z M8 16 H8.01 M12 16 H12.01",
            ["Pen"]          = "M18 2 L22 6 L8 20 L3 21 L4 16 Z",
        };

        readonly Microsoft.Maui.Controls.Shapes.Path _path;
        string _currentIcon = "";
        bool _animating;

        public static readonly BindableProperty IconProperty = BindableProperty.Create(
            nameof(Icon), typeof(string), typeof(MorphIcon), "ChevronRight",
            propertyChanged: async (b, o, n) =>
            {
                if (b is MorphIcon m && n is string s && s != m._currentIcon)
                    await m.AnimateTo(s);
            });

        public string Icon
        {
            get => (string)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        public static readonly BindableProperty IconColorProperty = BindableProperty.Create(
            nameof(IconColor), typeof(Color), typeof(MorphIcon), Colors.White,
            propertyChanged: (b, o, n) =>
            {
                if (b is MorphIcon m && n is Color c) m._path.Stroke = c;
            });

        public Color IconColor
        {
            get => (Color)GetValue(IconColorProperty);
            set => SetValue(IconColorProperty, value);
        }

        public static readonly BindableProperty IconSizeProperty = BindableProperty.Create(
            nameof(IconSize), typeof(double), typeof(MorphIcon), 20.0,
            propertyChanged: (b, o, n) =>
            {
                if (b is MorphIcon m && n is double d)
                {
                    m.WidthRequest = d;
                    m.HeightRequest = d;
                    m._path.WidthRequest = d;
                    m._path.HeightRequest = d;
                }
            });

        public double IconSize
        {
            get => (double)GetValue(IconSizeProperty);
            set => SetValue(IconSizeProperty, value);
        }

        public static readonly BindableProperty StrokeWidthProperty = BindableProperty.Create(
            nameof(StrokeWidth), typeof(double), typeof(MorphIcon), 2.0,
            propertyChanged: (b, o, n) =>
            {
                if (b is MorphIcon m && n is double d) m._path.StrokeThickness = d;
            });

        public double StrokeWidth
        {
            get => (double)GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public MorphIcon()
        {
            _path = new Microsoft.Maui.Controls.Shapes.Path
            {
                Stroke = IconColor,
                StrokeThickness = StrokeWidth,
                StrokeLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Aspect = Stretch.Uniform,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                WidthRequest = IconSize,
                HeightRequest = IconSize,
            };
            WidthRequest = IconSize;
            HeightRequest = IconSize;
            Content = _path;
            SetPathData(Icon);
        }

        void SetPathData(string icon)
        {
            if (Paths.TryGetValue(icon, out var data))
            {
                _path.Data = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(data)!;
                _currentIcon = icon;
            }
        }

        async Task AnimateTo(string newIcon)
        {
            if (_animating)
            {
                _path.CancelAnimations();
                _path.Opacity = 1;
                _path.Rotation = 0;
            }
            _animating = true;

            try
            {
                await Task.WhenAll(
                    _path.FadeToAsync(0, 120, Easing.CubicIn),
                    _path.RotateToAsync(90, 120, Easing.CubicIn));

                _path.Rotation = -90;
                SetPathData(newIcon);

                await Task.WhenAll(
                    _path.FadeToAsync(1, 240, Easing.CubicOut),
                    _path.RotateToAsync(0, 240, Easing.CubicOut));
            }
            catch
            {
                _path.Opacity = 1;
                _path.Rotation = 0;
                SetPathData(newIcon);
            }
            finally
            {
                _animating = false;
            }
        }

    /// <summary>应用启动时预热：提前解析全部图标路径，避免首次打开设置页时才解析卡顿。</summary>
    public static void Prewarm()
    {
        try
        {
            foreach (var kv in Paths)
            {
                _ = (Geometry)new PathGeometryConverter().ConvertFromInvariantString(kv.Value)!;
            }
        }
        catch { /* 预热失败不影响运行 */ }
    }
}

// ────────────── 原 StatusBadge.cs ──────────────
/// <summary>
/// 呼吸发光状态点：直径 15px 实心圆点 + 在线时周围每 2 秒扩散一圈
/// 半透明圆环（15px→40px，透明度 100%→0%），无限循环，类似 animate-ping。
/// </summary>
public class PingDot : ContentView
{
    private readonly Border _core;
    private readonly Border _ring;
    // 动画代际令牌：每次 SetMode 自增，旧的动画循环发现代际变了就自行退出，
    // 保证同一时刻最多只有一个循环在跑（否则连续切换状态会无限叠加循环把 UI 线程拖死）
    private int _pingEpoch;

    public PingDot()
    {
        _ring = new Border
        {
            WidthRequest = 15,
            HeightRequest = 15,
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(7.5) },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        _core = new Border
        {
            WidthRequest = 15,
            HeightRequest = 15,
            StrokeThickness = 0,
            BackgroundColor = Color.FromArgb("#AAAAAA"),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(7.5) },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        // 容器比点略大，给扩散环留空间
        Content = new Grid
        {
            WidthRequest = 42,
            HeightRequest = 42,
            Children = { _ring, _core }
        };
    }

    /// <summary>设置状态：online=true 绿点+光晕，false 红点无光晕。</summary>
    public void SetMode(bool? online)
    {
        if (online == true)
        {
            StartPing();
        }
        else
        {
            StopPing();
            _core.BackgroundColor = online == false ? Colors.Red : Color.FromArgb("#AAAAAA");
        }
    }

    private void StopPing()
    {
        _pingEpoch++;
        _ring.CancelAnimations(); // 立刻取消在跑的扩散动画，旧循环下次检查代际即退出
        _ring.Scale = 1;
        _ring.Opacity = 1;
        _ring.BackgroundColor = Colors.Transparent;
    }

    private void StartPing()
    {
        var epoch = ++_pingEpoch;
        _core.BackgroundColor = Colors.LimeGreen;
        _ring.BackgroundColor = Color.FromArgb("#66FF66");
        _ = PingLoopAsync(epoch);
    }

    private async Task PingLoopAsync(int epoch)
    {
        const double scaleTarget = 40.0 / 15.0; // 15px → 40px
        while (epoch == _pingEpoch)
        {
            // 复位
            _ring.Scale = 1;
            _ring.Opacity = 1;
            _ring.BackgroundColor = Color.FromArgb("#66FF66");
            // 2 秒扩散 + 淡出
            await Task.WhenAll(
                _ring.ScaleToAsync(scaleTarget, 2000, Easing.Linear),
                _ring.FadeToAsync(0, 2000, Easing.Linear));
        }
    }
}

/// <summary>状态点 + 文字的组合徽章，供设置页模型状态显示。</summary>
public class StatusBadge : ContentView
{
    private readonly PingDot _dot;
    private readonly Label _label;

    public StatusBadge()
    {
        _dot = new PingDot();
        _label = new Label
        {
            FontSize = 12,
            TextColor = Color.FromArgb("#AAAAAA"),
            VerticalOptions = LayoutOptions.Center,
        };
        // 用 Grid 固定点位置：点始终在最左固定列，文字在右侧，不随文字长度左右移动
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            }
        };
        grid.Add(_dot, 0, 0);
        grid.Add(_label, 1, 0);
        Content = grid;
    }

    /// <summary>设置状态：true=在线(绿)，false=离线(红)，null=检测中。</summary>
    public void SetStatus(bool? online)
    {
        _dot.SetMode(online);
        if (online == true)
        {
            _label.Text = "在线";
            _label.TextColor = Colors.LimeGreen;
        }
        else if (online == false)
        {
            _label.Text = "离线";
            _label.TextColor = Colors.Red;
        }
        else
        {
            _label.Text = "检测中";
            _label.TextColor = Color.FromArgb("#AAAAAA");
        }
    }

    /// <summary>应用启动时预热：提前构建控件树，避免首次打开设置页时才初始化卡顿。</summary>
    public static void Prewarm()
    {
        try { _ = new StatusBadge(); } catch { }
    }
}

// ────────────── 原 DotMotionLoader.cs ──────────────
/// <summary>
/// 点阵加载动画：4×4 圆角方块，波浪式脉动（原"转圈"加载条的替代品）。
///
/// 来源：用户给的一段 Web Component（canvas 版 `DotMotionLoader`）。那份实现里
/// 每个格子带着 **120 个采样点 × 3 个通道 × 16 格 ≈ 5800 个数**的原始数据，
/// 照抄进 C# 不现实（又长又容易抄错），所以这里按它的**视觉特征**参数化重建：
///
///   · 4×4 网格、圆角方块、深色底块 + 亮色前块（带辉光）
///   · 周期 1.111 秒、无限循环
///   · 前块大小走 **0→1 的梯形波**（先升、停、再降、停）—— 原数据里正是这种"线性斜坡 + 平台"
///   · 底块透明度 0.58~0.90 同步脉动；前块透明度 0.84~1.00
///   · 相位按 (行+列) 沿对角线传播
///   · 几何与配色照抄原数据（392 画布 / 格子 73 / 步长 93 / 边距 20 / 圆角 16.06；
///     主色 rgb(173,228,255)、底色 rgb(45,55,67)）
///
/// 绘制交给内嵌的 <see cref="Painter"/> —— `GraphicsView` 自己就实现了 `IDrawable`，
/// 直接 `Drawable = this` 会撞上方法隐藏（CS0108），所以不这么干。
/// </summary>
public class DotMotionLoader : GraphicsView
{
    private readonly Painter _painter = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private IDispatcherTimer? _timer;

    public DotMotionLoader()
    {
        Drawable = _painter;
        BackgroundColor = Colors.Transparent;
    }

    // ───────────── 生命周期：只在**可见**时跑计时器 ─────────────
    // 气泡列表会复用模板，隐藏的实例要是也在跑 30fps 计时器，纯属白耗电。

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        SyncTimer();
    }

    protected override void OnPropertyChanged(string? propertyName = null)
    {
        base.OnPropertyChanged(propertyName);
        if (propertyName == IsVisibleProperty.PropertyName) SyncTimer();
    }

    private void SyncTimer()
    {
        try
        {
            if (IsVisible && Handler != null)
            {
                _timer ??= CreateTimer();
                if (!_timer.IsRunning) { _clock.Restart(); _timer.Start(); }
            }
            else
            {
                _timer?.Stop();
            }
        }
        catch { }
    }

    private IDispatcherTimer CreateTimer()
    {
        var t = Dispatcher.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(33);   // ~30fps：加载动画够顺，也不费电
        t.Tick += (_, _) =>
        {
            try
            {
                _painter.Phase = (_clock.Elapsed.TotalSeconds % Painter.DurationSec) / Painter.DurationSec;
                Invalidate();
            }
            catch { }
        };
        return t;
    }

    // ───────────── 实际绘制 ─────────────

    private sealed class Painter : IDrawable
    {
        // ── 几何（原数据的 392 画布坐标系，绘制时整体缩放到控件大小）──
        private const float CanvasSize = 392f;
        private const float CellSize = 73f;
        private const float CellStep = 93f;
        private const float CellMargin = 20f;
        private const float CornerRadius = 16.06f;
        private const int Grid = 4;

        /// <summary>相邻格子的相位差（对角线方向传播）。</summary>
        private const double PhaseStep = 0.16;

        // ── 配色 ──
        // **跟随应用主题色**：在设置里换主题，这里的点阵立刻跟着变（每帧读一次）。
        // 底块用主题色压暗（做成"主色的影子"），整体色调才和 App 一致。
        // 原素材是固定的 rgb(173,228,255)，但那样跟主题色对不上 —— 按用户要求改成跟随。
        private const float BackLuminosity = 0.18f;   // 底块亮度（0=黑 1=白）

        /// <summary>一个完整周期的秒数（原数据 duration = 1.1111）。</summary>
        public const double DurationSec = 1.1111;

        /// <summary>当前相位 0..1，由外层计时器每帧写进来。</summary>
        public double Phase { get; set; }

        public void Draw(ICanvas canvas, RectF rect)
        {
            try
            {
                float size = Math.Min(rect.Width, rect.Height);
                if (size <= 2) return;

                float scale = size / CanvasSize;

                // 每帧读一次主题色（在设置里换主题，点阵立刻跟着变）
                Color primary;
                try { primary = AppSettings.ThemeColor; }
                catch { primary = Color.FromRgb(173, 228, 255); }
                var back = primary.WithLuminosity(BackLuminosity);

                canvas.SaveState();
                canvas.Translate(rect.X, rect.Y);
                canvas.Scale(scale, scale);

                for (int row = 0; row < Grid; row++)
                {
                    for (int col = 0; col < Grid; col++)
                    {
                        // 沿对角线传播：越靠右下，相位越靠后
                        double p = Phase - (col + row) * PhaseStep;

                        float grow = Trapezoid(p);                               // 前块大小比例 0..1
                        float backAlpha = 0.58f + 0.32f * Trapezoid(p + 0.18);   // 底块 0.58~0.90
                        float frontAlpha = 0.84f + 0.16f * Trapezoid(p + 0.10);  // 前块 0.84~1.00

                        float x = CellMargin + col * CellStep;
                        float y = CellMargin + row * CellStep;

                        // ① 底块：主题色压暗的"影子"，常驻
                        canvas.FillColor = back.WithAlpha(backAlpha);
                        canvas.FillRoundedRectangle(x, y, CellSize, CellSize, CornerRadius);

                        // ② 前块：主题色本体，从中心长大/缩小，带辉光
                        float front = CellSize * grow;
                        if (front > 0.5f)
                        {
                            float offset = (CellSize - front) / 2f;
                            float radius = Math.Min(front / 2f, CornerRadius * front / CellSize);

                            // 辉光的模糊半径要跟着缩放走，否则控件很小时会糊成一圈光晕
                            canvas.SetShadow(new SizeF(0, 0), 8f * scale, primary.WithAlpha(frontAlpha));
                            canvas.FillColor = primary.WithAlpha(frontAlpha);
                            canvas.FillRoundedRectangle(x + offset, y + offset, front, front, radius);
                            canvas.SetShadow(SizeF.Zero, 0, Colors.Transparent);
                        }
                    }
                }

                canvas.RestoreState();
            }
            catch { /* 画崩了也不该影响聊天 */ }
        }

        /// <summary>
        /// 梯形波：先线性升、停、再线性降、停，返回 0..1。
        /// 原数据的采样序列正是"线性斜坡 + 平台"（斜坡约占周期 21%），所以用梯形而不是正弦。
        /// </summary>
        private static float Trapezoid(double x)
        {
            x -= Math.Floor(x);              // 取小数部分 → 0..1
            const double ramp = 0.21;        // 升降各占 21%，其余是平台

            if (x < ramp) return (float)(x / ramp);
            if (x < 0.5) return 1f;
            if (x < 0.5 + ramp) return (float)(1 - (x - 0.5) / ramp);
            return 0f;
        }
    }
}


// ────────────── FloatingLabelEntry（谷歌 Material 浮动标签输入框）──────────────
// 2026-11-16 新增：替代「只靠 Placeholder」的普通 Entry。
//   未输入 + 未聚焦：标签显示在框内（大号、灰）；
//   聚焦 或 已有内容：标签缩小上移到框上方，之后一直显示（不消失）。
//   框下方有「帮助文字」(HelperText，常显，输入时不消失) 和「错误文字」(ErrorText，失焦校验后显示)。
public class FloatingLabelEntry : ContentView
{
    private readonly Grid _root;
    private readonly Border _box;
    private readonly Entry _entry;
    private readonly Label _floatLabel;
    private readonly Label _helper;
    private readonly Label _error;

    private bool _floating;
    private bool _suppress;

    /// <summary>标签文案（= 原来的 Placeholder，但它会浮动，不消失）。</summary>
    public static readonly BindableProperty LabelTextProperty = BindableProperty.Create(
        nameof(LabelText), typeof(string), typeof(FloatingLabelEntry), "",
        propertyChanged: (b, o, n) => ((FloatingLabelEntry)b)._floatLabel.Text = (string?)n ?? "");

    /// <summary>帮助文字（常显，输入时不消失；例如「密码至少 8 位」）。</summary>
    public static readonly BindableProperty HelperTextProperty = BindableProperty.Create(
        nameof(HelperText), typeof(string), typeof(FloatingLabelEntry), "",
        propertyChanged: (b, o, n) => ((FloatingLabelEntry)b)._helper.Text = (string?)n ?? "");

    /// <summary>错误文字（失焦校验不通过时显示在下方，说明怎么改）。</summary>
    public static readonly BindableProperty ErrorTextProperty = BindableProperty.Create(
        nameof(ErrorText), typeof(string), typeof(FloatingLabelEntry), "",
        propertyChanged: (b, o, n) => ((FloatingLabelEntry)b).ApplyError((string?)n));

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(FloatingLabelEntry), "",
        BindingMode.TwoWay,
        propertyChanged: (b, o, n) => ((FloatingLabelEntry)b).OnTextChanged());

    public static readonly BindableProperty IsPasswordProperty = BindableProperty.Create(
        nameof(IsPassword), typeof(bool), typeof(FloatingLabelEntry), false,
        propertyChanged: (b, o, n) => ((FloatingLabelEntry)b)._entry.IsPassword = (bool)n!);

    public static readonly BindableProperty KeyboardProperty = BindableProperty.Create(
        nameof(Keyboard), typeof(Keyboard), typeof(FloatingLabelEntry), Keyboard.Default,
        propertyChanged: (b, o, n) => ((FloatingLabelEntry)b)._entry.Keyboard = (Keyboard)n!);

    /// <summary>失焦校验回调：返回错误文案，null/空 = 通过。设置它才能启用「失焦后才校验」。</summary>
    public Func<string?, string?>? Validator { get; set; }

    public event EventHandler<TextChangedEventArgs>? TextChanged;
    public event EventHandler? Completed;
    // 注意：这两个事件必须用 EventHandler<FocusEventArgs>，与基类 VisualElement 的签名一致。
    // 若用无参 EventHandler 会遮蔽基类 Focused/Unfocused，导致 XAML 里 Unfocused="处理函数"
    // 在 Release(XamlC AOT) 下报 XC0002「EventHandler 签名不匹配」（Debug 运行时绑定不报，故易漏）。
    public event EventHandler<FocusEventArgs>? Focused;
    public event EventHandler<FocusEventArgs>? Unfocused;

    public FloatingLabelEntry()
    {
        _floatLabel = new Label
        {
            FontSize = 15,
            TextColor = Color.FromArgb("#9E8D8C"),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            InputTransparent = true,
            TranslationY = 16,   // 初始：居中在框内
        };

        _entry = new Entry
        {
            FontSize = 15,
            TextColor = Colors.White,
            BackgroundColor = Colors.Transparent,
            Placeholder = "",
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 10, 0, 0),   // 给上方浮动的标签留位置
        };
        _entry.TextChanged += (s, e) => { if (!_suppress) TextChanged?.Invoke(this, e); };
        _entry.Focused += OnEntryFocused;
        _entry.Unfocused += OnEntryUnfocused;
        _entry.Completed += (s, e) => Completed?.Invoke(this, EventArgs.Empty);

        _box = new Border
        {
            BackgroundColor = Color.FromArgb("#2F2928"),
            Stroke = Color.FromArgb("#2B2524"),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = new Thickness(12, 4, 12, 4),
            Content = _entry,
        };

        _helper = new Label
        {
            FontSize = 12,
            TextColor = Color.FromArgb("#9E8D8C"),
            Margin = new Thickness(2, 4, 0, 0),
            LineBreakMode = LineBreakMode.WordWrap,
        };

        _error = new Label
        {
            FontSize = 12,
            TextColor = Color.FromArgb("#FF8A80"),
            Margin = new Thickness(2, 2, 0, 0),
            IsVisible = false,
            LineBreakMode = LineBreakMode.WordWrap,
        };

        var stack = new VerticalStackLayout { Spacing = 0 };
        // 框 + 浮动标签叠一起
        var grid = new Grid { HeightRequest = 50 };
        grid.Children.Add(_box);
        grid.Children.Add(_floatLabel);
        Grid.SetColumn(_floatLabel, 0);
        _floatLabel.Margin = new Thickness(14, 0, 0, 0);   // 与 Entry 文字左对齐

        stack.Children.Add(grid);
        stack.Children.Add(_helper);
        stack.Children.Add(_error);

        _root = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star) } };
        _root.Children.Add(stack);

        Content = stack;
    }

    // ── 公开属性转发 ──
    public string LabelText { get => (string)GetValue(LabelTextProperty); set => SetValue(LabelTextProperty, value); }
    public string HelperText { get => (string)GetValue(HelperTextProperty); set => SetValue(HelperTextProperty, value); }
    public string ErrorText { get => (string)GetValue(ErrorTextProperty); set => SetValue(ErrorTextProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsPassword { get => (bool)GetValue(IsPasswordProperty); set => SetValue(IsPasswordProperty, value); }
    public Keyboard Keyboard { get => (Keyboard)GetValue(KeyboardProperty); set => SetValue(KeyboardProperty, value); }

    /// <summary>给外部清空错误用（例如重新聚焦时收起错误）。</summary>
    public void ClearError() => ErrorText = "";

    private void OnTextChanged()
    {
        _suppress = true;
        _entry.Text = Text ?? "";
        _suppress = false;
        UpdateFloatState();
    }

    private void OnEntryFocused(object? sender, FocusEventArgs e)
    {
        ClearError();
        UpdateFloatState();
        Focused?.Invoke(this, e);
    }

    private void OnEntryUnfocused(object? sender, FocusEventArgs e)
    {
        // 失焦后才校验，不在输入过程中报错
        var err = Validator?.Invoke(_entry.Text);
        ErrorText = err ?? "";
        UpdateFloatState();
        Unfocused?.Invoke(this, e);
    }

    private void UpdateFloatState()
    {
        bool shouldFloat = _entry.IsFocused || !string.IsNullOrEmpty(_entry.Text);
        if (shouldFloat == _floating) return;
        _floating = shouldFloat;

        if (shouldFloat)
        {
            _ = _floatLabel.TranslateToAsync(0, -8, 150, Easing.CubicOut);
            _floatLabel.FontSize = 11;
            _floatLabel.TextColor = _entry.IsFocused ? AppSettings.ThemeColor : Color.FromArgb("#9E8D8C");
            _entry.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            _ = _floatLabel.TranslateToAsync(0, 16, 150, Easing.CubicOut);
            _floatLabel.FontSize = 15;
            _floatLabel.TextColor = Color.FromArgb("#9E8D8C");
            _entry.Margin = new Thickness(0, 10, 0, 0);
        }
    }

    private void ApplyError(string? msg)
    {
        bool has = !string.IsNullOrWhiteSpace(msg);
        _error.Text = msg ?? "";
        _error.IsVisible = has;
        _helper.IsVisible = !has || string.IsNullOrEmpty(_helper.Text);
        _box.Stroke = has ? Color.FromArgb("#FF8A80") : Color.FromArgb("#2B2524");
    }

    /// <summary>外部主动聚焦输入框。</summary>
    public new void Focus() => _entry.Focus();
}
// ────────────── OtpInput（6 位验证码独立方格）──────────────
// 2026-11-16 新增：短信/邮箱验证码输入。
//   · 6 个独立 Entry 方格，输入后自动跳下一格，退格时回到上一格；
//   · 支持一次粘贴整段验证码（含「123456」「123 456」「您的验证码是123456」等），
//     自动抽取数字并分格填入 —— 不会全挤进第一格；
//   · 每格只收 1 个字符，多余字符自动顺延到后续格子（覆盖式补齐）。
public class OtpInput : ContentView
{
    private readonly Entry[] _boxes = new Entry[6];
    private readonly Border[] _borders = new Border[6];
    private bool _suppress;

    /// <summary>当前完整的 6 位验证码（不足 6 位返回空串）。</summary>
    public string Code
    {
        get
        {
            var s = string.Concat(_boxes.Select(b => b.Text ?? ""));
            return s.Length == 6 ? s : "";
        }
    }

    /// <summary>验证码填满时触发（用于自动提交）。</summary>
    public event EventHandler<string>? CodeCompleted;

    public OtpInput()
    {
        var row = new HorizontalStackLayout { Spacing = 8, HorizontalOptions = LayoutOptions.Center };

        for (int i = 0; i < 6; i++)
        {
            int idx = i;   // 闭包捕获
            var entry = new Entry
            {
                Keyboard = Keyboard.Numeric,
                MaxLength = 1,
                FontSize = 22,
                FontAttributes = FontAttributes.Bold,
                TextColor = Colors.White,
                BackgroundColor = Colors.Transparent,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
                WidthRequest = 44,
                HeightRequest = 54,
                IsTextPredictionEnabled = false,
            };
            entry.TextChanged += (s, e) => OnBoxTextChanged(idx, e);
            entry.Focused += (s, e) => Highlight(idx, true);
            entry.Unfocused += (s, e) => Highlight(idx, false);

            var border = new Border
            {
                BackgroundColor = Color.FromArgb("#2F2928"),
                Stroke = Color.FromArgb("#2B2524"),
                StrokeThickness = 1,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Content = entry,
            };

            _boxes[idx] = entry;
            _borders[idx] = border;
            row.Children.Add(border);
        }

        Content = row;
    }

    private void Highlight(int idx, bool focused)
    {
        if (idx < 0 || idx >= _borders.Length) return;
        _borders[idx].Stroke = focused ? AppSettings.ThemeColor : Color.FromArgb("#2B2524");
        _borders[idx].StrokeThickness = focused ? 2 : 1;
    }

    /// <summary>
    /// 单格文本变化：实现「输入即跳下一格」+「粘贴整段自动分格」+「退格回上一格」。
    /// </summary>
    private void OnBoxTextChanged(int idx, TextChangedEventArgs e)
    {
        if (_suppress) return;

        string newText = e.NewTextValue ?? "";

        // 情况 A：该格被清空（退格）→ 跳回上一格并聚焦（把光标放在上一格末尾）
        if (string.IsNullOrEmpty(newText))
        {
            if (idx > 0)
            {
                _boxes[idx - 1].Focus();
            }
            RaiseIfComplete();
            return;
        }

        // 情况 B：输入法把「一整段/多位」塞进了这一格（一键粘贴、自动填充）。
        //   抽出所有数字，从当前格开始依次铺开，不全挤进第一格。
        string digits = new string(newText.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            // 非数字字符：清掉这一格
            _suppress = true; _boxes[idx].Text = ""; _suppress = false;
            return;
        }

        if (digits.Length > 1)
        {
            DistributeFrom(idx, digits);
            return;
        }

        // 情况 C：正常单字符输入 → 截断到 1 位并跳下一格
        _suppress = true;
        _boxes[idx].Text = digits[0].ToString();
        _suppress = false;
        if (idx < 5) _boxes[idx + 1].Focus();
        RaiseIfComplete();
    }

    /// <summary>从 from 开始，把 digits 依次铺进后续格子。</summary>
    private void DistributeFrom(int from, string digits)
    {
        _suppress = true;
        int pos = 0;
        for (int i = from; i < 6 && pos < digits.Length; i++, pos++)
            _boxes[i].Text = digits[pos].ToString();
        _suppress = false;

        // 聚焦到下一个空格（或最后一格）
        int next = from + digits.Length;
        if (next >= 6) next = 5;
        _boxes[next].Focus();
        RaiseIfComplete();
    }

    /// <summary>清空全部并聚焦第一格。</summary>
    public void Clear()
    {
        _suppress = true;
        foreach (var b in _boxes) b.Text = "";
        _suppress = false;
        _boxes[0].Focus();
    }

    /// <summary>自动填入一整段验证码（例如从剪贴板/短信读出）。</summary>
    public void SetCode(string digits)
    {
        string d = new string((digits ?? "").Where(char.IsDigit).ToArray());
        if (d.Length > 6) d = d[..6];
        DistributeFrom(0, d);
    }

    private void RaiseIfComplete()
    {
        var code = Code;
        if (!string.IsNullOrEmpty(code))
            CodeCompleted?.Invoke(this, code);
    }
}
#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;

namespace 青阳AI;

/// <summary>
/// 长按电源键（系统判定为「唤醒助手」）时，Android 会把本 Activity 拉起。
///
/// 职责只有一件事：
///   1. 播放唤醒光效（悬浮层，若已授权悬浮窗）；
///   2. 打开青阳AI 聊天页。
///
/// 之所以做成独立 Activity 而不是复用 MainActivity：助手唤醒走的是
/// ACTION_ASSIST intent，系统要求被唤醒的组件有明确的 intent-filter，
/// 且要能独立冷启动。
/// </summary>
[Activity(
    Name = "com.qingyang.ai.QyAssistActivity",
    Label = "青阳AI 助手",
    Exported = true,
    Theme = "@android:style/Theme.NoDisplay",
    TaskAffinity = "com.qingyang.ai.assist",
    LaunchMode = Android.Content.PM.LaunchMode.SingleTop,
    ExcludeFromRecents = true)]
[IntentFilter(new[] { Intent.ActionAssist },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryVoice })]
[IntentFilter(new[] { "android.intent.action.VOICE_COMMAND" },
    Categories = new[] { Intent.CategoryDefault })]
public class QyAssistActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 1) 先放光效（悬浮层，独立于本 Activity 的界面）
        try
        {
            if (AppSettings.AssistantGlowEnabled)
                AssistantGlowOverlay.Play(ApplicationContext!);
        }
        catch { }

        // 2) 再把用户带到聊天页
        try
        {
            var intent = new Intent(this, typeof(MainActivity));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            StartActivity(intent);
        }
        catch { }

        // 本 Activity 只是"引子"，不显示任何界面，立刻结束
        Finish();
        OverridePendingTransition(0, 0);
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        // 电源键不会派发到这里（系统独占），保留默认行为
        return base.OnKeyDown(keyCode, e);
    }
}
#endif

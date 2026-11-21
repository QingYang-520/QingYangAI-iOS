#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;

namespace 青阳AI;

/// <summary>
/// 本地模型下载的**前台服务**：常驻通知栏显示进度，保证 App 切后台/锁屏时下载与安装不中断。
/// 只在 Android 上编译（本文件在 Platforms/Android 下）。
/// </summary>
[Service(ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync,
         Exported = false,
         Label = "青阳AI 模型下载")]
public class LocalModelService : Service
{
    public const string ExtraKey = "key";
    public const string ExtraTitle = "title";
    public const string ExtraText = "text";
    public const string ExtraPercent = "percent";
    public const string ExtraDone = "done";
    public const string ActionUpdate = "com.qingyang.ai.MODEL_PROGRESS";
    public const string ActionStop = "com.qingyang.ai.MODEL_STOP";

    public const string ChannelId = "local_model_download";
    private const int BaseNotifyId = 5100;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            EnsureChannel();
            var action = intent?.Action ?? "";
            var key = intent?.GetStringExtra(ExtraKey) ?? "";

            if (action == ActionStop)
            {
                StopForeground(true);
                StopSelf();
                return StartCommandResult.NotSticky;
            }

            var title = intent?.GetStringExtra(ExtraTitle) ?? "模型下载";
            var text = intent?.GetStringExtra(ExtraText) ?? "";
            var pct = intent?.GetIntExtra(ExtraPercent, 0) ?? 0;
            var done = intent?.GetBooleanExtra(ExtraDone, false) ?? false;

            var id = BaseNotifyId + Math.Abs(key.GetHashCode() % 400);
            var notification = Build(notificationId: id, title: title, text: text, percent: pct, done: done);

#if NET9_0_OR_GREATER
            if (Android.OS.Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(id, notification,
                    global::Android.Content.PM.ForegroundService.TypeDataSync);
            else
                StartForeground(id, notification);
#else
            StartForeground(id, notification);
#endif

            if (done)
            {
                // 结束：撤下前台身份（通知保留一小会儿让用户看到"已完成"）
                if (Android.OS.Build.VERSION.SdkInt >= BuildVersionCodes.N) StopForeground(StopForegroundFlags.Detach);
                else StopForeground(false);
                StopSelf();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LocalModelService] {ex.Message}");
        }
        return StartCommandResult.NotSticky;
    }

    private Notification Build(int notificationId, string title, string text, int percent, bool done)
    {
        var open = new Intent(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var pi = PendingIntent.GetActivity(this, notificationId, open,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = Android.OS.Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);

        builder.SetContentTitle(title)
               .SetContentText(text)
               .SetSmallIcon(Resource.Drawable.dotnet_bot)
               .SetContentIntent(pi)
               .SetOngoing(!done)
               .SetOnlyAlertOnce(true)
               .SetAutoCancel(done);

        if (!done)
            builder.SetProgress(100, Math.Clamp(percent, 0, 100), percent <= 0);
        else
            builder.SetProgress(0, 0, false);

#pragma warning disable CA1422
        if (Android.OS.Build.VERSION.SdkInt < BuildVersionCodes.O)
            builder.SetPriority((int)NotificationPriority.Low);
#pragma warning restore CA1422

        return builder.Build();
    }

    private void EnsureChannel()
    {
        if (Android.OS.Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager == null || manager.GetNotificationChannel(ChannelId) != null) return;
        var ch = new NotificationChannel(ChannelId, "模型下载", NotificationImportance.Low)
        {
            Description = "本地大模型下载与部署进度",
        };
        ch.SetShowBadge(false);
        manager.CreateNotificationChannel(ch);
    }
}

/// <summary><see cref="AndroidBackground"/> 的 Android 实现。</summary>
public static partial class AndroidBackground
{
    static partial void StartImpl(string key, string title)
    {
        Dispatch(key, title, "准备中…", 0, false);
    }

    static partial void UpdateImpl(string key, string title, string text, int percent, bool done)
    {
        Dispatch(key, title, text, percent, done);
    }

    static partial void StopImpl(string key)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var i = new Intent(ctx, typeof(LocalModelService));
            i.SetAction(LocalModelService.ActionStop);
            i.PutExtra(LocalModelService.ExtraKey, key);
            ctx.StartService(i);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AndroidBackground.Stop] {ex.Message}"); }
    }

    private static void Dispatch(string key, string title, string text, int percent, bool done)
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var i = new Intent(ctx, typeof(LocalModelService));
            i.SetAction(LocalModelService.ActionUpdate);
            i.PutExtra(LocalModelService.ExtraKey, key);
            i.PutExtra(LocalModelService.ExtraTitle, title);
            i.PutExtra(LocalModelService.ExtraText, text);
            i.PutExtra(LocalModelService.ExtraPercent, percent);
            i.PutExtra(LocalModelService.ExtraDone, done);
            if (Android.OS.Build.VERSION.SdkInt >= BuildVersionCodes.O)
                ctx.StartForegroundService(i);
            else
                ctx.StartService(i);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[AndroidBackground.Dispatch] {ex.Message}"); }
    }
}
#endif

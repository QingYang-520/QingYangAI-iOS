using Microsoft.Maui.Controls;

namespace 青阳AI;

/// <summary>日记页：按日期倒序展示Ta写的每日日记；打开时顺手补写昨日日记。</summary>
public partial class DiaryPage : ContentPage
{
    public DiaryPage()
    {
        InitializeComponent();
        ThemeManager.Apply(this);   // 应用当前主题色
        Loaded += (_, _) => JellyAnim.EnterPage(Content);   // 页面进入动效
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            // 相伴天数（确保首次相遇时间已初始化）
            await ChatStore.Instance.CountMemoriesAsync();
            lblDays.Text = $"已陪伴 {AppSettings.CompanionDays} 天";

            diaryList.ItemsSource = await ChatStore.Instance.GetDiariesAsync();

            // 若昨日日记还没生成，生成完刷新列表
            await DiaryService.CheckYesterdayDiaryAsync();
            diaryList.ItemsSource = await ChatStore.Instance.GetDiariesAsync();
            JellyAnim.PopIn(diaryList);   // 列表回填：轻微果冻弹入
        }
        catch { /* 加载失败保持空列表 */ }
    }

    /// <summary>跳到「这段时间」情绪曲线页。</summary>
    private async void OnEmotionTapped(object? sender, TappedEventArgs e)
    {
        try { await Navigation.PushAsync(new EmotionPage()); }
        catch { /* 导航失败静默 */ }
    }
}

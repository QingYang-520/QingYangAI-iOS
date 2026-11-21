using Microsoft.Maui.Controls;
using System.Collections.ObjectModel;

namespace 青阳AI;

/// <summary>
/// 收藏列表的一条（视图模型）。
/// 不直接绑 ChatMsg 是因为列表需要「谁说的 / 说话人配色 / 左右对齐」这些展示字段，
/// 而且图片消息的正文可能为空，得在这里兜一层占位文案。
/// </summary>
public class FavoriteItem
{
    /// <summary>消息主键（取消收藏 / 复制时用）。</summary>
    public int MsgId { get; init; }

    /// <summary>正文（复制用原文；展示走 DisplayContent）。</summary>
    public string Content { get; init; } = "";

    /// <summary>列表里显示的正文（空正文兜占位）。</summary>
    public string DisplayContent { get; init; } = "";

    /// <summary>说话的人：你 / Ta。</summary>
    public string Who { get; init; } = "";

    /// <summary>说话人那行小字的颜色。</summary>
    public Color WhoColor { get; init; } = Colors.White;

    /// <summary>用户消息靠右、AI 消息靠左。</summary>
    public LayoutOptions Align { get; init; } = LayoutOptions.Start;

    /// <summary>时间（年-月-日 时:分）。</summary>
    public string TimeText { get; init; } = "";
}

/// <summary>
/// 我的收藏页：列出所有被收藏的消息，可复制、可取消收藏。
/// 数据层走 ChatStore.GetFavoritesAsync / SetFavoriteAsync。
/// </summary>
public partial class FavoritePage : ContentPage
{
    /// <summary>最多拉多少条（跟 ChatStore.GetFavoritesAsync 的默认值对齐）。</summary>
    private const int FetchLimit = 100;

    /// <summary>
    /// 列表数据源。用 ObservableCollection 而不是 List：
    /// 取消收藏时就地 Remove 就能让 CollectionView 自己刷新，
    /// 不用整个 ItemsSource 换一遍（换一遍会跳回列表顶部，体验很差）。
    /// </summary>
    private readonly ObservableCollection<FavoriteItem> _items = new();

    public FavoritePage()
    {
        InitializeComponent();
        ThemeManager.Apply(this);   // 应用当前主题色
        favList.ItemsSource = _items;
        Loaded += (_, _) => JellyAnim.EnterPage(Content);   // 页面进入动效
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = LoadAsync();
    }

    /// <summary>拉取收藏列表并刷新条数。</summary>
    private async Task LoadAsync()
    {
        try
        {
            var msgs = await ChatStore.Instance.GetFavoritesAsync(FetchLimit);

            _items.Clear();
            foreach (var m in msgs)
                _items.Add(ToItem(m));

            UpdateCountLabel();
            if (_items.Count > 0) JellyAnim.PopIn(favList);   // 列表回填：轻微果冻弹入
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FavoritePage] {ex.Message}");
        }
    }

    /// <summary>刷新右上角条数（0 条时留空，空态文案已经说清楚了）。</summary>
    private void UpdateCountLabel()
    {
        lblCount.Text = _items.Count > 0 ? $"{_items.Count} 条" : "";
    }

    /// <summary>ChatMsg → FavoriteItem。</summary>
    private static FavoriteItem ToItem(ChatMsg m)
    {
        var body = (m.Content ?? "").Trim();
        return new FavoriteItem
        {
            MsgId = m.Id,
            Content = m.Content ?? "",
            DisplayContent = body.Length > 0 ? body : "[图片或语音消息]",
            Who = m.IsUser ? "你" : "Ta",
            WhoColor = m.IsUser ? Color.FromArgb("#FBB5B2") : Color.FromArgb("#B9D5C4"),
            Align = m.IsUser ? LayoutOptions.End : LayoutOptions.Start,
            TimeText = m.Timestamp.ToString("yyyy-MM-dd HH:mm")
        };
    }

    /// <summary>自绘返回按钮。</summary>
    private async void OnBackTapped(object? sender, EventArgs e)
    {
        try { await Navigation.PopAsync(); } catch { }
    }

    /// <summary>「复制」按钮。</summary>
    private async void OnCopyTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is FavoriteItem item) await CopyAsync(item);
    }

    /// <summary>「取消收藏」按钮。</summary>
    private async void OnUnfavoriteTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is FavoriteItem item) await UnfavoriteAsync(item);
    }

    /// <summary>
    /// 「去聊天看」：退回聊天页并定位到这条消息。
    ///
    /// 收藏页是从设置页进来的（ChatPage → Settings → FavoritePage），
    /// 所以要一路退两层才回到聊天页。
    /// 定位不走 ChatJump 静态回调 —— 那条路只有「打开过搜索页」时才挂上，
    /// 直接走导航栈找到栈底的 ChatPage 实例调公开方法，任何进入路径都能用。
    /// </summary>
    private async void OnJumpTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not FavoriteItem item) return;
        try
        {
            var nav = Navigation;
            // 最多退两层，别把不相干的页面也一起退掉
            for (int i = 0; i < 2 && nav.NavigationStack.Count > 1; i++)
                await nav.PopAsync();

            // 退完之后栈里剩下的就是聊天页（它是根页）
            foreach (var page in nav.NavigationStack)
            {
                if (page is ChatPage chat)
                {
                    chat.ScrollToMessageId(item.MsgId);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FavoritePage OnJumpTapped] {ex.Message}");
        }
    }

    /// <summary>复制正文到剪贴板。</summary>
    private async Task CopyAsync(FavoriteItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.Content))
            {
                await DisplayAlertAsync("复制失败", "这条消息没有文字内容。", "知道了");
                return;
            }
            await Clipboard.Default.SetTextAsync(item.Content);
            await DisplayAlertAsync("已复制", "这条消息已复制到剪贴板。", "知道了");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("复制失败", ex.Message, "知道了");
        }
    }

    /// <summary>取消收藏：改库 → 从列表里摘掉 → 刷新条数。</summary>
    private async Task UnfavoriteAsync(FavoriteItem item)
    {
        try
        {
            await ChatStore.Instance.SetFavoriteAsync(item.MsgId, false);

            // ObservableCollection.Remove 会让 CollectionView 就地刷新，
            // 滚动位置不会跳（比换 ItemsSource 体验好得多）
            _items.Remove(item);
            UpdateCountLabel();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("操作失败", ex.Message, "知道了");
        }
    }
}

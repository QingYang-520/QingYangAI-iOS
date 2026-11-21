namespace 青阳AI;

/// <summary>
/// 功能说明页：把设置里各功能原本散落的**括号注释**与**小字提示**集中展示。
///
/// 正文来自 <see cref="FeatureGuideContent.Markdown"/>（唯一来源），
/// 排版交给 <see cref="MarkdownRenderer"/> —— 与协议页同一套做法。
/// </summary>
public partial class FeatureGuidePage : ContentPage
{
    public FeatureGuidePage()
    {
        InitializeComponent();
        guideBody.Add(MarkdownRenderer.Render(FeatureGuideContent.Markdown));
        ThemeManager.Apply(this);   // 应用当前主题色
        Loaded += (_, _) => JellyAnim.EnterPage(Content);   // 页面进入动效
    }
}

// 登录 API 令牌（LoginVault）编译期常量。
// 明文绝不进仓库：这里默认是空串。
// CI（GitHub Actions）打包前会用 Secret LOGIN_API_TOKEN 把空串替换成真实令牌，
// 因此云端产出的 IPA 登录功能正常；本地直接用此文件则需在设置里手填令牌。
internal static class BuildConstants
{
    public const string LoginApiToken = "";
}

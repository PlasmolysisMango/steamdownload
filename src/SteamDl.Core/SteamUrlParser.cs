// Steam 链接解析,与 Web UI 支持的输入类型保持一致
using System.Text.RegularExpressions;

namespace SteamDl.Core
{
    public static class SteamUrlParser
    {
        static readonly (string Kind, Regex Pattern)[] Patterns =
        [
            ("app", new Regex(@"store\.steampowered\.com/app/(\d+)")),
            ("workshop", new Regex(@"steamcommunity\.com/sharedfiles/filedetails/\?id=(\d+)")),
            ("workshop", new Regex(@"steamcommunity\.com/workshop/filedetails/\?id=(\d+)")),
            ("app", new Regex(@"steam://(?:store|install|run|rungameid)/(\d+)")),
            ("app", new Regex(@"^\s*(\d+)\s*$")),
        ];

        /// <summary>解析商店链接 / steam:// 协议链接 / 纯 appid,失败返回 null。</summary>
        public static (string Kind, string Id)? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            foreach (var (kind, pattern) in Patterns)
            {
                var match = pattern.Match(text);
                if (match.Success)
                {
                    return (kind, match.Groups[1].Value);
                }
            }

            return null;
        }
    }
}

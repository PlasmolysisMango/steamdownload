// Steam 商店/社区 Web API 访问:游戏信息代理(带缓存) + 创意工坊物品所属 App 解析
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SteamDl.Core
{
    public static class AppInfoService
    {
        static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
        static readonly ConcurrentDictionary<uint, JsonObject> Cache = new();

        /// <summary>取商店 appdetails,返回给前端的精简 JSON;商店无信息时返回 null。</summary>
        public static async Task<JsonObject> GetAppInfoAsync(uint appId)
        {
            if (Cache.TryGetValue(appId, out var cached))
            {
                return cached;
            }

            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l=schinese";
            var body = await Http.GetStringAsync(url).ConfigureAwait(false);
            var root = JsonNode.Parse(body)?[appId.ToString()];
            if (root?["success"]?.GetValue<bool>() != true)
            {
                return null;
            }

            var data = root["data"];
            var info = new JsonObject
            {
                ["appid"] = appId,
                ["name"] = data?["name"]?.GetValue<string>() ?? string.Empty,
                ["header_image"] = data?["header_image"]?.GetValue<string>() ?? string.Empty,
                ["is_free"] = data?["is_free"]?.GetValue<bool>() ?? false,
                ["type"] = data?["type"]?.GetValue<string>() ?? string.Empty,
                ["installdir"] = data?["config"]?["installdir"]?.GetValue<string>() ?? string.Empty,
                ["install_dir"] = data?["config"]?["installdir"]?.GetValue<string>() ?? string.Empty,
                ["size_bytes"] = ReadUInt64(data?["size_bytes"] ?? data?["size"] ?? data?["depots"]?["branches"]?["public"]?["download_size"]),
            };
            Cache[appId] = info;
            return info;
        }

        static ulong ReadUInt64(JsonNode node)
        {
            if (node == null) return 0;
            if (node is JsonValue value)
            {
                if (value.TryGetValue<ulong>(out var ulongValue)) return ulongValue;
                if (value.TryGetValue<long>(out var longValue) && longValue > 0) return (ulong)longValue;
                if (value.TryGetValue<string>(out var stringValue) && ulong.TryParse(stringValue, out var parsed)) return parsed;
            }
            return 0;
        }

        /// <summary>解析创意工坊物品所属的消费端 AppID(DownloadPubfileAsync 需要)。</summary>
        public static async Task<uint> ResolveWorkshopAppIdAsync(ulong publishedFileId)
        {
            const string url = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["itemcount"] = "1",
                ["publishedfileids[0]"] = publishedFileId.ToString(),
            });

            using var resp = await Http.PostAsync(url, form).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var detail = JsonNode.Parse(body)?["response"]?["publishedfiledetails"]?[0];

            var appId = detail?["consumer_app_id"]?.GetValue<uint>() ?? 0;
            if (appId == 0)
            {
                throw new InvalidOperationException($"无法解析创意工坊物品 {publishedFileId} 所属的 AppID");
            }

            return appId;
        }
    }
}

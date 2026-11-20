using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Stellar.AgyBridge.Bridge.Storage;

namespace Stellar.AgyBridge.Bridge.Core;

/// <summary>
/// Localization lookup for KO / EN / JA bridge policy labels, follow-cancel messages, and runtime strings,
/// backed by the embedded <c>Lang/*.json</c> resources.
/// </summary>
public static class BridgeI18n
{
    private const string PolicyPrefix = "bridge.policy.";
    private const string FollowCancelPrefix = "bridge.follow_cancel.";
    private const string MsgPrefix = "bridge.msg.";

    private static readonly Regex PlaceholderRegex = new(@"\{([A-Za-z0-9_]+)\}", RegexOptions.Compiled);

    public static IReadOnlyList<string> SupportedLanguages { get; } = new[] { "ko", "en", "ja" };

    public static IReadOnlyDictionary<string, string> DefaultPersonas { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ko"] = "당신은 블루 프로토콜 스타 레조넌스를 플레이 하는 유저입니다.",
            ["en"] = "You are a player playing Blue Protocol: Star Resonance.",
            ["ja"] = "あなたはブループロトコル：スターレゾナンスをプレイしているユーザーです。",
        };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Catalogs =
        new(LoadAllCatalogs);

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LoadAllCatalogs()
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (string code in SupportedLanguages)
        {
            string json = EmbeddedAssets.ReadText($"Stellar.AgyBridge.Lang.{code}.json");
            using JsonDocument doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    map[prop.Name] = prop.Value.GetString() ?? "";
            }
            result[code] = map;
        }
        return result;
    }

    public static string NormalizeUiLanguage(string? raw)
    {
        string s = (raw ?? "").Trim().ToLowerInvariant().Replace('_', '-');
        if (string.IsNullOrEmpty(s))
            return "ko";
        int dash = s.IndexOf('-');
        string baseCode = dash >= 0 ? s[..dash] : s;
        if (baseCode == "ko")
            return "ko";
        if (baseCode is "ja" or "jp")
            return "ja";
        return "en";
    }

    public static string DefaultPersonaForLang(string? lang)
    {
        string code = NormalizeUiLanguage(lang);
        return DefaultPersonas.TryGetValue(code, out string? persona)
            ? persona
            : DefaultPersonas["ko"];
    }

    public static string PolicyLabel(string policyKey, string? lang = "ko")
    {
        string? raw = LookupKey(PolicyPrefix + policyKey, lang);
        return raw ?? policyKey;
    }

    public static string? FollowCancelTemplate(string phase, string? lang = "ko")
    {
        if (string.IsNullOrEmpty(phase))
            return null;
        return LookupKey(FollowCancelPrefix + phase, lang);
    }

    public static string T(string key, string? lang = "ko", IReadOnlyDictionary<string, object?>? args = null)
    {
        string tmpl = LookupKey(MsgPrefix + key, lang) ?? key;
        return FormatTemplate(tmpl, args);
    }

    public static string Format(string key, string? lang, params (string Key, object? Value)[] args)
    {
        if (args.Length == 0)
            return T(key, lang);
        var dict = new Dictionary<string, object?>(args.Length, StringComparer.Ordinal);
        foreach ((string k, object? v) in args)
            dict[k] = v;
        return T(key, lang, dict);
    }

    public static string FormatTemplate(string template, IReadOnlyDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0 || string.IsNullOrEmpty(template))
            return template;

        return PlaceholderRegex.Replace(template, match =>
        {
            string name = match.Groups[1].Value;
            if (args.TryGetValue(name, out object? value))
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            return match.Value;
        });
    }

    private static string? LookupKey(string fullKey, string? lang)
    {
        string code = NormalizeUiLanguage(lang);
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> all = Catalogs.Value;
        if (all.TryGetValue(code, out IReadOnlyDictionary<string, string>? table) && table.TryGetValue(fullKey, out string? val))
            return val;
        if (!string.Equals(code, "ko", StringComparison.Ordinal)
            && all.TryGetValue("ko", out IReadOnlyDictionary<string, string>? koTable)
            && koTable.TryGetValue(fullKey, out string? koVal))
            return koVal;
        return null;
    }
}

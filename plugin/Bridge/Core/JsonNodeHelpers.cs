using System;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Bridge.Core;

/// <summary>
/// Null-safe <see cref="System.Text.Json.Nodes"/> helpers compatible with <c>net6.0</c>.
/// </summary>
public static class JsonNodeHelpers
{
    public static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static JsonNode? DeepClone(JsonNode? node)
    {
        if (node == null)
            return null;
        return JsonNode.Parse(node.ToJsonString(CompactJsonOptions));
    }

    public static JsonObject CloneObject(JsonObject? obj)
    {
        if (obj == null)
            return new JsonObject();
        return DeepClone(obj) as JsonObject ?? new JsonObject();
    }

    public static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string ToCompactJson(JsonNode? node)
        => node?.ToJsonString(CompactJsonOptions) ?? "null";

    public static string GetString(JsonObject? obj, string key, string fallback = "")
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node) || node == null)
            return fallback;
        return AsString(node, fallback);
    }

    public static string AsString(JsonNode? node, string fallback = "")
    {
        if (node is not JsonValue jv)
            return fallback;
        if (jv.TryGetValue(out string? s))
            return s ?? fallback;
        if (jv.TryGetValue(out bool b))
            return b ? "true" : "false";
        if (jv.TryGetValue(out long l))
            return l.ToString(CultureInfo.InvariantCulture);
        if (jv.TryGetValue(out double d))
            return d.ToString(CultureInfo.InvariantCulture);
        return jv.ToString();
    }

    public static bool TryGetInt(JsonNode? node, out int val)
    {
        val = 0;
        if (node is not JsonValue jv)
            return false;
        if (jv.TryGetValue(out bool _))
            return false;
        if (jv.TryGetValue(out int i))
        {
            val = i;
            return true;
        }
        if (jv.TryGetValue(out long l) && l >= int.MinValue && l <= int.MaxValue)
        {
            val = (int)l;
            return true;
        }
        if (jv.TryGetValue(out double d) && !double.IsNaN(d) && !double.IsInfinity(d) && d >= int.MinValue && d <= int.MaxValue)
        {
            val = (int)d;
            return true;
        }
        if (jv.TryGetValue(out string? s))
        {
            string trimmed = (s ?? "").Trim();
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                return true;
        }
        return false;
    }

    public static int GetInt(JsonObject? obj, string key, int fallback = 0)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node) || node == null)
            return fallback;
        return TryGetInt(node, out int val) ? val : fallback;
    }

    public static bool TryGetLong(JsonNode? node, out long val)
    {
        val = 0;
        if (node is not JsonValue jv)
            return false;
        if (jv.TryGetValue(out bool _))
            return false;
        if (jv.TryGetValue(out long l))
        {
            val = l;
            return true;
        }
        if (jv.TryGetValue(out int i))
        {
            val = i;
            return true;
        }
        if (jv.TryGetValue(out double d) && !double.IsNaN(d) && !double.IsInfinity(d) && d >= long.MinValue && d <= long.MaxValue)
        {
            val = (long)d;
            return true;
        }
        if (jv.TryGetValue(out string? s))
        {
            string trimmed = (s ?? "").Trim();
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out val))
                return true;
        }
        return false;
    }

    public static long GetLong(JsonObject? obj, string key, long fallback = 0)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node) || node == null)
            return fallback;
        return TryGetLong(node, out long val) ? val : fallback;
    }

    public static bool TryGetDouble(JsonNode? node, out double val)
    {
        val = 0.0;
        if (node is not JsonValue jv)
            return false;
        if (jv.TryGetValue(out bool _))
            return false;
        if (jv.TryGetValue(out double d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            val = d;
            return true;
        }
        if (jv.TryGetValue(out long l))
        {
            val = l;
            return true;
        }
        if (jv.TryGetValue(out string? s))
        {
            string trimmed = (s ?? "").Trim();
            if (double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed)
                && !double.IsNaN(parsed) && !double.IsInfinity(parsed))
            {
                val = parsed;
                return true;
            }
        }
        return false;
    }

    public static double GetDouble(JsonObject? obj, string key, double fallback = 0.0)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node) || node == null)
            return fallback;
        return TryGetDouble(node, out double val) ? val : fallback;
    }

    public static bool IsTruthy(JsonNode? node)
    {
        if (node == null)
            return false;
        if (node is JsonArray arr)
            return arr.Count > 0;
        if (node is JsonObject obj)
            return obj.Count > 0;
        if (node is not JsonValue jv)
            return false;
        if (jv.TryGetValue(out bool b))
            return b;
        if (jv.TryGetValue(out long l))
            return l != 0;
        if (jv.TryGetValue(out double d))
            return d != 0.0 && !double.IsNaN(d);
        if (jv.TryGetValue(out string? s))
            return !string.IsNullOrEmpty(s);
        return true;
    }

    public static bool GetBool(JsonObject? obj, string key, bool fallback = false)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node) || node == null)
            return fallback;
        bool? parsed = GetNullableBool(obj, key);
        if (parsed.HasValue)
            return parsed.Value;
        return IsTruthy(node);
    }

    public static bool? GetNullableBool(JsonObject? obj, string key)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node) || node == null)
            return null;
        if (node is not JsonValue jv)
            return null;
        if (jv.TryGetValue(out bool b))
            return b;
        if (jv.TryGetValue(out long l))
            return l != 0;
        if (jv.TryGetValue(out string? s))
        {
            string trimmed = (s ?? "").Trim().ToLowerInvariant();
            if (trimmed is "true" or "1" or "yes" or "on")
                return true;
            if (trimmed is "false" or "0" or "no" or "off")
                return false;
        }
        return null;
    }

    /// <summary>
    /// Returns the first key present in <paramref name="obj"/> that parses to a boolean value,
    /// supporting canonical and alias parameter names uniformly.
    /// </summary>
    public static bool? GetFirstNullableBool(JsonObject? obj, params string[] keys)
    {
        if (obj == null || keys == null)
            return null;
        foreach (string key in keys)
        {
            if (string.IsNullOrEmpty(key))
                continue;
            bool? val = GetNullableBool(obj, key);
            if (val.HasValue)
                return val.Value;
        }
        return null;
    }

    /// <summary>
    /// Returns the first non-empty trimmed string value across the given <paramref name="keys"/>.
    /// </summary>
    public static string GetFirstString(JsonObject? obj, params string[] keys)
    {
        if (obj == null || keys == null)
            return "";
        foreach (string key in keys)
        {
            if (string.IsNullOrEmpty(key))
                continue;
            string val = GetString(obj, key, "").Trim();
            if (!string.IsNullOrEmpty(val))
                return val;
        }
        return "";
    }

    /// <summary>
    /// Returns the first integer value parsed across the given <paramref name="keys"/>,
    /// or <paramref name="fallback"/> if none is found.
    /// </summary>
    public static int GetFirstInt(JsonObject? obj, int fallback, params string[] keys)
    {
        if (obj == null || keys == null)
            return fallback;
        foreach (string key in keys)
        {
            if (string.IsNullOrEmpty(key))
                continue;
            if (obj.TryGetPropertyValue(key, out JsonNode? node) && node != null && TryGetInt(node, out int val))
                return val;
        }
        return fallback;
    }

    /// <summary>
    /// Returns the first non-empty string across <paramref name="keys"/>, or if none matches and
    /// the object contains exactly one payload property (excluding <c>type</c> and <c>as_admin</c>)
    /// whose JSON value is strictly a string, returns that trimmed string.
    /// </summary>
    public static string GetFirstStringOrSoleValue(JsonObject? obj, params string[] keys)
    {
        string explicitVal = GetFirstString(obj, keys);
        if (!string.IsNullOrEmpty(explicitVal))
            return explicitVal;
        if (TryGetSolePayloadProperty(obj, out _, out JsonNode? soleNode)
            && soleNode is JsonValue jv
            && jv.TryGetValue(out string? s))
        {
            return (s ?? "").Trim();
        }
        return "";
    }

    /// <summary>
    /// Returns the first boolean value across <paramref name="keys"/>, or if none matches and
    /// the object contains exactly one payload property (excluding <c>type</c> and <c>as_admin</c>),
    /// attempts to parse that property via <see cref="GetNullableBool"/>.
    /// </summary>
    public static bool? GetFirstNullableBoolOrSoleBool(JsonObject? obj, params string[] keys)
    {
        bool? explicitVal = GetFirstNullableBool(obj, keys);
        if (explicitVal.HasValue)
            return explicitVal.Value;
        if (TryGetSolePayloadProperty(obj, out string soleKey, out _))
            return GetNullableBool(obj, soleKey);
        return null;
    }

    private static bool TryGetSolePayloadProperty(JsonObject? obj, out string soleKey, out JsonNode? soleNode)
    {
        soleKey = "";
        soleNode = null;
        if (obj == null)
            return false;

        int count = 0;
        foreach (System.Collections.Generic.KeyValuePair<string, JsonNode?> kv in obj)
        {
            if (string.Equals(kv.Key, "type", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Key, "as_admin", StringComparison.OrdinalIgnoreCase)
                || kv.Value == null)
            {
                continue;
            }
            soleKey = kv.Key;
            soleNode = kv.Value;
            count++;
            if (count > 1)
                return false;
        }
        return count == 1;
    }

    public static JsonObject? GetObject(JsonObject? obj, string key)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node))
            return null;
        return node as JsonObject;
    }

    public static JsonArray? GetArray(JsonObject? obj, string key)
    {
        if (obj == null || !obj.TryGetPropertyValue(key, out JsonNode? node))
            return null;
        return node as JsonArray;
    }
}

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stellar.AgyBridge.Bridge.Core;
using Stellar.AgyBridge.Bridge.Models;
using Stellar.AgyBridge.Bridge.Planning;

namespace Stellar.AgyBridge.Bridge.Runtime;

/// <summary>
/// Markdown stripping and smart <=140-char chunking for plain in-game chat
/// (1:1 port of <c>split_chat_chunks</c> in <c>mcp_bridge/chat_output.py</c>).
/// </summary>
public static class ChatTextChunker
{
    private static readonly Regex FenceRegex = new(@"```[a-zA-Z0-9_-]*", RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(@"^\s*#{1,6}\s+", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^\s*[-*•]\s+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[.!?。])\s+", RegexOptions.Compiled);

    public static List<string> SplitChatChunks(
        string? text,
        int maxChars = BridgeConstants.MaxChatChunkChars,
        int maxChunks = BridgeConstants.MaxChunksPerReply)
    {
        if (string.IsNullOrEmpty(text))
            return new List<string>();

        string cleaned = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace("\r", "\n", StringComparison.Ordinal);
        cleaned = FenceRegex.Replace(cleaned, "");
        cleaned = cleaned.Replace("`", "", StringComparison.Ordinal)
                         .Replace("**", "", StringComparison.Ordinal)
                         .Replace("__", "", StringComparison.Ordinal);

        var lines = new List<string>();
        foreach (string rawLn in cleaned.Split('\n'))
        {
            string ln = HeadingRegex.Replace(rawLn, "").Trim();
            ln = BulletRegex.Replace(ln, "• ");
            ln = WhitespaceRegex.Replace(ln, " ").Trim();
            if (!string.IsNullOrEmpty(ln))
                lines.Add(ln);
        }

        if (lines.Count == 0)
            return new List<string>();

        string joined = string.Join(" / ", lines);
        if (joined.Length <= maxChars)
            return new List<string> { joined };

        var atoms = new List<string>();
        foreach (string ln in lines)
        {
            foreach (string rawPart in SentenceSplitRegex.Split(ln))
            {
                string p = rawPart.Trim();
                if (string.IsNullOrEmpty(p))
                    continue;
                if (p.Length <= maxChars)
                {
                    atoms.Add(p);
                    continue;
                }

                string curW = "";
                foreach (string rawWord in p.Split(' '))
                {
                    string w = rawWord;
                    if (string.IsNullOrEmpty(w))
                        continue;
                    while (w.Length > maxChars)
                    {
                        if (!string.IsNullOrEmpty(curW))
                        {
                            atoms.Add(curW);
                            curW = "";
                        }
                        atoms.Add(w[..maxChars]);
                        w = w[maxChars..];
                    }

                    string cand = string.IsNullOrEmpty(curW) ? w : $"{curW} {w}".Trim();
                    if (cand.Length <= maxChars)
                    {
                        curW = cand;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(curW))
                            atoms.Add(curW);
                        curW = w;
                    }
                }
                if (!string.IsNullOrEmpty(curW))
                    atoms.Add(curW);
            }
        }

        var chunks = new List<string>();
        string current = "";
        foreach (string atom in atoms)
        {
            if (string.IsNullOrEmpty(current))
            {
                current = atom;
                continue;
            }
            char lastChar = current[^1];
            string sep = (lastChar is '.' or '!' or '?' or '。') ? " " : " / ";
            string candidate = $"{current}{sep}{atom}";
            if (candidate.Length <= maxChars)
            {
                current = candidate;
            }
            else
            {
                chunks.Add(current);
                current = atom;
                if (chunks.Count >= maxChunks)
                    break;
            }
        }

        if (!string.IsNullOrEmpty(current) && chunks.Count < maxChunks)
            chunks.Add(current.Length <= maxChars ? current : current[..maxChars]);

        return chunks;
    }
}

/// <summary>
/// Tracks recent bot-sent lines so the bot does not reply to its own messages.
/// </summary>
public sealed class EchoGuard
{
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly int _maxLen;
    private readonly Func<double> _clock;
    private readonly List<(double Timestamp, string NormalizedText)> _recent = new();

    public EchoGuard(int maxLen = 80, Func<double>? clock = null)
    {
        _maxLen = Math.Max(1, maxLen);
        _clock = clock ?? GoalHelpers.MonotonicNow;
    }

    public void Record(string? text)
    {
        string norm = NormalizeText(text);
        if (string.IsNullOrEmpty(norm))
            return;

        lock (_lock)
        {
            _recent.Add((_clock(), norm));
            if (_recent.Count > _maxLen)
                _recent.RemoveAt(0);
        }
    }

    public bool IsRecent(string? text, double ttlSec = 20.0)
    {
        string norm = NormalizeText(text);
        if (string.IsNullOrEmpty(norm))
            return false;

        lock (_lock)
        {
            double now = _clock();
            for (int i = _recent.Count - 1; i >= 0; i--)
            {
                (double ts, string sentNorm) = _recent[i];
                if (now - ts > ttlSec)
                    break;
                if (string.Equals(sentNorm, norm, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    private static string NormalizeText(string? raw)
        => WhitespaceRegex.Replace((raw ?? "").Trim(), " ");
}

/// <summary>
/// Splits long replies, enforces send cooldown spacing, records echo guard, and delivers stickers
/// (1:1 port of <c>ChatOutputSender</c> in <c>mcp_bridge/chat_output.py</c>).
/// </summary>
public sealed class ChatOutputSender
{
    private readonly RpcDispatchAsync _dispatch;
    private readonly EchoGuard _echo;
    private readonly SleepAsync _sleep;
    private readonly Action<string>? _log;

    public ChatOutputSender(
        RpcDispatchAsync dispatchRpc,
        EchoGuard echoGuard,
        SleepAsync? sleepFn = null,
        Action<string>? log = null)
    {
        _dispatch = dispatchRpc ?? throw new ArgumentNullException(nameof(dispatchRpc));
        _echo = echoGuard ?? throw new ArgumentNullException(nameof(echoGuard));
        _sleep = sleepFn ?? BridgeRuntimeConfig.DefaultSleepAsync;
        _log = log;
    }

    public Task<JsonObject> SendNoticeAsync(
        string channel,
        string text,
        long targetCharId,
        string? connId = null,
        CancellationToken ct = default,
        bool asAdmin = false)
    {
        _echo.Record(text);
        var args = new JsonObject
        {
            ["channel"] = channel,
            ["text"] = text,
            ["target_char_id"] = targetCharId,
            ["as_admin"] = asAdmin,
        };
        return _dispatch("send_chat", args, connId, ct);
    }

    public async Task<(List<string> Chunks, JsonObject? StickerResult)> SendReplyAndStickerAsync(
        string channel,
        long senderId,
        string replyText,
        JsonObject? stickerReq,
        string? connId = null,
        CancellationToken ct = default,
        bool asAdmin = false)
    {
        List<string> chunks = ChatTextChunker.SplitChatChunks(
            replyText,
            BridgeConstants.MaxChatChunkChars,
            BridgeConstants.MaxChunksPerReply);

        double prevCooldown = 0.0;
        bool anyTextSent = false;
        for (int idx = 0; idx < chunks.Count; idx++)
        {
            if (idx > 0)
                await _sleep(Math.Max(1.25, prevCooldown + 0.25), ct).ConfigureAwait(false);

            JsonObject sendRes = await SendNoticeAsync(channel, chunks[idx], senderId, connId, ct, asAdmin).ConfigureAwait(false);
            if (!JsonNodeHelpers.GetBool(sendRes, "ok", false))
            {
                _log?.Invoke($"Chunk {idx + 1}/{chunks.Count} send_chat blocked/failed: {JsonNodeHelpers.ToCompactJson(sendRes)}");
                break;
            }
            anyTextSent = true;
            prevCooldown = JsonNodeHelpers.GetDouble(sendRes, "remaining_cooldown_sec", 0.0);
        }

        JsonObject? stickerSentRes = null;
        string stickerTag = JsonNodeHelpers.GetString(stickerReq, "sticker", "");
        int stickerId = JsonNodeHelpers.GetInt(stickerReq, "sticker_id", 0);
        if (stickerReq != null && (!string.IsNullOrEmpty(stickerTag) || stickerId > 0))
        {
            if (anyTextSent)
                await _sleep(Math.Max(1.25, prevCooldown + 0.25), ct).ConfigureAwait(false);

            var sParams = new JsonObject
            {
                ["channel"] = channel,
                ["sticker"] = !string.IsNullOrEmpty(stickerTag) ? stickerTag : "random",
                ["target_char_id"] = senderId,
                ["as_admin"] = asAdmin,
            };
            if (stickerId > 0)
                sParams["sticker_id"] = stickerId;

            stickerSentRes = await _dispatch("send_sticker", sParams, connId, ct).ConfigureAwait(false);
            if (!JsonNodeHelpers.GetBool(stickerSentRes, "ok", false))
                _log?.Invoke($"send_sticker blocked/failed: {JsonNodeHelpers.ToCompactJson(stickerSentRes)}");
        }

        return (chunks, stickerSentRes);
    }
}

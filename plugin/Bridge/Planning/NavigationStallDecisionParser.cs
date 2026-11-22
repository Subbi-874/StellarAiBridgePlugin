using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Stellar.AgyBridge.Bridge.Core;

namespace Stellar.AgyBridge.Bridge.Planning;

/// <summary>
/// Parsed high-level navigation stall recovery decision returned by the LLM.
/// </summary>
public sealed class NavigationStallDecision
{
    public string Action { get; init; } = "";
    public int TpId { get; init; }
    public string Destination { get; init; } = "";
    public string Gimmick { get; init; } = "";
    public long Uuid { get; init; }
    public string WaypointId { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public bool HasCoords { get; init; }
    public string ReplyText { get; init; } = "";
    public string Reason { get; init; } = "";

    public JsonObject ToRpcParams(string stallId)
    {
        var obj = new JsonObject
        {
            ["stall_id"] = stallId,
            ["action"] = Action,
            ["reason"] = Reason,
        };
        if (TpId > 0)
            obj["tp_id"] = TpId;
        if (!string.IsNullOrEmpty(Destination))
            obj["destination"] = Destination;
        if (!string.IsNullOrEmpty(Gimmick))
            obj["gimmick"] = Gimmick;
        if (Uuid > 0)
            obj["uuid"] = Uuid;
        if (!string.IsNullOrEmpty(WaypointId))
            obj["waypoint_id"] = WaypointId;
        if (HasCoords)
        {
            obj["x"] = X;
            obj["y"] = Y;
            obj["z"] = Z;
        }
        return obj;
    }
}

/// <summary>
/// Parses LLM responses for <c>navigation_stalled</c> into a validated
/// <see cref="NavigationStallDecision"/>. Supports both the dedicated consult JSON schema
/// (<c>warp_portal</c>, <c>use_gimmick</c>, <c>detour_waypoint</c>, <c>skip_quest</c>, <c>abort</c>)
/// and the remote-agent <c>response_schema.json</c> <c>actions</c> array format.
/// </summary>
public static class NavigationStallDecisionParser
{
    private static readonly IReadOnlySet<string> AllowedActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "warp_portal",
        "use_gimmick",
        "detour_waypoint",
        "skip_quest",
        "abort",
        "fallback",
    };

    public static NavigationStallDecision? Parse(string? raw, JsonObject? stallPayload = null)
    {
        JsonObject? obj = ConsultDecisionParser.UnwrapEnvelopeObject(raw, "action", "actions");
        if (obj == null)
            return null;

        string replyText = JsonNodeHelpers.GetString(obj, "reply_text", "").Trim();
        string reason = JsonNodeHelpers.GetString(obj, "reason", "").Trim();
        string rawAction = JsonNodeHelpers.GetString(obj, "action", "").Trim().ToLowerInvariant();

        if (!string.IsNullOrEmpty(rawAction))
        {
            if (!AllowedActions.Contains(rawAction))
                return !string.IsNullOrEmpty(replyText) ? new NavigationStallDecision { Action = "abort", ReplyText = replyText, Reason = reason } : null;

            return BuildDirectDecision(rawAction, obj, stallPayload, replyText, reason);
        }

        JsonArray? actions = JsonNodeHelpers.GetArray(obj, "actions");
        if (actions != null)
        {
            foreach (JsonNode? node in actions)
            {
                if (node is not JsonObject act)
                    continue;

                NavigationStallDecision? mapped = MapSchemaAction(act, stallPayload, replyText, reason);
                if (mapped != null)
                    return mapped;
            }

            if (!string.IsNullOrEmpty(replyText))
                return new NavigationStallDecision { Action = "abort", ReplyText = replyText, Reason = !string.IsNullOrEmpty(reason) ? reason : "remote_no_action_abort" };
        }

        return null;
    }

    private static NavigationStallDecision BuildDirectDecision(
        string action,
        JsonObject obj,
        JsonObject? stallPayload,
        string replyText,
        string reason)
    {
        int tpId = JsonNodeHelpers.GetInt(obj, "tp_id", 0);
        string dest = JsonNodeHelpers.GetFirstString(obj, "destination", "place", "target", "name").Trim();
        if (tpId <= 0 && int.TryParse(dest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTp) && parsedTp > 0)
            tpId = parsedTp;

        long uuid = JsonNodeHelpers.GetLong(obj, "uuid", 0L);
        string gimmick = JsonNodeHelpers.GetFirstString(obj, "gimmick", "gimmick_name").Trim();
        string wpId = JsonNodeHelpers.GetFirstString(obj, "waypoint_id", "id").Trim();

        double x = JsonNodeHelpers.GetDouble(obj, "x", double.NaN);
        double y = JsonNodeHelpers.GetDouble(obj, "y", double.NaN);
        double z = JsonNodeHelpers.GetDouble(obj, "z", double.NaN);
        bool hasCoords = double.IsFinite(x) && double.IsFinite(z) && (Math.Abs(x) > 0.01 || Math.Abs(z) > 0.01);

        if (action == "detour_waypoint" && !hasCoords && !string.IsNullOrEmpty(wpId))
            hasCoords = TryLookupWaypointCoords(stallPayload, wpId, out x, out y, out z);

        return new NavigationStallDecision
        {
            Action = action,
            TpId = tpId,
            Destination = dest,
            Gimmick = gimmick,
            Uuid = uuid,
            WaypointId = wpId,
            X = hasCoords ? x : 0.0,
            Y = hasCoords && double.IsFinite(y) ? y : 0.0,
            Z = hasCoords ? z : 0.0,
            HasCoords = hasCoords,
            ReplyText = replyText,
            Reason = reason,
        };
    }

    private static NavigationStallDecision? MapSchemaAction(
        JsonObject act,
        JsonObject? stallPayload,
        string replyText,
        string reason)
    {
        string type = JsonNodeHelpers.GetString(act, "type", "").Trim().ToLowerInvariant();
        if (type == "teleport_to_place")
        {
            string dest = JsonNodeHelpers.GetFirstString(act, "destination", "place", "place_name", "warp", "target", "name", "query").Trim();
            int tpId = JsonNodeHelpers.GetInt(act, "tp_id", 0);

            if (dest.StartsWith("waypoint:", StringComparison.OrdinalIgnoreCase))
            {
                string wpId = dest["waypoint:".Length..].Trim();
                bool found = TryLookupWaypointCoords(stallPayload, wpId, out double x, out double y, out double z);
                return new NavigationStallDecision
                {
                    Action = "detour_waypoint",
                    WaypointId = wpId,
                    X = x,
                    Y = y,
                    Z = z,
                    HasCoords = found,
                    ReplyText = replyText,
                    Reason = reason,
                };
            }

            if (!string.IsNullOrEmpty(dest) && TryLookupWaypointCoords(stallPayload, dest, out double wx, out double wy, out double wz))
            {
                return new NavigationStallDecision
                {
                    Action = "detour_waypoint",
                    WaypointId = dest,
                    X = wx,
                    Y = wy,
                    Z = wz,
                    HasCoords = true,
                    ReplyText = replyText,
                    Reason = reason,
                };
            }

            if (tpId <= 0 && int.TryParse(dest, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedTp) && parsedTp > 0)
                tpId = parsedTp;

            return new NavigationStallDecision
            {
                Action = "warp_portal",
                TpId = tpId,
                Destination = dest,
                ReplyText = replyText,
                Reason = reason,
            };
        }

        if (type == "use_gimmick")
        {
            string gimmick = JsonNodeHelpers.GetFirstString(act, "gimmick", "gimmick_name", "destination", "name", "query").Trim();
            long uuid = JsonNodeHelpers.GetLong(act, "uuid", 0L);
            if (uuid <= 0 && long.TryParse(gimmick, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedUuid) && parsedUuid > 0)
                uuid = parsedUuid;

            return new NavigationStallDecision
            {
                Action = "use_gimmick",
                Uuid = uuid,
                Gimmick = gimmick,
                ReplyText = replyText,
                Reason = reason,
            };
        }

        if (type == "auto_quest")
        {
            string q = JsonNodeHelpers.GetFirstString(act, "quest", "query", "name").Trim();
            if (string.Equals(q, "skip_quest", StringComparison.OrdinalIgnoreCase))
            {
                return new NavigationStallDecision
                {
                    Action = "skip_quest",
                    ReplyText = replyText,
                    Reason = reason,
                };
            }
        }

        if (type is "stop" or "cancel_goal")
        {
            return new NavigationStallDecision
            {
                Action = "abort",
                ReplyText = replyText,
                Reason = reason,
            };
        }

        return null;
    }

    private static bool TryLookupWaypointCoords(
        JsonObject? stallPayload,
        string waypointId,
        out double x,
        out double y,
        out double z)
    {
        x = y = z = 0.0;
        JsonArray? wps = JsonNodeHelpers.GetArray(stallPayload, "detour_waypoints");
        if (wps == null || string.IsNullOrWhiteSpace(waypointId))
            return false;

        foreach (JsonNode? n in wps)
        {
            if (n is not JsonObject wp)
                continue;
            string id = JsonNodeHelpers.GetString(wp, "id", "").Trim();
            if (!string.Equals(id, waypointId, StringComparison.OrdinalIgnoreCase))
                continue;

            x = JsonNodeHelpers.GetDouble(wp, "x", 0.0);
            y = JsonNodeHelpers.GetDouble(wp, "y", 0.0);
            z = JsonNodeHelpers.GetDouble(wp, "z", 0.0);
            return Math.Abs(x) > 0.01 || Math.Abs(z) > 0.01;
        }
        return false;
    }
}

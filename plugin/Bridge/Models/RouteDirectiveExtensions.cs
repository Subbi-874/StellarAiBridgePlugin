using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Bridge.Models;

public sealed partial class RouteDirective
{
    public JsonObject? EventContext { get; set; }
    public bool FactAlreadyRendered { get; set; }
}

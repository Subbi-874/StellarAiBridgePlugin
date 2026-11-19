using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Stellar.AgyBridge.Adapters.Items;

/// <summary>
/// Domain adapter encapsulating bag item/vitality counting, multi-system recipe queries/export
/// (LifeProfession Build/Alchemy/Cooking, Housing Furniture, EquipCreate), and crafting coroutine dispatch.
/// </summary>
public interface IItemAdapter : ICapabilityProbe
{
    bool IsLuaReady { get; }
    string GetGameVersion();
    bool IsInHomelandScene();
    long ReadItemCount(int itemId);
    JsonObject GetItemCount(int itemId, string? query = null, IReadOnlyList<int>? itemIds = null);
    JsonObject GetRecipe(int recipeId = 0, string? query = null);
    JsonObject ExportRecipeCatalog(int offset = 0, int limit = 200, bool refresh = false);
    string? StartCraftCoroutine(int recipeId, string recipeKind, int count);
}

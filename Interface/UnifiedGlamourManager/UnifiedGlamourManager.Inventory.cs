using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private HashSet<uint> ownedEquipment = [];
    private bool          ownedEquipmentComplete;
    private long          nextOwnedEquipmentRefresh;

    private readonly Dictionary<InventoryType, HashSet<uint>> ownedInventoryItems = [];

    private static readonly InventoryType[] OwnedEquipmentContainers =
        [.. Inventories.PlayerWithArmory.Where(x => x != InventoryType.ArmoryWaist)];

    private static readonly Lazy<Dictionary<uint, uint[]>> NativeGlamourSetItems = new
    (() => LuminaGetter.Get<MirageStoreSetItem>().ToDictionary
     (
         x => x.RowId,
         x => new[]
         {
             x.MainHand.RowId, x.OffHand.RowId, x.Head.RowId, x.Body.RowId, x.Hands.RowId,
             x.Legs.RowId, x.Feet.RowId, x.Earrings.RowId, x.Necklace.RowId, x.Bracelets.RowId, x.Ring.RowId
         }
     )
    );

    private HashSet<uint> GetOwnedEquipment
    (
        out bool complete
    )
    {
        if (Environment.TickCount64 < nextOwnedEquipmentRefresh)
        {
            complete = ownedEquipmentComplete;
            return ownedEquipment;
        }

        if (!IClientState.Instance().IsLoggedIn || ICondition.Instance().IsBetweenAreas)
        {
            complete = ownedEquipmentComplete;
            return ownedEquipment;
        }

        var manager = InventoryManager.Instance();
        if (manager != null)
        {
            foreach (var type in OwnedEquipmentContainers)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded)
                    continue;

                HashSet<uint> inventoryItems = [];
                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId == 0)
                        continue;

                    inventoryItems.Add(item->GetBaseItemId());
                }

                ownedInventoryItems[type] = inventoryItems;
            }
        }

        HashSet<uint> owned           = [];
        var           storageComplete = AddNativeStorageOwnership
        (
            ItemFinderModule.Instance(),
            LocalPlayerState.ContentID,
            NativeGlamourSetItems.Value,
            NativeCabinetItems.Value,
            owned
        );
        GetCachedOwnedEquipment(owned, storageComplete, out complete);
        return CacheOwnedEquipment(owned, complete);
    }

    private HashSet<uint> GetCachedOwnedEquipment
    (
        HashSet<uint> owned,
        bool          storageComplete,
        out bool      complete
    )
    {
        foreach (var inventory in ownedInventoryItems.Values)
            owned.UnionWith(inventory);

        complete = storageComplete &&
                   ownedInventoryItems.Count == OwnedEquipmentContainers.Length;
        return owned;
    }

    private static bool AddNativeStorageOwnership
    (
        ItemFinderModule*        finder,
        ulong                    contentID,
        Dictionary<uint, uint[]> sets,
        Dictionary<uint, uint>   cabinetItems,
        HashSet<uint>            owned
    )
    {
        var dresserItems = ReadNativePrismBoxItems(finder, contentID);
        var cabinet      = ReadNativeCabinetItems(finder, contentID, cabinetItems);
        if (dresserItems == null || cabinet == null)
            return false;

        foreach (var (rawItemID, _, missingSlots) in dresserItems)
        {
            var itemID = ItemUtil.GetBaseId(rawItemID).ItemId;
            if (itemID == 0)
                continue;
            if (!sets.TryGetValue(itemID, out var parts))
            {
                owned.Add(itemID);
                continue;
            }

            for (var slot = 0; slot < parts.Length; slot++)
            {
                if (parts[slot] != 0 && IsNativeSetSlotUnlocked(missingSlots, slot))
                    owned.Add(parts[slot]);
            }
        }

        foreach (var (_, itemID) in cabinet)
            owned.Add(itemID);

        var status = GetNativeStorageStatus(finder, contentID);
        return status.PrismBox && status.Cabinet;
    }

    private void InvalidateOwnedEquipment() => nextOwnedEquipmentRefresh = 0;

    private HashSet<uint> CacheOwnedEquipment
    (
        HashSet<uint> owned,
        bool          complete
    )
    {
        if (!ownedEquipment.SetEquals(owned) || ownedEquipmentComplete != complete)
            presetEditorItemsDirty = true;
        ownedEquipment            = owned;
        ownedEquipmentComplete    = complete;
        nextOwnedEquipmentRefresh = Environment.TickCount64 + 500;
        return owned;
    }
}

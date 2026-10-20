using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Dalamud;
using OmenTools.Info.Game.Data;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private HashSet<uint> ownedEquipment = [];
    private bool          ownedEquipmentComplete;
    private long          nextOwnedEquipmentRefresh;

    private static readonly InventoryType[] OwnedEquipmentContainers =
        [.. Inventories.PlayerWithArmory.Where(x => x != InventoryType.ArmoryWaist)];

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

        if (!IClientState.Instance().IsLoggedIn)
        {
            complete = false;
            return CacheOwnedEquipment([], false);
        }

        var owned = items.Where(x => !x.IsSetContainer &&
                                     (x.InPrismBox || x.InCabinet))
                         .Select(x => x.ItemID)
                         .ToHashSet();
        complete    = prismBoxItems != null && cabinetItems != null && !TaskHelper.IsBusy;
        var manager = InventoryManager.Instance();
        if (manager == null)
        {
            complete = false;
            return CacheOwnedEquipment(owned, complete);
        }

        foreach (var type in OwnedEquipmentContainers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded)
            {
                complete = false;
                continue;
            }

            for (var slot = 0; slot < container->Size; slot++)
            {
                var item = container->GetInventorySlot(slot);
                if (item == null || item->ItemId == 0)
                    continue;

                owned.Add(item->GetBaseItemId());
            }
        }

        return CacheOwnedEquipment(owned, complete);
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

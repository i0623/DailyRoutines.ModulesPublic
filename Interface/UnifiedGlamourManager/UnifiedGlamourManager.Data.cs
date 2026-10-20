using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private readonly List<UnifiedItem> items = [];
    private          bool              isRefreshingItems;

    private List<UnifiedItem> prismBoxItems;
    private List<UnifiedItem> cabinetItems;

    private static bool TryGetLoadedMirageManager
    (
        out MirageManager* manager
    )
    {
        manager = MirageManager.Instance();
        return manager != null && manager->PrismBoxRequested && manager->PrismBoxLoaded;
    }

    private static bool TryGetReadyPlateEditor
    (
        out AgentMiragePrismMiragePlate* agent
    )
    {
        agent = AgentMiragePrismMiragePlate.Instance();
        return agent != null                          &&
               agent->IsAgentActive()                 &&
               agent->Data                    != null &&
               agent->Data->SelectedItemIndex < PlateSlotDefinitions.Length;
    }

    // 数据刷新
    private void StartRefreshAll
    (
        UnifiedItem? reselectItem = null
    )
    {
        if (!IClientState.Instance().IsLoggedIn)
            return;

        var itemToReselect = reselectItem ?? selectedItem;
        TaskHelper.Enqueue
        (() =>
            {
                isRefreshingItems = true;
                try
                {
                    prismBoxItems = LoadPrismBoxItems();
                    cabinetItems  = LoadCabinetItems();
                    items.Clear();
                    items.AddRange(prismBoxItems ?? []);
                    items.AddRange(cabinetItems ?? []);
                    MergeItems();
                    selectedItem = itemToReselect == null ?
                                       null :
                                       items.FirstOrDefault(x => x == itemToReselect);
                }
                catch (Exception ex)
                {
                    DLog.Error("UnifiedGlamourManager refresh failed", ex);
                }
                finally
                {
                    isRefreshingItems = false;
                    InvalidateOwnedEquipment();
                    presetEditorItemsDirty = true;
                    MarkFilteredItemsDirty(true, true);
                }
            }
        );
    }

    // 投影台/收藏柜数据读取
    private List<UnifiedItem> LoadPrismBoxItems()
    {
        if (!TryGetLoadedMirageManager(out var manager)) return prismBoxItems;

        List<UnifiedItem> result = [];

        var itemSheet = LuminaGetter.Get<ItemSheet>();

        for (var i = 0U; i < PRISM_BOX_CAPACITY; i++)
        {
            var rawItemID = manager->PrismBoxItemIds[(int)i];
            if (rawItemID == 0) continue;

            var itemID  = ItemUtil.GetBaseId(rawItemID).ItemId;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            AddStoredItem
            (
                result,
                itemRow.Value,
                name,
                ItemSource.PrismBox,
                itemID,
                rawItemID,
                i,
                0,
                manager->PrismBoxStain0Ids[(int)i],
                manager->PrismBoxStain1Ids[(int)i],
                manager
            );

        }

        return result;
    }

    private List<UnifiedItem> LoadCabinetItems()
    {
        var cabinet = GetLoadedCabinet();
        if (cabinet == null) return cabinetItems;

        List<UnifiedItem> result = [];

        var itemSheet = LuminaGetter.Get<ItemSheet>();

        foreach (var cabinetRow in LuminaGetter.Get<CabinetSheet>())
        {
            var cabinetID = cabinetRow.RowId;
            var itemID    = cabinetRow.Item.RowId;
            if (itemID == 0 || !cabinet->IsItemInCabinet(cabinetID)) continue;

            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            AddStoredItem(result, itemRow.Value, name, ItemSource.Cabinet, itemID, itemID, 0, cabinetID);
        }

        return result;
    }

    private void AddStoredItem
    (
        List<UnifiedItem> target,
        ItemSheet         item,
        string            name,
        ItemSource        source,
        uint              itemID,
        uint              rawItemID,
        uint              prismBoxIndex,
        uint              cabinetID,
        uint              stain0ID = 0,
        uint              stain1ID = 0,
        MirageManager*    manager  = null
    )
    {
        var setParts = GetSetParts(itemID);

        target.Add
        (
            UnifiedItem.Create
            (
                itemID,
                rawItemID,
                name,
                item,
                source,
                prismBoxIndex,
                cabinetID,
                stain0ID,
                stain1ID,
                setParts.Count > 0
            )
        );

        foreach (var setPart in setParts)
        {
            if (source != ItemSource.PrismBox || (manager != null && manager->IsSetSlotUnlocked(prismBoxIndex, setPart.SlotIndex)))
                AddSetPartItem(target, setPart, rawItemID, prismBoxIndex, cabinetID, itemID, name, source);
        }
    }

    private static GameCabinet* GetLoadedCabinet()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return null;

        var cabinet = &uiState->Cabinet;
        return cabinet->IsCabinetLoaded() ?
                   cabinet :
                   null;
    }

    private static bool TryGetItemName
    (
        ItemSheet  item,
        out string name
    )
    {
        name = item.Name.ExtractText();
        return !string.IsNullOrWhiteSpace(name);
    }

    private static List<PlateItemInfo> GetCurrentPlateItems()
    {
        if (!TryGetReadyPlateEditor(out var agent)) return [];

        List<PlateItemInfo> items = [];

        var currentItems = agent->Data->CurrentItems;

        for (var i = 0; i < currentItems.Length; i++)
        {
            var item = currentItems[i];
            if (item.ItemId == 0) continue;

            items.Add
            (
                new
                (
                    (uint)i,
                    item.ItemId,
                    item.StainIds[0],
                    item.StainIds[1]
                )
            );
        }

        return items;
    }

    private static List<PlateItemInfo> GetInspectPlateItems()
    {
        if (!InventoryType.Examine.TryGetItems
            (
                x => x.ItemId != 0 && PlateSlots.Contains(x.Slot),
                out var inspectItems
            ))
            return [];

        List<PlateItemInfo> items = [];

        foreach (var item in inspectItems)
        {
            var itemID = item.GlamourId != 0 ?
                             item.GlamourId :
                             item.ItemId;
            var plateSlot = (uint)Array.IndexOf(PlateSlots, item.Slot);
            if (itemID == 0) continue;

            items.Add
            (
                new
                (
                    plateSlot,
                    itemID,
                    item.Stains[0],
                    item.Stains[1]
                )
            );
        }

        return items;
    }

    private static string GetRaceName
    (
        byte race,
        byte sex
    ) =>
        !LuminaGetter.TryGetRow(race, out Race raceRow) ? Lang.Get("Unknown")
        : sex == FEMALE_SEX                             ? raceRow.Feminine.ToString()    ?? Lang.Get("Unknown")
                                                          : raceRow.Masculine.ToString() ?? Lang.Get("Unknown");

    private static string GetSexName
    (
        byte sex
    ) =>
        sex == FEMALE_SEX ?
            LuminaWrapper.GetAddonText(15609) :
            LuminaWrapper.GetAddonText(15608);

    private static string GetDefaultPresetTitle
    (
        PresetSource source
    ) =>
        source switch
        {
            PresetSource.Self        => $"{Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset")}-{LuminaWrapper.GetAddonText(3991)}",
            PresetSource.OtherPlayer => $"{Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset")}-{LuminaWrapper.GetAddonText(7979)}",
            _                        => Lang.Get("UnifiedGlamourManager-Preset-UntitledPreset")
        };

    private void ResetStoredItemsSession()
    {
        TaskHelper?.Abort();
        InvalidatePresetPreview();
        isRefreshingItems = false;
        prismBoxItems     = null;
        cabinetItems      = null;
        items.Clear();
        filteredItems.Clear();
        selectedItem = null;
        ownedEquipment.Clear();
        ownedEquipmentComplete = false;
        InvalidateOwnedEquipment();
        presetEditorItemsDirty = true;
        MarkFilteredItemsDirty(true, true);
    }

    private void OnStoredItemsLogout
    (
        int code,
        int type
    ) => ResetStoredItemsSession();
}

using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;
using GameCabinet = FFXIVClientStructs.FFXIV.Client.Game.UI.Cabinet;
using ItemSheet = Lumina.Excel.Sheets.Item;
using static FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentMiragePrismMiragePlateData;

namespace DailyRoutines.ModulesPublic.Interface.UnifiedGlamourManager;

public unsafe partial class UnifiedGlamourManager
{
    private readonly List<UnifiedItem> items = [];
    private          bool              isRefreshingItems;

    private readonly Dictionary<uint, (uint ItemID, uint Stain0ID, uint Stain1ID)> prismBoxStains = [];

    private static readonly Lazy<Dictionary<uint, uint>> NativeCabinetItems = new
    (() => LuminaGetter.Get<Lumina.Excel.Sheets.Cabinet>()
                       .Where(x => x.Item.RowId != 0)
                       .ToDictionary(x => x.RowId, x => x.Item.RowId)
    );

    private static bool IsNativeStorageOwner
    (
        ItemFinderModule* finder,
        ulong             contentID
    ) =>
        finder != null && contentID != 0 && finder->CharacterContentId == contentID;

    private static (bool PrismBox, bool Cabinet) GetNativeStorageStatus
    (
        ItemFinderModule* finder,
        ulong             contentID
    ) =>
        !IsNativeStorageOwner(finder, contentID) ?
            (false, false) :
            (finder->IsGlamourDresserCached, finder->CabinetState == (byte)GameCabinet.CabinetState.Loaded);

    private static bool SyncNativeCabinetCache
    (
        ItemFinderModule*  finder,
        ulong              contentID,
        ReadOnlySpan<uint> unlockBits
    )
    {
        if (!IsNativeStorageOwner(finder, contentID) || unlockBits.Length != finder->CabinetItemUnlockBits.Length)
            return false;

        if (finder->CabinetState == (byte)GameCabinet.CabinetState.Loaded && unlockBits.SequenceEqual(finder->CabinetItemUnlockBits))
            return false;

        unlockBits.CopyTo(finder->CabinetItemUnlockBits);
        finder->CabinetState = (byte)GameCabinet.CabinetState.Loaded;
        finder->HasChanges   = true;
        return true;
    }

    private static bool RefreshNativeCabinetCache()
    {
        if (!IClientState.Instance().IsLoggedIn || ICondition.Instance().IsBetweenAreas)
            return false;

        var finder    = ItemFinderModule.Instance();
        var contentID = LocalPlayerState.ContentID;
        if (!IsNativeStorageOwner(finder, contentID))
            return false;

        var cabinet = GetLoadedCabinet();
        if (cabinet == null)
            return false;

        Span<uint> unlockBits = stackalloc uint[finder->CabinetItemUnlockBits.Length];
        unlockBits.Clear();
        foreach (var cabinetID in NativeCabinetItems.Value.Keys)
        {
            var word = cabinetID / 32;
            if (word < unlockBits.Length && cabinet->IsItemInCabinet(cabinetID))
                unlockBits[(int)word] |= 1u << (int)(cabinetID % 32);
        }

        return SyncNativeCabinetCache(finder, contentID, unlockBits);
    }

    private static List<(uint RawItemID, uint Index, ushort? MissingSlots)> ReadNativePrismBoxItems
    (
        ItemFinderModule* finder,
        ulong             contentID
    )
    {
        if (!IsNativeStorageOwner(finder, contentID))
            return null;

        List<(uint RawItemID, uint Index, ushort? MissingSlots)> result = [];
        for (var index = 0; index < finder->GlamourDresserItemIds.Length; index++)
        {
            var rawItemID = finder->GlamourDresserItemIds[index];
            if (rawItemID != 0)
            {
                ushort? missingSlots = finder->IsGlamourDresserCached ?
                                           finder->GlamourDresserItemSetUnlockBits[index] :
                                           null;
                result.Add((rawItemID, (uint)index, missingSlots));
            }
        }

        return result;
    }

    private static List<(uint CabinetID, uint ItemID)> ReadNativeCabinetItems
    (
        ItemFinderModule*      finder,
        ulong                  contentID,
        Dictionary<uint, uint> cabinetItems
    )
    {
        if (!IsNativeStorageOwner(finder, contentID))
            return null;

        List<(uint CabinetID, uint ItemID)> result = [];

        var bits = finder->CabinetItemUnlockBits;
        foreach (var (cabinetID, itemID) in cabinetItems)
        {
            var word = cabinetID / 32;
            if (word < bits.Length && (bits[(int)word] & (1u << (int)(cabinetID % 32))) != 0)
                result.Add((cabinetID, itemID));
        }

        return result;
    }

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
        if (!IClientState.Instance().IsLoggedIn || ICondition.Instance().IsBetweenAreas)
            return;

        var itemToReselect = reselectItem ?? selectedItem;
        TaskHelper.Enqueue
        (() =>
            {
                isRefreshingItems = true;
                try
                {
                    if (!IClientState.Instance().IsLoggedIn ||
                        ICondition.Instance().IsBetweenAreas ||
                        !IsNativeStorageOwner(ItemFinderModule.Instance(), LocalPlayerState.ContentID))
                    {
                        return;
                    }

                    RefreshNativeCabinetCache();
                    items.Clear();
                    items.AddRange(LoadPrismBoxItems());
                    items.AddRange(LoadCabinetItems());
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
        var finder    = ItemFinderModule.Instance();
        var contentID = LocalPlayerState.ContentID;
        var cached    = ReadNativePrismBoxItems(finder, contentID);
        if (cached == null)
            return [];

        var liveLoaded = TryGetLoadedMirageManager(out var manager);

        if (GetNativeStorageStatus(finder, contentID).PrismBox)
        {
            foreach (var index in prismBoxStains.Keys.ToArray())
            {
                if (index >= finder->GlamourDresserItemIds.Length || finder->GlamourDresserItemIds[(int)index] == 0)
                    prismBoxStains.Remove(index);
            }
        }

        List<UnifiedItem> result = [];

        var itemSheet = LuminaGetter.Get<ItemSheet>();

        foreach (var (rawItemID, index, missingSlots) in cached)
        {
            var itemID  = ItemUtil.GetBaseId(rawItemID).ItemId;
            var itemRow = itemSheet.GetRowOrDefault(itemID);
            if (itemRow == null || !TryGetItemName(itemRow.Value, out var name))
                continue;

            var stain0 = 0U;
            var stain1 = 0U;
            if (liveLoaded && ItemUtil.GetBaseId(manager->PrismBoxItemIds[(int)index]).ItemId == itemID)
            {
                stain0 = manager->PrismBoxStain0Ids[(int)index];
                stain1 = manager->PrismBoxStain1Ids[(int)index];
            }
            else if (prismBoxStains.TryGetValue(index, out var old) && old.ItemID == itemID)
            {
                stain0 = old.Stain0ID;
                stain1 = old.Stain1ID;
            }

            prismBoxStains[index] = (itemID, stain0, stain1);

            AddStoredItem
            (
                result,
                itemRow.Value,
                name,
                ItemSource.PrismBox,
                itemID,
                rawItemID,
                index,
                0,
                stain0,
                stain1,
                missingSlots
            );
        }

        return result;
    }

    private List<UnifiedItem> LoadCabinetItems()
    {
        var finder    = ItemFinderModule.Instance();
        var contentID = LocalPlayerState.ContentID;
        var cached    = ReadNativeCabinetItems(finder, contentID, NativeCabinetItems.Value);
        if (cached == null)
            return [];

        List<UnifiedItem> result = [];

        var itemSheet = LuminaGetter.Get<ItemSheet>();

        foreach (var (cabinetID, itemID) in cached)
        {
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
        uint              stain0ID     = 0,
        uint              stain1ID     = 0,
        ushort?           missingSlots = 0
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
            if (IsNativeSetSlotUnlocked(missingSlots, setPart.SlotIndex))
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
        openItemSearchMenu = false;
        itemSearchOptions  = [];
        isRefreshingItems  = false;
        prismBoxStains.Clear();
        ResetGlamaholicImport();
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

using System.Collections.Frozen;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Info.Game.Data;
using OmenTools.Interop.Game.Lumina;

namespace DailyRoutines.ModulesPublic;

internal unsafe class AutoRetrieveCabinetGlamours : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title       = Lang.Get("AutoRetrieveCabinetGlamoursTitle"),
        Description = Lang.Get("AutoRetrieveCabinetGlamoursDescription"),
        Category    = ModuleCategory.Interface
    };

    public override string ModuleName => nameof(AutoRetrieveCabinetGlamours);

    private uint   session;
    private ulong  contentID;
    private int    restoredCount;
    private int    skippedCount;
    private string status = string.Empty;

    protected override void Init()
    {
        TaskHelper = new() { TimeoutMS = 10_000 };

        GameState.Instance().Login  += Stop;
        GameState.Instance().Logout += Stop;
        IAddonLifecycle.Instance().RegisterListener(AddonEvent.PreFinalize, "MiragePrismPrismBox", OnDresserClosed);
    }

    protected override void Uninit()
    {
        Stop();
        GameState.Instance().Login  -= Stop;
        GameState.Instance().Logout -= Stop;
        IAddonLifecycle.Instance().UnregisterListener(OnDresserClosed);
    }

    protected override void ConfigUI()
    {
        ImGui.TextWrapped(Info.Description);

        using (ImRaii.Disabled(TaskHelper.IsBusy || !TryGetDresser(out _)))
        {
            if (ImGui.Button(Lang.Get("AutoRetrieveCabinetGlamours-Start")))
                Start();
        }

        using (ImRaii.Disabled(!TaskHelper.IsBusy))
        {
            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("Stop")))
                Stop();
        }

        if (!string.IsNullOrEmpty(status))
            ImGui.TextWrapped(status);
    }

    private void Start()
    {
        if (TaskHelper.IsBusy || !TryGetDresser(out var manager)) return;

        var entries = GetEntries(manager);
        if (entries.Count == 0)
        {
            status = Lang.Get("AutoRetrieveCabinetGlamours-Empty");
            return;
        }

        var currentSession = ++session;
        contentID     = LocalPlayerState.ContentID;
        restoredCount = 0;
        skippedCount  = 0;
        status        = Lang.Get("AutoRetrieveCabinetGlamours-Running");

        foreach (var entry in entries)
        {
            var requested = false;
            var bagCount  = 0;

            TaskHelper.Enqueue(() =>
            {
                if (!TryGetOperationDresser(currentSession, out var current)) return;
                if (!IsAvailable(current, entry))
                {
                    Skip(entry);
                    return;
                }

                if (!TryGetBagState(entry.ItemID, out var emptySlots, out bagCount))
                {
                    Fail("AutoRetrieveCabinetGlamours-BagUnavailable");
                    return;
                }

                if (emptySlots == 0)
                {
                    Fail("AutoRetrieveCabinetGlamours-BagFull");
                    return;
                }

                requested = Restore(current, entry);
                if (!requested)
                    Skip(entry);
            });

            TaskHelper.Enqueue(
                () =>
                {
                    if (!TryGetOperationDresser(currentSession, out var current)) return true;
                    if (!requested) return true;
                    if (IsAvailable(current, entry)) return false;
                    if (!TryGetBagState(entry.ItemID, out _, out var count) || count <= bagCount) return false;

                    restoredCount++;
                    UpdateProgress();
                    return true;
                },
                timeoutAction: () => Fail("AutoRetrieveCabinetGlamours-Timeout")
            );
        }

        TaskHelper.Enqueue(() =>
        {
            if (!TryGetOperationDresser(currentSession, out _)) return;
            UpdateProgress();
            NotifyHelper.Instance().Chat(status);
        });
    }

    private void Stop()
    {
        session++;
        TaskHelper.Abort();
        contentID = 0;
        status    = Lang.Get("AutoRetrieveCabinetGlamours-Stopped");
    }

    private void Fail(
        string key
    )
    {
        Stop();
        status = Lang.Get(key);
        NotifyHelper.Instance().Chat(status);
    }

    private void Skip(
        RestoreEntry entry
    )
    {
        skippedCount++;
        UpdateProgress();
        var name = LuminaGetter.GetRow<Item>(entry.ItemID)?.Name.ExtractText() ?? entry.ItemID.ToString();
        NotifyHelper.Instance().Chat(Lang.Get("AutoRetrieveCabinetGlamours-Skipped", new Dictionary<string, object> { ["itemName"] = name }));
    }

    private void UpdateProgress() =>
        status = Lang.Get(
            "AutoRetrieveCabinetGlamours-Progress",
            new Dictionary<string, object> { ["count"] = restoredCount, ["skipped"] = skippedCount }
        );

    private void OnDresserClosed(
        AddonEvent type,
        AddonArgs? args
    ) =>
        Stop();

    private bool TryGetOperationDresser(
        uint               expectedSession,
        out MirageManager* manager
    )
    {
        manager = null;
        if (expectedSession != session) return false;
        if (contentID != 0 && contentID == LocalPlayerState.ContentID && TryGetDresser(out manager)) return true;

        Stop();
        return false;
    }

    private static bool TryGetDresser(
        out MirageManager* manager
    )
    {
        manager = null;
        if (!GameState.IsLoggedIn || !MiragePrismPrismBox->IsAddonAndNodesReady()) return false;

        manager = MirageManager.Instance();
        return manager != null && manager->PrismBoxRequested && manager->PrismBoxLoaded;
    }

    private static List<RestoreEntry> GetEntries(
        MirageManager* manager
    )
    {
        List<RestoreEntry> entries = [];

        for (var index = 0; index < manager->PrismBoxItemIds.Length; index++)
        {
            var rawItemID = manager->PrismBoxItemIds[index];
            var itemID    = rawItemID % 1_000_000;
            if (itemID == 0) continue;

            if (SetParts.TryGetValue(itemID, out var parts))
            {
                foreach (var part in parts)
                {
                    if (manager->IsSetSlotUnlocked((uint)index, part.Slot))
                        entries.Add(new((uint)index, rawItemID, part.ItemID, part.Slot));
                }
            }
            else if (CabinetItems.Contains(itemID))
                entries.Add(new((uint)index, rawItemID, itemID, -1));
        }

        return entries;
    }

    private static bool IsAvailable(
        MirageManager* manager,
        RestoreEntry   entry
    ) =>
        manager->PrismBoxItemIds[(int)entry.Index] == entry.RawItemID &&
        (entry.Slot < 0 || manager->IsSetSlotUnlocked(entry.Index, entry.Slot));

    private static bool Restore(
        MirageManager* manager,
        RestoreEntry   entry
    )
    {
        if (entry.Slot < 0)
            return manager->RestorePrismBoxItem(entry.Index);

        var mask = (ushort)(1 << entry.Slot);
        return manager->RestorePrismBoxSetItem(entry.Index, (byte*)&mask);
    }

    private static bool TryGetBagState(
        uint     itemID,
        out int  emptySlots,
        out int  count
    )
    {
        emptySlots = 0;
        count      = 0;
        var inventory = InventoryManager.Instance();
        if (inventory == null) return false;

        foreach (var type in Inventories.Player)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) return false;

            for (var index = 0; index < container->Size; index++)
            {
                var item = container->GetInventorySlot(index);
                if (item == null) return false;
                if (item->ItemId == 0)
                    emptySlots++;
                else if (item->GetBaseItemId() == itemID)
                    count += item->Quantity;
            }
        }

        return true;
    }

    private static List<SetPart> GetCabinetParts(
        MirageStoreSetItem row
    )
    {
        uint[] items =
        [
            row.MainHand.RowId, row.OffHand.RowId, row.Head.RowId, row.Body.RowId,
            row.Hands.RowId, row.Legs.RowId, row.Feet.RowId, row.Earrings.RowId,
            row.Necklace.RowId, row.Bracelets.RowId, row.Ring.RowId
        ];

        List<SetPart> parts = [];
        for (var slot = 0; slot < items.Length; slot++)
        {
            if (items[slot] > 1 && CabinetItems.Contains(items[slot]))
                parts.Add(new(items[slot], slot));
        }

        return parts;
    }

    private readonly record struct RestoreEntry(uint Index, uint RawItemID, uint ItemID, int Slot);
    private readonly record struct SetPart(uint ItemID, int Slot);

    #region 常量

    private static readonly FrozenSet<uint> CabinetItems =
        LuminaGetter.Get<Cabinet>()
                    .Where(x => x.Item.RowId > 0)
                    .Select(x => x.Item.RowId)
                    .ToFrozenSet();

    private static readonly FrozenDictionary<uint, List<SetPart>> SetParts =
        LuminaGetter.Get<MirageStoreSetItem>()
                    .ToFrozenDictionary(x => x.RowId, GetCabinetParts);

    #endregion
}

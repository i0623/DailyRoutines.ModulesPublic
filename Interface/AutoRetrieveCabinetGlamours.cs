using System.Collections.Frozen;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmenTools.Dalamud;
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
        TaskHelper = new() { TimeoutMS = 10_000, RetryIntervalMS = RESTORE_RETRY_INTERVAL_MS, ShowDebug = true };
        IPluginLog.Instance().Information($"[{ModuleName}] Loaded revision={REVISION}, intervalMS={RESTORE_INTERVAL_MS}, maxAttempts={RESTORE_MAX_ATTEMPTS}");

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
        ImGui.TextDisabled(Lang.Get("AutoRetrieveCabinetGlamours-Version", REVISION));
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
        IPluginLog.Instance().Information($"[{ModuleName}] Start: items={entries.Count}, session={currentSession}");

        foreach (var entry in entries)
        {
            var requested = false;
            var attempts  = 0;

            TaskHelper.Enqueue(() =>
            {
                if (!TryGetOperationDresser(currentSession, out var current)) return true;
                if (!IsAvailable(current, entry))
                {
                    Skip(entry);
                    return true;
                }

                attempts++;
                requested = Restore(current, entry);
                IPluginLog.Instance().Information($"[{ModuleName}] Restore: item={entry.ItemID}, index={entry.Index}, part={entry.Slot}, attempt={attempts}, sent={requested}");
                if (requested) return true;

                if (attempts >= RESTORE_MAX_ATTEMPTS)
                {
                    Skip(entry);
                    return true;
                }

                return false;
            });

            TaskHelper.Enqueue(
                () =>
                {
                    if (!TryGetOperationDresser(currentSession, out var current)) return true;
                    if (!requested) return true;
                    if (IsAvailable(current, entry)) return false;

                    restoredCount++;
                    UpdateProgress();
                    IPluginLog.Instance().Information($"[{ModuleName}] Restored: item={entry.ItemID}, index={entry.Index}, part={entry.Slot}, restored={restoredCount}");
                    return true;
                },
                name: $"{ModuleName}: WaitRestore item={entry.ItemID}",
                timeoutAction: () => Fail("AutoRetrieveCabinetGlamours-Timeout")
            );

            TaskHelper.DelayNext(RESTORE_INTERVAL_MS, ModuleName);
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
        DLog.Warning($"[{ModuleName}] {key}; restored={restoredCount}, skipped={skippedCount}, task={TaskHelper.CurrentTaskName}");
        Stop();
        status = Lang.Get(key);
        NotifyHelper.Instance().Chat(status);
    }

    private void Skip(
        RestoreEntry entry
    )
    {
        skippedCount++;
        DLog.Warning($"[{ModuleName}] Skipped: item={entry.ItemID}, index={entry.Index}, part={entry.Slot}");
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
            var itemID    = ItemUtil.GetBaseId(rawItemID).ItemId;
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

    private const string REVISION                   = "2026.11.14.3";
    private const int    RESTORE_INTERVAL_MS         = 500;
    private const int    RESTORE_RETRY_INTERVAL_MS   = 500;
    private const int    RESTORE_MAX_ATTEMPTS        = 3;

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

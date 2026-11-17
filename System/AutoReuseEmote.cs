using System.Globalization;
using DailyRoutines.Common.Module.Abstractions;
using DailyRoutines.Common.Module.Enums;
using DailyRoutines.Common.Module.Models;
using DailyRoutines.Extensions;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;
using OmenTools.Interop.Game.Lumina;
using OmenTools.Interop.Game.Models;
using OmenTools.KamiToolKit.Addons.InputNumeric;
using OmenTools.OmenService;
using AgentEmote = FFXIVClientStructs.Expansion.FFXIV.Client.UI.Agent.AgentEmote;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoReuseEmote : ModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Title = Lang.Get("AutoReuseEmoteTitle"),
        Description = Lang.Get
        (
            "AutoReuseEmoteDescription",
            new Dictionary<string, object>
            {
                ["command"] = COMMAND
            }
        ),
        Category = ModuleCategory.System,
        Author   = ["Xww"]
    };

    public override string ModuleName => nameof(AutoReuseEmote);

    public override ModuleBehaviour Behaviour { get; } = new()
    {
        AllDefaultEnabled = true
    };

    private Config config;

    private AutoReuseEmoteMenuItem menuItem;

    private uint currentEmoteID;

    protected override void Init()
    {
        TaskHelper = new() { TimeoutMS = 0 };

        config = LoadConfig<Config>() ?? new();

        menuItem = new(this);
        ContextMenuManager.Instance().Reg(menuItem);

        CommandManager.Instance().AddSubCommand
        (
            COMMAND,
            new(OnCommand)
            {
                HelpMessage = Lang.Get
                (
                    "AutoReuseEmote-CommandHelp",
                    new Dictionary<string, object>
                    {
                        ["command"] = COMMAND
                    }
                )
            }
        );
    }

    protected override void Uninit()
    {
        currentEmoteID = 0;
        TaskHelper.Abort();

        ContextMenuManager.Instance().Unreg(menuItem);

        CommandManager.Instance().RemoveSubCommand(COMMAND);
    }

    private void OnCommand
    (
        string command,
        string args
    )
    {
        args = args.Trim();

        if (string.IsNullOrEmpty(args))
        {
            StopReuse();
            return;
        }

        var emoteName             = args;
        var repeatInterval        = config.DefaultInterval;
        var repeatAfterCompletion = false;

        var intervalStartIndex = args.LastIndexOf(' ');

        if (intervalStartIndex > 0 &&
            args[(intervalStartIndex + 1)..].Equals(AUTO_MODE, StringComparison.OrdinalIgnoreCase))
        {
            emoteName             = args[..intervalStartIndex].TrimEnd();
            repeatAfterCompletion = true;
        }
        else if (intervalStartIndex > 0 &&
                 uint.TryParse(args[(intervalStartIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedInterval))
        {
            if (parsedInterval is 0 or > int.MaxValue)
            {
                NotifyHelper.Instance().ChatError
                (
                    ReadOnlySeString.FormatIncorrectCommandParamLogMessage
                    (
                        2,
                        Lang.Get("Interval"),
                        parsedInterval.ToString()
                    )
                );
                return;
            }

            emoteName      = args[..intervalStartIndex].TrimEnd();
            repeatInterval = parsedInterval;
        }

        emoteName = emoteName.Trim('"', '“', '”');

        if (!TryParseEmoteByName(emoteName, out var emoteID))
        {
            NotifyHelper.Instance().ChatError
            (
                ReadOnlySeString.FormatNotFoundCommandParamLogMessage
                (
                    1,
                    Lang.Get("Emote"),
                    emoteName
                )
            );
            return;
        }

        StartReuse(emoteID, repeatInterval, repeatAfterCompletion);
    }

    private static unsafe bool TryParseEmoteByName
    (
        string     name,
        out ushort id
    )
    {
        id = 0;

        var query = name.AsSpan().Trim();
        if (query.IsEmpty) return false;

        foreach (var emote in LuminaGetter.Get<Emote>())
        {
            var textCommand = emote.TextCommand;
            if (textCommand.ValueNullable is null)
                continue;

            var emoteName = emote.Name.ToString().AsSpan().Trim();
            if (emoteName.IsEmpty)
                continue;

            if (!emoteName.Equals(query, StringComparison.OrdinalIgnoreCase))
            {
                var commandName = textCommand.Value.Command.ToString().AsSpan().Trim('/');
                if (!commandName.Equals(query, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            // 情感动作需要解锁
            var unlockLink = emote.UnlockLink;
            if (unlockLink != 0 &&
                !UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(unlockLink))
                return false;

            id = (ushort)emote.RowId;
            return true;
        }

        return false;
    }

    private void StartReuse
    (
        ushort emoteID,
        uint   interval,
        bool   repeatAfterCompletion = false
    )
    {
        TaskHelper.Abort();

        currentEmoteID = emoteID;
        var timelineIDs = repeatAfterCompletion ?
                              LuminaGetter.GetRowOrDefault<Emote>(emoteID).ActionTimeline
                                          .Select(x => (ushort)x.RowId).Where(x => x != 0).Distinct().ToArray() :
                              [];

        TaskHelper.Enqueue
        (
            () => WaitUntilReusable(emoteID),
            timeoutMS: EMOTE_START_TIMEOUT_MS,
            timeoutAction: () => StopWithError("AutoReuseEmote-Notification-Unavailable")
        );
        TaskHelper.Enqueue
        (() =>
            {
                var message = Lang.Get
                (
                    repeatAfterCompletion ?
                        "AutoReuseEmote-Notification-StartAfterCompletion" :
                        "AutoReuseEmote-Notification-Start",
                    new Dictionary<string, object>
                    {
                        ["ms"]    = interval,
                        ["emote"] = LuminaWrapper.GetEmoteName(emoteID)
                    }
                );
                NotifyHelper.Toast(message);
                NotifyHelper.Instance().Chat(message);
            }
        );
        UseEmoteByID(emoteID, interval, repeatAfterCompletion, timelineIDs);
    }

    private void StopReuse()
    {
        TaskHelper.Abort();
        currentEmoteID = 0;

        var message = Lang.Get("AutoReuseEmote-Notification-Stop");
        NotifyHelper.Instance().Chat(message);
        NotifyHelper.Toast(message);
    }

    private unsafe void UseEmoteByID
    (
        ushort   id,
        uint     interval,
        bool     repeatAfterCompletion,
        ushort[] timelineIDs
    )
    {
        if (currentEmoteID != id)
            return;

        EmotePlayback playback = null;

        TaskHelper.Enqueue
        (() =>
            {
                if (currentEmoteID != id)
                    return;

                if (!CanReuseEmoteNow(id))
                {
                    StopReuse();
                    return;
                }

                if (repeatAfterCompletion)
                {
                    var player = LocalPlayerState.Object.ToStruct();
                    playback = new(id, player->EmoteController.EmoteId, timelineIDs);
                }

                AgentEmote.Instance()->ExecuteEmote(id, null, false, false);
                if (playback != null)
                    WaitForPlayback(id, playback, true);
            }
        );

        if (repeatAfterCompletion)
        {
            TaskHelper.Enqueue
            (
                () => WaitForPlayback(id, playback, true),
                timeoutMS: EMOTE_START_TIMEOUT_MS,
                timeoutAction: () => StopWithError("AutoReuseEmote-Notification-StartNotObserved")
            );
            TaskHelper.Enqueue(() => WaitForPlayback(id, playback, false));
            TaskHelper.Enqueue
            (
                () => WaitUntilReusable(id),
                timeoutMS: EMOTE_START_TIMEOUT_MS,
                timeoutAction: () => StopWithError("AutoReuseEmote-Notification-Unavailable")
            );
        }
        else
            TaskHelper.DelayNext((int)interval);

        TaskHelper.Enqueue
        (() =>
            {
                if (currentEmoteID == id)
                    UseEmoteByID(id, interval, repeatAfterCompletion, timelineIDs);
            }
        );
    }

    private unsafe bool WaitForPlayback(ushort emoteID, EmotePlayback playback, bool waitForStart)
    {
        if (currentEmoteID != emoteID)
            return true;

        if (!CanContinueReuse() || playback == null)
        {
            StopReuse();
            return true;
        }

        var player         = LocalPlayerState.Object.ToStruct();
        var playingEmoteID = player->EmoteController.EmoteId;
        var timelines      = player->Timeline.TimelineSequencer.TimelineIds;

        if (playback.IsInterrupted(playingEmoteID))
        {
            StopReuse();
            return true;
        }

        if (waitForStart)
            return playback.ObserveStart(playingEmoteID, timelines, Environment.TickCount64);

        return playback.HasFinished(playingEmoteID, timelines, Environment.TickCount64);
    }

    private bool WaitUntilReusable(ushort emoteID)
    {
        if (currentEmoteID != emoteID)
            return true;

        if (!CanContinueReuse())
        {
            StopReuse();
            return true;
        }

        return CanReuseEmoteNow(emoteID);
    }

    private void StopWithError(string key)
    {
        NotifyHelper.Instance().ChatError(Lang.Get(key));
        StopReuse();
    }

    private static unsafe bool CanReuseEmoteNow(ushort emoteID)
    {
        if (!CanContinueReuse()) return false;

        var emote = AgentEmote.Instance();
        return emote != null && emote->CanUseEmote(emoteID);
    }

    private static unsafe bool CanContinueReuse()
    {
        if (!GameState.IsLoggedIn                    ||
            LocalPlayerState.Object == null          ||
            ICondition.Instance().IsBetweenAreas     ||
            ICondition.Instance().IsWatchingCutscene ||
            ICondition.Instance()[ConditionFlag.InCombat])
            return false;

        var map = AgentMap.Instance();
        return map != null && !map->IsPlayerMoving;
    }

    private sealed class AutoReuseEmoteMenuItem
    (
        AutoReuseEmote module
    ) : ContextMenuEntry
    {
        public override string Identifier => nameof(AutoReuseEmote);

        public override unsafe IReadOnlyList<ContextMenuItem>? CreateMultiple
        (
            ContextMenuOpenedArgs args
        )
        {
            if (args.AddonName != "Emote")
                return null;

            var addon = args.Addon;
            if (addon == null)
                return null;

            var agent = AgentEmote.Instance();
            if (agent is null)
                return null;

            var emoteID = agent->ContextMenuEmoteID;
            if (emoteID is 0)
                return null;

            List<ContextMenuItem> items = [];

            if (module.currentEmoteID == emoteID)
            {
                items.Add
                (
                    new()
                    {
                        Name      = Lang.Get("AutoReuseEmote-StopReuse"),
                        OnClicked = _ => module.StopReuse()
                    }
                );
            }
            else
            {
                items.Add
                (
                    new()
                    {
                        Name      = Lang.Get("AutoReuseEmote-StartReuse"),
                        OnClicked = _ => module.StartReuse((ushort)emoteID, module.config.DefaultInterval)
                    }
                );
                items.Add
                (
                    new()
                    {
                        Name      = Lang.Get("AutoReuseEmote-StartAfterCompletion"),
                        OnClicked = _ => module.StartReuse((ushort)emoteID, module.config.DefaultInterval, true)
                    }
                );
            }

            items.Add
            (
                new()
                {
                    Name = Lang.Get("AutoReuseEmote-ConfigureDefualtInterval"),
                    OnClicked = _ =>
                    {
                        InputNumericAddon.Open
                        (
                            new()
                            {
                                Prompt          = Lang.Get("AutoReuseEmote-ConfigureDefualtInterval-PleaseInput"),
                                Value           = (int)module.config.DefaultInterval,
                                Min             = 100,
                                Max             = int.MaxValue,
                                Step            = 1000,
                                BlockedParentID = addon->Id,
                                ParentID        = addon->Id,
                                Callback = (_, info) =>
                                {
                                    if (info.Result != InputNumericAddonResult.Confirmed)
                                        return;

                                    module.config.DefaultInterval = (uint)info.Value;
                                    module.config.Save(module);
                                },
                                Position = new
                                (
                                    args.Addon->RootNode->GetNodeState().Center,
                                    AddonPositionAlignment.TopCenter
                                )
                            }
                        );
                    }
                }
            );

            return items;
        }
    }

    private class Config : ModuleConfig
    {
        public uint DefaultInterval = 2000;
    }

    #region 常量

    private const string COMMAND                = "remote";
    private const string AUTO_MODE              = "auto";
    private const int    EMOTE_START_TIMEOUT_MS = 5_000;

    #endregion
}

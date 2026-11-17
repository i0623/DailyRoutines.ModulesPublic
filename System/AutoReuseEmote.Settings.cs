using System.Numerics;
using DailyRoutines.Extensions;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;

namespace DailyRoutines.ModulesPublic;

internal partial class AutoReuseEmote
{
    private EmoteSettingsAddon settingsAddon;

    private unsafe void OpenSettings(AtkUnitBase* parent)
    {
        settingsAddon?.Dispose();
        settingsAddon = new(this, parent->RootNode->GetNodeState().Center)
        {
            InternalName          = $"DR{nameof(AutoReuseEmote)}Settings",
            Title                 = Lang.Get("AutoReuseEmote-ConfigureDefualtInterval"),
            Size                  = new(360f, 244f),
            ParentAddonId         = parent->Id,
            BlockedParentAddonId  = parent->Id,
            RememberClosePosition = false,
            RespectCloseAll       = true
        };
        settingsAddon.Open();
    }

    private sealed class EmoteSettingsAddon
    (
        AutoReuseEmote module,
        Vector2        anchor
    ) : NativeAddon
    {
        protected override unsafe void OnSetup
        (
            AtkUnitBase*   addon,
            Span<AtkValue> atkValues
        )
        {
            var prompt = new TextNode
            {
                Position = ContentStartPosition,
                Size     = new(312f, 24f),
                FontSize = 14,
                String   = Lang.Get("AutoReuseEmote-ConfigureDefualtInterval-PleaseInput")
            };
            prompt.AttachNode(this);

            var intervalInput = new NumericInputNode
            {
                Position  = ContentStartPosition + new Vector2(0f, 32f),
                Size      = new(200f, 28f),
                Min       = 100,
                Max       = int.MaxValue,
                Step      = 1000,
                Value     = (int)Math.Clamp(module.config.DefaultInterval, 100u, int.MaxValue),
                IsEnabled = !module.config.RepeatAfterCompletion
            };
            intervalInput.AttachNode(this);

            var automaticCheckbox = new CheckboxNode
            {
                Position  = ContentStartPosition + new Vector2(214f, 36f),
                Size      = new(98f, 24f),
                String    = Lang.Get("Auto"),
                IsChecked = module.config.RepeatAfterCompletion,
                OnClick   = enabled => intervalInput.IsEnabled = !enabled
            };
            automaticCheckbox.AttachNode(this);

            var help = new TextNode
            {
                Position  = ContentStartPosition + new Vector2(0f, 76f),
                Size      = new(312f, 44f),
                FontSize  = 12,
                TextFlags = TextFlags.MultiLine | TextFlags.WordWrap,
                String    = Lang.Get("AutoReuseEmote-AutoRepeatHelp")
            };
            help.AttachNode(this);

            var confirm = new TextButtonNode
            {
                Position = ContentStartPosition + new Vector2(48f, 140f),
                Size     = new(100f, 28f),
                String   = Lang.Get("Confirm"),
                OnClick = () =>
                {
                    module.config.DefaultInterval       = (uint)intervalInput.Value;
                    module.config.RepeatAfterCompletion = automaticCheckbox.IsChecked;
                    module.config.Save(module);
                    Close();
                }
            };
            confirm.AttachNode(this);

            var cancel = new TextButtonNode
            {
                Position = ContentStartPosition + new Vector2(164f, 140f),
                Size     = new(100f, 28f),
                String   = Lang.Get("Cancel"),
                OnClick  = () => Close()
            };
            cancel.AttachNode(this);
        }

        protected override unsafe void OnSetupComplete(AtkUnitBase* addon)
        {
            Vector2 screen = AtkStage.Instance()->ScreenSize;
            var     size   = new Vector2(addon->GetScaledWidth(true), addon->GetScaledHeight(true));
            var     limit  = Vector2.Max(Vector2.Zero, screen - size);

            SetWindowPosition(Vector2.Clamp(anchor - new Vector2(size.X / 2f, 0f), Vector2.Zero, limit));
        }
    }
}

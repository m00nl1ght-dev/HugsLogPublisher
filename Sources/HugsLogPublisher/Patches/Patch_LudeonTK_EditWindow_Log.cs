using System;
using HarmonyLib;
using HugsLogPublisher.Compatibility;
using LunarFramework.Patching;
using UnityEngine;
using Verse;

#if RW_1_5_OR_GREATER
using LudeonTK;
#endif

namespace HugsLogPublisher.Patches;

[PatchGroup("Main")]
[HarmonyPatch(typeof(EditWindow_Log))]
internal static class Patch_LudeonTK_EditWindow_Log
{
    [HarmonyPrepare]
    private static bool PatchCondition() => LogPublisherEntrypoint.IsStandaloneMod && !ModCompat_HugsLib.IsPresent;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(EditWindow_Log.DoWindowContents))]
    private static void DoWindowContents_Postfix(Rect inRect)
    {
        var x = inRect.width;

        DoRevRowButton(ref x, 0, "HugsLogPublisher.shareBtn".Translate(), "HugsLogPublisher.shareBtnDescr".Translate(),
            () => LogPublisher.Instance.ShowPublishPrompt());
    }

    private static void DoRevRowButton(ref float x, float y, string text, string tooltip, Action action)
    {
        var vector2 = Text.CalcSize(text);

        #if RW_1_5_OR_GREATER
        var rect = new Rect(x - vector2.x - 10f, y, vector2.x + 10f, 24f);
        if (DevGUI.ButtonText(rect, text)) action();
        #else
        var rect = new Rect(x - vector2.x - 16f, y, vector2.x + 16f, vector2.y + 2f);
        if (Widgets.ButtonText(rect, text)) action();
        #endif

        if (!tooltip.NullOrEmpty())
            TooltipHandler.TipRegion(rect, (TipSignal) tooltip);
        x -= rect.width + 4f;
    }
}

using System.Linq;
using HugsLogPublisher.Compatibility;
using LunarFramework;
using LunarFramework.Logging;
using LunarFramework.Patching;
using UnityEngine;
using Verse;

namespace HugsLogPublisher;

[LunarComponentEntrypoint]
public static class LogPublisherEntrypoint
{
    internal static readonly LunarAPI LunarAPI = LunarAPI.Create("HugsLogPublisher", Init, Cleanup);

    internal static LogContext Logger => LunarAPI.LogContext;

    internal static PatchGroup CompatPatchGroup;

    internal static bool IsStandaloneMod { get; private set; }

    private const string StandalonePackageId = "m00nl1ght.UnofficialUpdates.HugsLogPublisher";

    private static void Init()
    {
        IsStandaloneMod = ModsConfig.ActiveModsInLoadOrder.Any(m => m.SamePackageId(StandalonePackageId, true));

        CompatPatchGroup ??= LunarAPI.RootPatchGroup.NewSubGroup("Compat");
        CompatPatchGroup.Subscribe();

        ModCompat.ApplyAll(LunarAPI, CompatPatchGroup);

        if (LogPublisher.Instance != null && IsStandaloneMod && !ModCompat_HugsLib.IsPresent)
        {
            LunarAPI.LifecycleHooks.DoOnGUI(OnGUI);
        }
    }

    private static void Cleanup()
    {
        CompatPatchGroup?.UnsubscribeAll();
    }

    private static void OnGUI()
    {
        if (Event.current.type != EventType.KeyDown) return;

        if (Input.GetKey(KeyCode.F12) && HugsLibUtility.ControlIsHeld)
        {
            if (HugsLibUtility.AltIsHeld)
            {
                LogPublisher.Instance.CopyToClipboard();
            }
            else
            {
                LogPublisher.Instance.ShowPublishPrompt();
            }

            Event.current.Use();
        }
    }
}

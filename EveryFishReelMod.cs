using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Completes one real reel immediately, then records every real fish through the native mission save path.</summary>
public sealed class EveryFishReelMod : BaseMod
{
    private static readonly Ref<string> Status = new("Every Fish Reel is disabled.");
    private static readonly FieldInfo FishPullingValueField = AccessTools.Field(typeof(FishingRod), "fishPullingValue");
    private static readonly FieldInfo ActionInteractField = AccessTools.Field(typeof(FishingRod), "actionInteract");
    private static readonly MethodInfo GetAllFishesMethod = AccessTools.Method(typeof(WorldMissionFishing), "GetAllFishes");
    private static readonly MethodInfo HasCaughtFishMethod = AccessTools.Method(typeof(WorldMissionFishing), "HasCaughtFish");
    private static readonly MethodInfo IncrementCaughtCountMethod =
        AccessTools.Method(typeof(WorldMissionFishing), "IncrementCaughtCount");
    private static readonly MethodInfo CheckCollectedAllFishesMethod =
        AccessTools.Method(typeof(WorldMissionFishing), "CheckCollectedAllFishes");
    private static bool grantingAllFish;

    public override string Name => "Every Fish Reel";
    public override string Description =>
        "One reel completes the catch and records every real fish, with randomized pull strength.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Label = "Enable every-fish reel")]
    public static Ref<bool> Enabled = new();

    [ModSetting(Order = 20, Min = 0.25f, Max = 5f, Label = "Minimum random pull")]
    public static Ref<float> MinimumPull = new(0.7f);

    [ModSetting(Order = 30, Min = 0.25f, Max = 5f, Label = "Maximum random pull")]
    public static Ref<float> MaximumPull = new(2.8f);

    [ModSetting(Order = 40, Label = "Only add missing fish")]
    public static Ref<bool> OnlyMissingFish = new(true);

    protected override void OnStaticInit()
    {
        var harmony = new Harmony(Plugin.Guid + ".every-fish-reel");
        harmony.PatchAll(typeof(OneReelCatchPatch));
        harmony.PatchAll(typeof(EveryFishCatchPatch));
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("EveryFishReelHelp",
                "Enable the mode, use a normal fishing rod, wait for a fish to bite, and reel once. The catch meter completes " +
                "on that reel with a new random pull strength. After the real catch succeeds, every fish species from every " +
                "installed fishing area is recorded through Wobbly Life's normal fishing-mission save data."),
            base.BuildPanel(id),
            new HStack("EveryFishReelActions",
                ActionMenu(new Button("Enable", Enable), nameof(Enable)),
                ActionMenu(new Button("Disable", Disable), nameof(Disable))
            ).WithContentWidth(),
            new TextWrapped("EveryFishReelStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void Enable()
    {
        Enabled.Value = true;
        Status.Value = "Enabled. Catch a bite and reel once to collect every fish.";
    }

    [ModAction(ShowInUI = false)]
    public static void Disable()
    {
        Enabled.Value = false;
        Status.Value = "Every Fish Reel is disabled.";
    }

    private static bool IsLocalRod(FishingRod rod)
    {
        if (!rod || !GameInstance.InstanceExists) return false;
        var localPlayer = GameInstance.Instance.GetFirstLocalPlayerController();
        var interact = ActionInteractField?.GetValue(rod) as ActionEnterExitInteract;
        return localPlayer && interact && interact.GetDriverController() == localPlayer;
    }

    private static void CompleteReel(FishingRod rod, ref float fishRodPullValue)
    {
        var minimum = Mathf.Max(0.25f, Mathf.Min(MinimumPull.Value, MaximumPull.Value));
        var maximum = Mathf.Max(minimum, Mathf.Max(MinimumPull.Value, MaximumPull.Value));
        var randomPower = UnityEngine.Random.Range(minimum, maximum);
        fishRodPullValue = randomPower;
        FishPullingValueField?.SetValue(rod, 1.05f + randomPower * 0.05f);
        Status.Value = $"One-reel catch triggered with random pull power {randomPower:0.00}.";
    }

    private static void GrantEveryFish(WorldMissionFishing mission, PlayerController playerController)
    {
        if (!mission || !playerController || grantingAllFish || !PropSpawnManager.IsServer) return;
        var fishes = GetAllFishesMethod?.Invoke(mission, null) as IEnumerable;
        if (fishes == null)
        {
            Status.Value = "The game's fish catalog is not ready yet.";
            return;
        }

        grantingAllFish = true;
        var awarded = 0;
        var total = 0;
        try
        {
            foreach (var entry in fishes)
            {
                if (entry is not FishCatchableScriptableObject fish || !fish || !fish.IsAFish()) continue;
                total++;
                var assetId = fish.GetAssetid();
                if (string.IsNullOrWhiteSpace(assetId)) continue;
                var alreadyCaught = HasCaughtFishMethod != null &&
                    (bool)HasCaughtFishMethod.Invoke(mission, new object[] { assetId });
                if (OnlyMissingFish.Value && alreadyCaught) continue;
                IncrementCaughtCountMethod?.Invoke(mission, new object[] { assetId, playerController });
                awarded++;
            }
            CheckCollectedAllFishesMethod?.Invoke(mission, null);
            Status.Value = awarded == 0
                ? $"All {total} fish species were already caught."
                : $"One reel caught every fish: {awarded} new of {total} total species recorded.";
            Plugin.Log?.LogInfo(Status.Value);
        }
        catch (Exception exception)
        {
            Status.Value = $"Every-fish grant stopped safely: {exception.GetBaseException().Message}";
            Plugin.Log?.LogError(exception);
        }
        finally
        {
            grantingAllFish = false;
        }
    }

    [HarmonyPatch(typeof(FishingRod), "PushRodTowardFish")]
    private static class OneReelCatchPatch
    {
        private static void Prefix(FishingRod __instance, bool bRealingIn, ref float fishRodPullValue)
        {
            if (Enabled.Value && bRealingIn && IsLocalRod(__instance))
                CompleteReel(__instance, ref fishRodPullValue);
        }
    }

    [HarmonyPatch(typeof(WorldMissionFishing), "IncrementCaughtCount")]
    private static class EveryFishCatchPatch
    {
        private static void Postfix(WorldMissionFishing __instance, PlayerController playerController)
        {
            if (Enabled.Value && !grantingAllFish && playerController && GameInstance.InstanceExists &&
                playerController == GameInstance.Instance.GetFirstLocalPlayerController())
                GrantEveryFish(__instance, playerController);
        }
    }
}

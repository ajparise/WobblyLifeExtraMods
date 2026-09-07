using System.Collections.Generic;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.Mods;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Turns strong plane steering inputs into complete aerobatic maneuvers.</summary>
public sealed class RealisticPlaneFlightMod : BaseMod
{
    private enum StuntAxis
    {
        None,
        Pitch,
        Roll
    }

    private sealed class StuntState
    {
        internal StuntAxis Axis;
        internal float Direction;
        internal float DegreesRemaining;
        internal bool AwaitingRecenter;
    }

    private static readonly Ref<bool> EnabledState = new();
    private static readonly Ref<string> Status = new("Realistic stunt flight is disabled.");
    private static readonly Dictionary<PlayerPlaneMovement, StuntState> States = new();
    private static bool firstPersonEntryHandled;
    private static bool forcedFirstPerson;
    private static bool previousFirstPersonAll;
    private static bool previousFirstPersonPlayerOne;

    public override string Name => "Realistic Plane Flight";

    public override string Description =>
        "Strong up/down steering performs complete back/front flips, while left/right steering performs barrel rolls.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 60f, Label = "Stunt activation angle",
        Description = "How far the flight aim must move from the plane before a maneuver begins.")]
    public static Ref<float> ActivationAngle = new(18f);

    [ModSetting(Order = 20, Min = 60f, Max = 360f, Label = "Flip speed",
        Description = "Pitch rotation speed in degrees per second.")]
    public static Ref<float> FlipSpeed = new(140f);

    [ModSetting(Order = 30, Min = 60f, Max = 500f, Label = "Barrel-roll speed",
        Description = "Roll rotation speed in degrees per second.")]
    public static Ref<float> RollSpeed = new(140f);

    [ModSetting(Order = 40, Min = 0f, Max = 1f, Label = "Cross-axis freedom",
        Description = "How much normal plane rotation remains during a stunt. Lower values produce cleaner loops.")]
    public static Ref<float> CrossAxisFreedom = new(0.15f);

    [ModSetting(Order = 50, Label = "Automatic first person",
        Description = "Switches player one to first person upon entering a plane and restores the previous camera mode on exit.")]
    public static Ref<bool> AutomaticFirstPerson = new(true);

    protected override void OnStaticInit()
    {
        new Harmony("com.aj.wobblylife.extramods.realisticplaneflight")
            .PatchAll(typeof(PlaneMovementPatch));
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("RealisticPlaneHelp",
                "Enable this mode and pilot a normal plane. Aim strongly upward for a full backflip, downward for a " +
                "front flip, or left/right for one barrel roll. Recenter the controls before starting another maneuver. " +
                "Designed for offline play or a lobby you host."),
            base.BuildPanel(id),
            new HStack("RealisticPlaneActions",
                ActionMenu(new Button("Enable flight mode", Enable), nameof(Enable)),
                ActionMenu(new Button("Disable flight mode", Disable), nameof(Disable))
            ).WithContentWidth(),
            new TextWrapped("RealisticPlaneStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void Enable()
    {
        EnabledState.Value = true;
        States.Clear();
        Status.Value = "Realistic stunt flight enabled. Enter a plane and use its normal steering controls.";
    }

    [ModAction(ShowInUI = false)]
    public static void Disable()
    {
        EnabledState.Value = false;
        States.Clear();
        RestoreFirstPerson();
        Status.Value = "Realistic stunt flight disabled.";
    }

    public override void Update()
    {
        var inPlane = EnabledState.Value && IsLocalPlayerDrivingPlane();

        if (inPlane && AutomaticFirstPerson.Value && !firstPersonEntryHandled)
            EnterFirstPerson();
        else if ((!inPlane || !AutomaticFirstPerson.Value) && firstPersonEntryHandled)
            RestoreFirstPerson();
    }

    private static bool IsLocalPlayerDrivingPlane()
    {
        if (!GameInstance.InstanceExists) return false;

        var controller = GameInstance.Instance.GetFirstLocalPlayerController();
        var interactor = controller ? controller.GetPlayerControllerInteractor() : null;
        var entered = interactor?.GetEnteredAction()?.GetGameObject();
        if (!entered) return false;

        var plane = entered.GetComponentInParent<PlayerPlane>() ?? entered.GetComponentInChildren<PlayerPlane>(true);
        return plane && plane.GetDriverPlayerController() == controller;
    }

    private static void EnterFirstPerson()
    {
        firstPersonEntryHandled = true;
        previousFirstPersonAll = FirstPerson.FirstPersonEnabled.Value;
        previousFirstPersonPlayerOne = FirstPerson.FirstPersonEnabledPlayer1.Value;

        if (!previousFirstPersonAll && !previousFirstPersonPlayerOne)
        {
            FirstPerson.FirstPersonEnabledPlayer1.Value = true;
            forcedFirstPerson = true;
            Status.Value = "Plane entered: stunt flight and first-person camera active.";
        }
    }

    private static void RestoreFirstPerson()
    {
        firstPersonEntryHandled = false;
        if (forcedFirstPerson)
        {
            FirstPerson.FirstPersonEnabled.Value = previousFirstPersonAll;
            FirstPerson.FirstPersonEnabledPlayer1.Value = previousFirstPersonPlayerOne;
        }

        forcedFirstPerson = false;
        Status.Value = "Plane exited: previous camera mode restored.";
    }

    private static void Simulate(PlayerPlaneMovement movement, PlaneInput input)
    {
        if (!EnabledState.Value || !PropSpawnManager.IsServer || !movement) return;

        var plane = movement.GetComponentInParent<PlayerPlane>();
        var driver = plane ? plane.GetDriverPlayerController() : null;
        if (!driver || !driver.IsLocal()) return;

        var body = movement.GetComponent<Rigidbody>() ?? movement.GetComponentInParent<Rigidbody>();
        if (!body || body.isKinematic) return;

        if (!States.TryGetValue(movement, out var state))
        {
            state = new StuntState();
            States[movement] = state;
        }

        if (state.Axis == StuntAxis.None)
        {
            var inputAngles = GetInputAngles(movement.transform, input);
            var threshold = Mathf.Clamp(ActivationAngle.Value, 1f, 89f);

            if (state.AwaitingRecenter)
            {
                if (Mathf.Abs(inputAngles.x) <= threshold * 0.45f &&
                    Mathf.Abs(inputAngles.y) <= threshold * 0.45f)
                    state.AwaitingRecenter = false;

                return;
            }

            BeginManeuver(inputAngles, threshold, state);
        }

        if (state.Axis == StuntAxis.None) return;

        var rate = state.Axis == StuntAxis.Pitch
            ? Mathf.Max(1f, FlipSpeed.Value)
            : Mathf.Max(1f, RollSpeed.Value);
        var step = Mathf.Min(state.DegreesRemaining, rate * Time.fixedDeltaTime);
        var localAngular = movement.transform.InverseTransformDirection(body.angularVelocity);
        var freedom = Mathf.Clamp01(CrossAxisFreedom.Value);

        if (state.Axis == StuntAxis.Pitch)
        {
            localAngular.x = state.Direction * rate * Mathf.Deg2Rad;
            localAngular.y *= freedom;
            localAngular.z *= freedom;
        }
        else
        {
            localAngular.x *= freedom;
            localAngular.y *= freedom;
            localAngular.z = state.Direction * rate * Mathf.Deg2Rad;
        }

        body.maxAngularVelocity = Mathf.Max(body.maxAngularVelocity, rate * Mathf.Deg2Rad + 0.5f);
        body.angularVelocity = movement.transform.TransformDirection(localAngular);
        state.DegreesRemaining -= step;

        if (state.DegreesRemaining <= 0.01f)
        {
            if (state.Axis == StuntAxis.Pitch) localAngular.x = 0f;
            else localAngular.z = 0f;
            body.angularVelocity = movement.transform.TransformDirection(localAngular);
            state.Axis = StuntAxis.None;
            state.AwaitingRecenter = true;
            Status.Value = "Maneuver complete. Recenter the controls to arm the next stunt.";
        }
    }

    private static Vector2 GetInputAngles(Transform planeTransform, PlaneInput input)
    {
        var aimRotation = Quaternion.Euler(input.xRotationAngle, input.yRotationAngle, 0f);
        var localAim = planeTransform.InverseTransformDirection(aimRotation * Vector3.forward);
        var pitchAngle = Mathf.Atan2(localAim.y, localAim.z) * Mathf.Rad2Deg;
        var sideAngle = Mathf.Atan2(localAim.x, localAim.z) * Mathf.Rad2Deg;
        return new Vector2(pitchAngle, sideAngle);
    }

    private static void BeginManeuver(Vector2 inputAngles, float threshold, StuntState state)
    {
        var pitchAngle = inputAngles.x;
        var sideAngle = inputAngles.y;

        if (Mathf.Abs(pitchAngle) >= threshold && Mathf.Abs(pitchAngle) >= Mathf.Abs(sideAngle))
        {
            state.Axis = StuntAxis.Pitch;
            // Nose-up is negative local X in Unity: upward aim therefore produces a backflip.
            state.Direction = pitchAngle > 0f ? -1f : 1f;
            state.DegreesRemaining = 360f;
            Status.Value = pitchAngle > 0f ? "Backflip started." : "Front flip started.";
        }
        else if (Mathf.Abs(sideAngle) >= threshold)
        {
            state.Axis = StuntAxis.Roll;
            // Right steering drops the right wing; left steering drops the left wing.
            state.Direction = sideAngle > 0f ? -1f : 1f;
            state.DegreesRemaining = 360f;
            Status.Value = sideAngle > 0f ? "Right barrel roll started." : "Left barrel roll started.";
        }
    }

    [HarmonyPatch(typeof(PlayerPlaneMovement), "SimulateVehicleInput")]
    private static class PlaneMovementPatch
    {
        private static void Postfix(PlayerPlaneMovement __instance, PlaneInput input)
        {
            try
            {
                Simulate(__instance, input);
            }
            catch (System.Exception exception)
            {
                Plugin.Log?.LogError($"Realistic plane flight failed: {exception}");
            }
        }
    }
}

using System;
using System.Collections.Generic;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>
/// A host-authoritative, camera-aimed cone impulse. The first version is an
/// equippable gameplay mode; a held model and particles can be layered on later.
/// </summary>
public sealed class WindCannonMod : BaseMod
{
    private const float CrosshairGap = 7f;
    private const float CrosshairLength = 9f;
    private const float CrosshairThickness = 2f;

    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Wind cannon is unequipped.");
    private static readonly HashSet<Rigidbody> SeenBodies = new();
    private static readonly HashSet<Rigidbody> IgnoredBodies = new();

    private static float nextFireTime;

    public override string Name => "Wind Cannon";

    public override string Description =>
        "Equip a camera-aimed cannon that launches physics objects in a powerful cone of wind.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 150f, Label = "Gust velocity",
        Description = "Velocity added to affected objects regardless of mass, so heavy cars and light props both move.")]
    public static Ref<float> GustForce = new(45f);

    [ModSetting(Order = 20, Min = 5f, Max = 80f, Label = "Range")]
    public static Ref<float> Range = new(35f);

    [ModSetting(Order = 30, Min = 5f, Max = 60f, Label = "Cone angle")]
    public static Ref<float> ConeAngle = new(24f);

    [ModSetting(Order = 40, Min = 0.05f, Max = 2f, Label = "Cooldown")]
    public static Ref<float> Cooldown = new(0.35f);

    [ModSetting(Order = 50, Label = "Require line of sight",
        Description = "Stops the gust from pushing objects through walls.")]
    public static Ref<bool> RequireLineOfSight = new(true);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("WindCannonHelp",
                "Equip the cannon, close the F2 menu, aim with the crosshair, and press the left mouse button. " +
                "Physics authority remains with the host, so use this offline or in a lobby you host."),
            base.BuildPanel(id),
            new HStack("WindCannonEquipActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Test gust", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("WindCannonStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void Equip()
    {
        PropSpawnerGunMod.Unequip();
        VehicleAircraftSpawnerMod.Unequip();
        RocketLauncherMod.Unequip();
        PaintballGunMod.Unequip();
        MinecraftBuildingMod.Unequip();
        HeavyAutomaticGunMod.Unequip();
        GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        LightningGunMod.Unequip();
        WobblyHeadHomingGunMod.Unequip();
        BananaPeelLauncherMod.Unequip();
        TornadoGunMod.Unequip();
        FireworkMinigunMod.Unequip();
        JellyGunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip();
        MosesStaffMod.Unequip();
        PowerSwordMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Wind cannon equipped. Close the menu, aim, and left-click to fire.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Wind cannon unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (Time.unscaledTime < nextFireTime)
            return;

        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can fire the wind cannon.";
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before firing.";
            return;
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.05f, Cooldown.Value);

        var origin = camera.transform.position + camera.transform.forward * 0.75f;
        var forward = camera.transform.forward;
        var maxDistance = Mathf.Max(1f, Range.Value);
        var minimumDot = Mathf.Cos(Mathf.Clamp(ConeAngle.Value, 1f, 89f) * Mathf.Deg2Rad);
        var colliders = Physics.OverlapSphere(origin, maxDistance, ~0, QueryTriggerInteraction.Ignore);

        SeenBodies.Clear();
        CacheLocalPlayerBodies();
        var affected = 0;

        foreach (var collider in colliders)
        {
            if (!collider) continue;

            var body = collider.attachedRigidbody;
            if (!body || body.isKinematic || IgnoredBodies.Contains(body) || !SeenBodies.Add(body)) continue;

            var offset = body.worldCenterOfMass - origin;
            var distance = offset.magnitude;
            if (distance < 0.01f || distance > maxDistance) continue;

            var directionToBody = offset / distance;
            if (Vector3.Dot(forward, directionToBody) < minimumDot) continue;
            if (RequireLineOfSight.Value && IsBlocked(origin, directionToBody, distance, body)) continue;

            // Keep a strong forward component so nearby bodies fly in the aimed direction,
            // with a small radial component to make the gust widen naturally.
            var pushDirection = (forward * 0.82f + directionToBody * 0.18f).normalized;
            var falloff = Mathf.Lerp(0.35f, 1f, 1f - distance / maxDistance);

            body.WakeUp();
            // VelocityChange deliberately ignores mass. An ordinary impulse barely moves the
            // game's tonne-scale vehicle rigidbodies while violently launching character limbs.
            body.AddForce(pushDirection * GustForce.Value * falloff, ForceMode.VelocityChange);
            if (collider.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("wind cannon hit");
            affected++;
        }

        Status.Value = affected == 1
            ? "Gust fired: 1 physics object launched."
            : $"Gust fired: {affected} physics objects launched.";
        Plugin.Log?.LogInfo(Status.Value);
    }

    private static void CacheLocalPlayerBodies()
    {
        IgnoredBodies.Clear();

        if (!GameInstance.InstanceExists) return;

        var localController = GameInstance.Instance.GetFirstLocalPlayerController();
        var localCharacter = localController ? localController.GetPlayerCharacter() : null;
        if (!localCharacter) return;

        // Wobblies use several ragdoll rigidbodies. Excluding only the hip still lets the gust
        // catch a hand, foot, or head and fling the person firing the cannon.
        foreach (var body in localCharacter.GetComponentsInChildren<Rigidbody>(true))
        {
            if (body) IgnoredBodies.Add(body);
        }
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;

        // Clicking on the lstwoMODS overlay should never fire into the world.
        if (Cursor.visible) return;

        if (Input.GetMouseButtonDown(0))
            Fire();
    }

    private static bool IsBlocked(Vector3 origin, Vector3 direction, float distance, Rigidbody target)
    {
        if (!Physics.Raycast(origin, direction, out var hit, distance, ~0, QueryTriggerInteraction.Ignore))
            return false;

        return hit.rigidbody != target;
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint)
            return;

        var centerX = Screen.width * 0.5f;
        var centerY = Screen.height * 0.5f;
        var previousColor = GUI.color;
        GUI.color = new Color(0.55f, 0.95f, 1f, 0.95f);

        DrawRect(centerX - CrosshairGap - CrosshairLength, centerY - CrosshairThickness * 0.5f,
            CrosshairLength, CrosshairThickness);
        DrawRect(centerX + CrosshairGap, centerY - CrosshairThickness * 0.5f,
            CrosshairLength, CrosshairThickness);
        DrawRect(centerX - CrosshairThickness * 0.5f, centerY - CrosshairGap - CrosshairLength,
            CrosshairThickness, CrosshairLength);
        DrawRect(centerX - CrosshairThickness * 0.5f, centerY + CrosshairGap,
            CrosshairThickness, CrosshairLength);

        GUI.color = previousColor;
    }

    private static void DrawRect(float x, float y, float width, float height)
    {
        GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
    }
}

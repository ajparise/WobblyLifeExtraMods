using System;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Adds speed-sensitive damage, momentum loss, spin, and occupant ragdolls to road vehicle crashes.</summary>
public sealed class RealisticCarCrashMod : BaseMod
{
    private static readonly Ref<bool> EnabledState = new();
    private static readonly Ref<bool> RagdollOccupants = new(true);
    private static readonly Ref<bool> IgnoreLightStreetProps = new(true);
    private static readonly Ref<string> Status = new("Realistic car crashes are disabled.");
    private static readonly List<RealisticCarCrashSensor> Cleanup = new();
    private static float nextVehicleScan;

    public override string Name => "Realistic Car Crashes";

    public override string Description =>
        "Makes road vehicle crashes react to impact speed with damage, momentum loss, body rotation, and severe-crash occupant ragdolls.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 80f, Label = "Minimum crash speed (km/h)",
        Description = "Impacts below this closing speed use the game's normal collision physics.")]
    public static Ref<float> MinimumCrashSpeed = new(20f);

    [ModSetting(Order = 20, Min = 30f, Max = 160f, Label = "Severe crash speed (km/h)",
        Description = "Occupants ragdoll when the direct impact speed reaches this value.")]
    public static Ref<float> SevereCrashSpeed = new(70f);

    [ModSetting(Order = 30, Min = 0.2f, Max = 8f, Label = "Vehicle damage multiplier")]
    public static Ref<float> DamageMultiplier = new(2.2f);

    [ModSetting(Order = 40, Min = 0f, Max = 0.85f, Label = "Maximum momentum loss",
        Description = "Controls how sharply the car slows in a major impact.")]
    public static Ref<float> MaximumMomentumLoss = new(0.55f);

    [ModSetting(Order = 50, Min = 0f, Max = 8f, Label = "Off-center crash spin")]
    public static Ref<float> CrashSpin = new(2.4f);

    [ModSetting(Order = 60, Min = 5f, Max = 500f, Label = "Maximum damage per crash",
        Description = "Prevents a single ordinary collision from instantly destroying the vehicle.")]
    public static Ref<float> MaximumDamage = new(75f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("RealisticCrashHelp",
                "Enable this before driving. Direct impact speed determines vehicle damage and lost momentum. " +
                "Off-center collisions rotate the car, while sufficiently severe crashes ragdoll its occupants. " +
                "Native vehicle destruction is synchronized when you are offline or hosting."),
            new Checkbox("Ragdoll occupants in severe crashes", true).WithValue(RagdollOccupants),
            new Checkbox("Ignore light roadside props", true).WithValue(IgnoreLightStreetProps),
            base.BuildPanel(id),
            new HStack("RealisticCrashActions",
                ActionMenu(new Button("Enable realistic crashes", Enable), nameof(Enable)),
                ActionMenu(new Button("Disable realistic crashes", Disable), nameof(Disable))
            ).WithContentWidth(),
            new TextWrapped("RealisticCrashStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void Enable()
    {
        EnabledState.Value = true;
        nextVehicleScan = 0f;
        Status.Value = "Realistic car crashes enabled. Road vehicles are being monitored.";
    }

    [ModAction(ShowInUI = false)]
    public static void Disable()
    {
        EnabledState.Value = false;
        RemoveSensors();
        Status.Value = "Realistic car crashes disabled.";
    }

    public override void Update()
    {
        if (!EnabledState.Value || Time.unscaledTime < nextVehicleScan) return;
        nextVehicleScan = Time.unscaledTime + 1f;

        var attached = 0;
        foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<PlayerVehicle>())
        {
            if (!vehicle || !IsRoadVehicle(vehicle) || vehicle.GetComponent<RealisticCarCrashSensor>()) continue;
            var sensor = vehicle.gameObject.AddComponent<RealisticCarCrashSensor>();
            sensor.Configure(vehicle);
            attached++;
        }

        if (attached > 0)
            Status.Value = $"Realistic crashes active; monitoring {UnityEngine.Object.FindObjectsOfType<RealisticCarCrashSensor>().Length:N0} road vehicles.";
    }

    private static bool IsRoadVehicle(PlayerVehicle vehicle)
    {
        if (vehicle.GetComponent("PlayerVehicleRoad")) return true;
        return vehicle.GetComponents<MonoBehaviour>()
            .Any(component => component && component.GetType().Name == "PlayerVehicleRoad");
    }

    private static void RemoveSensors()
    {
        Cleanup.Clear();
        Cleanup.AddRange(UnityEngine.Object.FindObjectsOfType<RealisticCarCrashSensor>());
        foreach (var sensor in Cleanup)
            if (sensor) UnityEngine.Object.Destroy(sensor);
        Cleanup.Clear();
    }

    internal static bool ShouldRagdollOccupants => RagdollOccupants.Value;
    internal static bool ShouldIgnoreLightStreetProps => IgnoreLightStreetProps.Value;

    internal static void ReportCrash(float speedKmh, int damage, bool severe, string vehicleName)
    {
        var result = severe ? "SEVERE CRASH" : "Crash";
        Status.Value = $"{result}: {vehicleName} hit at {speedKmh:0} km/h; {damage:N0} damage applied.";
        Plugin.Log?.LogInfo(Status.Value);
    }
}

internal sealed class RealisticCarCrashSensor : MonoBehaviour
{
    private static readonly string[] LightStreetPropNames =
    {
        "lightpost", "streetlight", "trafficlight", "lamppost", "lampost", "roadsign", "signpost",
        "bollard", "trafficcone", "parkingmeter", "mailbox", "trashcan", "rubbishbin", "firehydrant"
    };

    private PlayerVehicle vehicle;
    private Rigidbody body;
    private PlayerVehicleDestructable destructable;
    private float nextCrashTime;

    internal void Configure(PlayerVehicle target)
    {
        vehicle = target;
        var movement = vehicle.GetVehicleMovementBase();
        body = movement ? movement.GetRigidbody() : vehicle.GetComponent<Rigidbody>();
        destructable = vehicle.GetComponent<PlayerVehicleDestructable>();
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!vehicle || !body || collision == null || collision.contactCount == 0 ||
            Time.unscaledTime < nextCrashTime)
            return;

        var contact = collision.GetContact(0);
        var closingSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
        var speedKmh = closingSpeed * 3.6f;
        var minimum = Mathf.Clamp(RealisticCarCrashMod.MinimumCrashSpeed.Value, 5f, 80f);
        if (speedKmh < minimum) return;

        // Breakaway street furniture should bend or fly away without being interpreted as a wall.
        // Let the game's ordinary collision/destruction code handle these contacts and add no car damage.
        if (RealisticCarCrashMod.ShouldIgnoreLightStreetProps && IsLightRoadsideObstacle(collision.collider))
        {
            nextCrashTime = Time.unscaledTime + 0.15f;
            return;
        }

        nextCrashTime = Time.unscaledTime + 0.35f;
        var severeThreshold = Mathf.Max(minimum + 5f,
            Mathf.Clamp(RealisticCarCrashMod.SevereCrashSpeed.Value, 30f, 160f));
        var severity = Mathf.Clamp01((speedKmh - minimum) / Mathf.Max(20f, severeThreshold - minimum));
        var severe = speedKmh >= severeThreshold;

        ApplyCrashPhysics(contact, severity);

        var maximumDamage = Mathf.Clamp(Mathf.RoundToInt(RealisticCarCrashMod.MaximumDamage.Value), 5, 500);
        var damage = Mathf.Clamp(Mathf.RoundToInt(
            (speedKmh - minimum) * Mathf.Clamp(RealisticCarCrashMod.DamageMultiplier.Value, 0.2f, 8f)),
            1, maximumDamage);

        if (destructable && PropSpawnManager.IsServer)
            destructable.ServerDamage((short)damage, false, false);

        if (severe && RealisticCarCrashMod.ShouldRagdollOccupants)
            RagdollVehicleOccupants(collision.relativeVelocity, severity);

        var displayName = string.IsNullOrWhiteSpace(vehicle.name)
            ? "vehicle"
            : vehicle.name.Replace("(Clone)", string.Empty).Trim();
        RealisticCarCrashMod.ReportCrash(speedKmh, damage, severe, displayName);
    }

    private bool IsLightRoadsideObstacle(Collider otherCollider)
    {
        if (!otherCollider) return false;
        if (otherCollider.GetComponentInParent<PlayerVehicle>()) return false;

        // Characters are soft collision targets too; hitting one must not behave like hitting concrete.
        if (otherCollider.GetComponentInParent<PlayerCharacter>() ||
            otherCollider.GetComponentInParent<PlayerNPCController>())
            return true;

        var otherBody = otherCollider.attachedRigidbody;
        if (otherBody && otherBody != body && !otherBody.isKinematic)
        {
            var carMass = Mathf.Max(1f, body.mass);
            if (otherBody.mass <= carMass * 0.2f) return true;
        }

        var current = otherCollider.transform;
        for (var depth = 0; current && depth < 8; depth++, current = current.parent)
        {
            var normalizedName = NormalizeName(current.name);
            if (LightStreetPropNames.Any(normalizedName.Contains)) return true;

            foreach (var component in current.GetComponents<MonoBehaviour>())
            {
                if (!component) continue;
                var normalizedType = NormalizeName(component.GetType().Name);
                if (LightStreetPropNames.Any(normalizedType.Contains)) return true;
            }
        }

        return false;
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private void ApplyCrashPhysics(ContactPoint contact, float severity)
    {
        var loss = Mathf.Clamp01(RealisticCarCrashMod.MaximumMomentumLoss.Value) * severity;
        var velocity = body.velocity;
        body.velocity = new Vector3(velocity.x * (1f - loss), velocity.y, velocity.z * (1f - loss));

        var offset = contact.point - body.worldCenterOfMass;
        var spinAxis = Vector3.Cross(offset.normalized, contact.normal).normalized;
        if (spinAxis.sqrMagnitude > 0.01f)
            body.AddTorque(spinAxis * (Mathf.Clamp(RealisticCarCrashMod.CrashSpin.Value, 0f, 8f) * severity),
                ForceMode.VelocityChange);
    }

    private void RagdollVehicleOccupants(Vector3 impactVelocity, float severity)
    {
        foreach (var character in UnityEngine.Object.FindObjectsOfType<PlayerCharacter>())
        {
            if (!character) continue;
            var controller = character.GetPlayerController();
            var entered = controller?.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
            var occupiedVehicle = entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
            if (occupiedVehicle != vehicle) continue;

            var ragdoll = character.GetRagdollController();
            if (!ragdoll) continue;
            ragdoll.Ragdoll();

            var playerBody = character.GetComponentInChildren<PlayerBody>(true);
            if (!playerBody) continue;
            var throwDirection = (-impactVelocity.normalized + Vector3.up * 0.35f).normalized;
            playerBody.SetRagdollVelocity(throwDirection * Mathf.Lerp(4f, 12f, severity));
        }
    }
}

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
    private static readonly Ref<bool> BodyDeformation = new(true);
    private static readonly Ref<bool> MechanicalDamage = new(true);
    private static readonly Ref<bool> PartSeparation = new(true);
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

    [ModSetting(Order = 70, Min = 0.15f, Max = 2.5f, Label = "Dent radius")]
    public static Ref<float> DentRadius = new(0.85f);

    [ModSetting(Order = 80, Min = 0.02f, Max = 0.8f, Label = "Maximum dent depth")]
    public static Ref<float> DentDepth = new(0.28f);

    [ModSetting(Order = 90, Min = 35f, Max = 160f, Label = "Wheel damage speed (km/h)")]
    public static Ref<float> WheelDamageSpeed = new(75f);

    [ModSetting(Order = 100, Min = 8f, Max = 80f, Label = "Structure spring stiffness")]
    public static Ref<float> StructureSpring = new(34f);

    [ModSetting(Order = 110, Min = 1f, Max = 20f, Label = "Structure damping")]
    public static Ref<float> StructureDamping = new(9f);

    [ModSetting(Order = 120, Min = 55f, Max = 180f, Label = "Part separation speed (km/h)")]
    public static Ref<float> PartSeparationSpeed = new(85f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("RealisticCrashHelp",
                "Enable this before driving. Direct impact speed determines vehicle damage and lost momentum. " +
                "BeamNG-inspired impacts use spring-damped mesh deformation, accumulate mechanical damage, bend wheels, " +
                "and can separate visible panels. " +
                "Off-center collisions rotate the car, while sufficiently severe crashes ragdoll its occupants. " +
                "Native vehicle destruction is synchronized when you are offline or hosting."),
            new Checkbox("Ragdoll occupants in severe crashes", true).WithValue(RagdollOccupants),
            new Checkbox("Ignore light roadside props", true).WithValue(IgnoreLightStreetProps),
            new Checkbox("BeamNG-style body deformation", true).WithValue(BodyDeformation),
            new Checkbox("Cumulative mechanical damage", true).WithValue(MechanicalDamage),
            new Checkbox("Separate parts in extreme crashes", true).WithValue(PartSeparation),
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
    internal static bool ShouldDeformBody => BodyDeformation.Value;
    internal static bool ShouldApplyMechanicalDamage => MechanicalDamage.Value;
    internal static bool ShouldSeparateParts => PartSeparation.Value;

    internal static void ReportCrash(float speedKmh, int damage, bool severe, string vehicleName,
        float chassisDamage, bool dented, bool wheelBent, bool partSeparated)
    {
        var result = severe ? "SEVERE CRASH" : "Crash";
        var deformation = dented ? "; body dented" : string.Empty;
        var wheel = wheelBent ? "; wheel bent" : string.Empty;
        var separated = partSeparated ? "; part separated" : string.Empty;
        Status.Value = $"{result}: {vehicleName} hit at {speedKmh:0} km/h; {damage:N0} damage; " +
                       $"chassis {chassisDamage * 100f:0}%{deformation}{wheel}{separated}.";
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
    private float chassisDamage;
    private float steeringDamage;
    private float wobblePhase;
    private readonly HashSet<Renderer> separatedRenderers = new();

    internal void Configure(PlayerVehicle target)
    {
        vehicle = target;
        var movement = vehicle.GetVehicleMovementBase();
        body = movement ? movement.GetRigidbody() : vehicle.GetComponent<Rigidbody>();
        destructable = vehicle.GetComponent<PlayerVehicleDestructable>();
        wobblePhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        Plugin.Log?.LogInfo($"Crash monitor attached to '{vehicle.name}': " +
                            $"{vehicle.GetComponentsInChildren<MeshFilter>(true).Length} mesh filters, " +
                            $"{vehicle.GetComponentsInChildren<Renderer>(true).Length} renderers.");
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
        var deformationSeverity = Mathf.Clamp01(Mathf.InverseLerp(minimum, 150f, speedKmh));
        var severe = speedKmh >= severeThreshold;

        ApplyCrashPhysics(contact, severity);
        if (RealisticCarCrashMod.ShouldApplyMechanicalDamage)
        {
            chassisDamage = Mathf.Clamp01(chassisDamage + Mathf.Lerp(0.04f, 0.38f, deformationSeverity));
            steeringDamage = Mathf.Clamp01(steeringDamage + deformationSeverity * 0.22f);
        }

        var dented = RealisticCarCrashMod.ShouldDeformBody && DeformBody(collision, deformationSeverity);
        var wheelBent = speedKmh >= Mathf.Clamp(RealisticCarCrashMod.WheelDamageSpeed.Value, 35f, 160f) &&
                        BendNearestWheel(contact.point, deformationSeverity);
        var partSeparated = RealisticCarCrashMod.ShouldSeparateParts &&
                            speedKmh >= Mathf.Clamp(RealisticCarCrashMod.PartSeparationSpeed.Value, 55f, 180f) &&
                            SeparateNearestPart(contact.point, collision.relativeVelocity, deformationSeverity);

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
        RealisticCarCrashMod.ReportCrash(speedKmh, damage, severe, displayName,
            chassisDamage, dented, wheelBent, partSeparated);
    }

    private void FixedUpdate()
    {
        if (!body || !RealisticCarCrashMod.ShouldApplyMechanicalDamage || chassisDamage <= 0.01f) return;

        var horizontalVelocity = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
        if (horizontalVelocity.sqrMagnitude > 0.25f)
        {
            // Accumulated chassis damage acts like increasing rolling resistance and lost engine efficiency.
            body.AddForce(-horizontalVelocity * Mathf.Lerp(0.03f, 0.42f, chassisDamage),
                ForceMode.Acceleration);

            // Bent suspension produces a small speed-dependent pull instead of an uncontrollable constant spin.
            var direction = Mathf.Sin(Time.time * 3.7f + wobblePhase);
            var pull = direction * steeringDamage * Mathf.Min(horizontalVelocity.magnitude, 18f) * 0.12f;
            body.AddTorque(Vector3.up * pull, ForceMode.Acceleration);
        }
    }

    private bool DeformBody(Collision collision, float severity)
    {
        if (severity <= 0.01f) return false;

        var radiusWorld = Mathf.Clamp(RealisticCarCrashMod.DentRadius.Value, 0.15f, 2.5f);
        var depthWorld = Mathf.Clamp(RealisticCarCrashMod.DentDepth.Value, 0.02f, 0.8f) * severity;
        var deformedAny = false;
        var meshCount = 0;

        var impactPoint = collision.GetContact(0).point;
        foreach (var filter in vehicle.GetComponentsInChildren<MeshFilter>(true)
                     .OrderBy(candidate => DistanceToRenderer(candidate, impactPoint)))
        {
            if (!filter || !filter.sharedMesh || IsWheelOrUtilityPart(filter.transform)) continue;
            // Keep the soft-body approximation bounded so complex workshop vehicles do not cause frame spikes.
            if (meshCount >= 3) break;

            try
            {
                var softBody = filter.GetComponent<BeamNgSoftBodyMesh>() ??
                               filter.gameObject.AddComponent<BeamNgSoftBodyMesh>();
                if (!softBody.TryInitialize(filter)) continue;

                var changed = false;
                for (var contactIndex = 0; contactIndex < collision.contactCount; contactIndex++)
                {
                    var contact = collision.GetContact(contactIndex);
                    changed |= softBody.ApplyImpact(contact.point, contact.normal, radiusWorld, depthWorld,
                        Mathf.Lerp(0.15f, 0.7f, severity));
                }

                if (!changed) continue;
                deformedAny = true;
                meshCount++;
            }
            catch (Exception)
            {
                // Some game meshes are intentionally not readable. Native damage still applies to those vehicles.
            }
        }

        // Stock vehicles can use meshes that are not CPU-readable. In that case, permanently crumple the nearest
        // independent visual transform so the collision still creates a visible body-shape change.
        return deformedAny || ApplyRendererCrumple(collision.GetContact(0), severity);
    }

    private bool ApplyRendererCrumple(ContactPoint contact, float severity)
    {
        var renderer = vehicle.GetComponentsInChildren<Renderer>(true)
            .Where(candidate => candidate && candidate.transform != vehicle.transform &&
                                !IsWheelOrUtilityPart(candidate.transform))
            .OrderBy(candidate => (candidate.bounds.ClosestPoint(contact.point) - contact.point).sqrMagnitude)
            .FirstOrDefault();
        if (!renderer) return false;

        var crumple = renderer.GetComponent<BeamNgRendererCrumple>() ??
                      renderer.gameObject.AddComponent<BeamNgRendererCrumple>();
        crumple.ApplyImpact(contact.normal,
            Mathf.Clamp(RealisticCarCrashMod.DentDepth.Value, 0.02f, 0.8f) * Mathf.Max(0.2f, severity));
        return true;
    }

    private static float DistanceToRenderer(MeshFilter filter, Vector3 point)
    {
        if (!filter) return float.MaxValue;
        var renderer = filter.GetComponent<Renderer>();
        return renderer
            ? (renderer.bounds.ClosestPoint(point) - point).sqrMagnitude
            : (filter.transform.position - point).sqrMagnitude;
    }

    private bool SeparateNearestPart(Vector3 impactPoint, Vector3 impactVelocity, float severity)
    {
        var renderer = vehicle.GetComponentsInChildren<MeshRenderer>(true)
            .Where(candidate => candidate && candidate.enabled && !separatedRenderers.Contains(candidate) &&
                                IsBreakablePanel(candidate.transform))
            .OrderBy(candidate => (candidate.bounds.ClosestPoint(impactPoint) - impactPoint).sqrMagnitude)
            .FirstOrDefault();
        if (!renderer || (renderer.bounds.ClosestPoint(impactPoint) - impactPoint).sqrMagnitude > 6.25f)
            return false;

        var sourceFilter = renderer.GetComponent<MeshFilter>();
        if (!sourceFilter || !sourceFilter.sharedMesh) return false;

        var debris = new GameObject($"ExtraMods Crash Part {renderer.name}");
        debris.transform.position = renderer.transform.position;
        debris.transform.rotation = renderer.transform.rotation;
        debris.transform.localScale = renderer.transform.lossyScale;

        var debrisFilter = debris.AddComponent<MeshFilter>();
        debrisFilter.sharedMesh = sourceFilter.sharedMesh;
        var debrisRenderer = debris.AddComponent<MeshRenderer>();
        debrisRenderer.sharedMaterials = renderer.sharedMaterials;
        var collider = debris.AddComponent<BoxCollider>();
        collider.center = sourceFilter.sharedMesh.bounds.center;
        collider.size = sourceFilter.sharedMesh.bounds.size;
        var debrisBody = debris.AddComponent<Rigidbody>();
        debrisBody.mass = Mathf.Lerp(3f, 12f, severity);
        debrisBody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        debrisBody.velocity = body.GetPointVelocity(impactPoint) - impactVelocity.normalized * Mathf.Lerp(1f, 5f, severity);
        debrisBody.angularVelocity = UnityEngine.Random.onUnitSphere * Mathf.Lerp(3f, 12f, severity);

        renderer.enabled = false;
        separatedRenderers.Add(renderer);
        UnityEngine.Object.Destroy(debris, 25f);
        chassisDamage = Mathf.Clamp01(chassisDamage + 0.12f);
        return true;
    }

    private bool BendNearestWheel(Vector3 impactPoint, float severity)
    {
        var wheel = vehicle.GetComponentsInChildren<Renderer>(true)
            .Select(renderer => renderer ? renderer.transform : null)
            .Where(candidate => candidate && IsWheelPart(candidate))
            .OrderBy(candidate => (candidate.position - impactPoint).sqrMagnitude)
            .FirstOrDefault();
        if (!wheel) return false;

        var bentWheel = wheel.GetComponent<BeamNgBentWheelVisual>() ??
                        wheel.gameObject.AddComponent<BeamNgBentWheelVisual>();
        bentWheel.AddDamage(Mathf.Lerp(5f, 24f, Mathf.Clamp01(severity)));
        steeringDamage = Mathf.Clamp01(steeringDamage + Mathf.Lerp(0.15f, 0.45f, severity));
        return true;
    }

    private static bool IsWheelOrUtilityPart(Transform candidate)
    {
        var normalized = NormalizeName(candidate.name);
        return IsWheelPart(candidate) || normalized.Contains("collider") || normalized.Contains("shadow") ||
               normalized.Contains("seat") || normalized.Contains("steering");
    }

    private static bool IsWheelPart(Transform candidate)
    {
        var normalized = NormalizeName(candidate.name);
        return normalized.Contains("wheel") || normalized.Contains("tire") || normalized.Contains("tyre") ||
               normalized.Contains("rim");
    }

    private static bool IsBreakablePanel(Transform candidate)
    {
        var normalized = NormalizeName(candidate.name);
        return normalized.Contains("bumper") || normalized.Contains("fender") || normalized.Contains("wing") ||
               normalized.Contains("hood") || normalized.Contains("bonnet") || normalized.Contains("boot") ||
               normalized.Contains("trunk") || normalized.Contains("door") || normalized.Contains("mirror") ||
               IsWheelPart(candidate);
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

/// <summary>Guaranteed deformation fallback for vehicle visuals whose mesh data cannot be edited at runtime.</summary>
internal sealed class BeamNgRendererCrumple : MonoBehaviour
{
    private bool initialized;
    private Vector3 baseLocalPosition;
    private Vector3 baseLocalScale;
    private Vector3 permanentOffset;
    private Vector3 scaleCompression;

    internal void ApplyImpact(Vector3 worldNormal, float depth)
    {
        if (!initialized)
        {
            initialized = true;
            baseLocalPosition = transform.localPosition;
            baseLocalScale = transform.localScale;
        }

        var parent = transform.parent;
        var localNormal = parent ? parent.InverseTransformDirection(worldNormal).normalized : worldNormal.normalized;
        permanentOffset += localNormal * Mathf.Clamp(depth * 0.45f, 0.015f, 0.22f);
        permanentOffset = Vector3.ClampMagnitude(permanentOffset, 0.55f);

        var direction = new Vector3(Mathf.Abs(localNormal.x), Mathf.Abs(localNormal.y), Mathf.Abs(localNormal.z));
        scaleCompression += direction * Mathf.Clamp(depth * 0.22f, 0.01f, 0.12f);
        scaleCompression = Vector3.Min(scaleCompression, Vector3.one * 0.38f);

        transform.localPosition = baseLocalPosition + permanentOffset;
        transform.localScale = Vector3.Scale(baseLocalScale, Vector3.one - scaleCompression);
    }
}

/// <summary>
/// Lightweight node-and-spring approximation: affected mesh vertices carry velocity, spring toward a permanently
/// deformed target, and lose oscillation through damping. This is visual and intentionally does not replace Unity's
/// rigid vehicle chassis.
/// </summary>
internal sealed class BeamNgSoftBodyMesh : MonoBehaviour
{
    private MeshFilter filter;
    private Mesh mesh;
    private Vector3[] restVertices;
    private Vector3[] workingVertices;
    private Vector3[] offsets;
    private Vector3[] targetOffsets;
    private Vector3[] velocities;
    private bool[] affectedFlags;
    private readonly List<int> affectedIndices = new();
    private float activeUntil;
    private bool initialized;
    private int normalUpdateCounter;

    internal bool TryInitialize(MeshFilter target)
    {
        if (initialized) return mesh && restVertices != null;
        initialized = true;
        filter = target;

        try
        {
            mesh = filter.mesh;
            if (!mesh || mesh.vertexCount == 0 || mesh.vertexCount > 30000) return false;
            restVertices = mesh.vertices;
            workingVertices = new Vector3[restVertices.Length];
            offsets = new Vector3[restVertices.Length];
            targetOffsets = new Vector3[restVertices.Length];
            velocities = new Vector3[restVertices.Length];
            affectedFlags = new bool[restVertices.Length];
            Array.Copy(restVertices, workingVertices, restVertices.Length);
            mesh.MarkDynamic();
            enabled = false;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool ApplyImpact(Vector3 worldPoint, Vector3 worldNormal, float radiusWorld,
        float permanentDepthWorld, float elasticKick)
    {
        if (!initialized || !mesh || restVertices == null) return false;

        var scale = transform.lossyScale;
        var averageScale = Mathf.Max(0.01f,
            (Mathf.Abs(scale.x) + Mathf.Abs(scale.y) + Mathf.Abs(scale.z)) / 3f);
        var localPoint = transform.InverseTransformPoint(worldPoint);
        var localNormal = transform.InverseTransformDirection(worldNormal).normalized;
        var localRadius = radiusWorld / averageScale;

        // Vehicle colliders frequently sit well outside or beside the visual mesh origin. Snap the damage center to
        // the closest actual vertex when needed instead of silently rejecting an otherwise valid impact.
        if (mesh.bounds.SqrDistance(localPoint) > localRadius * localRadius)
        {
            var nearestIndex = -1;
            var nearestDistance = float.MaxValue;
            for (var index = 0; index < restVertices.Length; index++)
            {
                var distance = (restVertices[index] + offsets[index] - localPoint).sqrMagnitude;
                if (distance >= nearestDistance) continue;
                nearestDistance = distance;
                nearestIndex = index;
            }

            if (nearestIndex < 0 || nearestDistance > Mathf.Pow(localRadius * 4f, 2f)) return false;
            localPoint = restVertices[nearestIndex] + offsets[nearestIndex];
        }

        var localDepth = permanentDepthWorld / averageScale;
        var changed = false;
        for (var index = 0; index < restVertices.Length; index++)
        {
            var currentVertex = restVertices[index] + offsets[index];
            var distance = Vector3.Distance(currentVertex, localPoint);
            if (distance >= localRadius) continue;

            var falloff = 1f - distance / localRadius;
            falloff *= falloff;
            var permanentChange = localNormal * (localDepth * falloff);
            targetOffsets[index] += permanentChange;
            velocities[index] += localNormal * (localDepth * elasticKick * falloff * 12f);

            if (!affectedFlags[index])
            {
                affectedFlags[index] = true;
                affectedIndices.Add(index);
            }
            changed = true;
        }

        if (!changed) return false;
        activeUntil = Time.time + 3f;
        enabled = true;
        return true;
    }

    private void FixedUpdate()
    {
        if (!mesh || affectedIndices.Count == 0)
        {
            enabled = false;
            return;
        }

        var deltaTime = Mathf.Min(Time.fixedDeltaTime, 0.033f);
        var spring = Mathf.Clamp(RealisticCarCrashMod.StructureSpring.Value, 8f, 80f);
        var damping = Mathf.Clamp(RealisticCarCrashMod.StructureDamping.Value, 1f, 20f);
        var moving = false;

        foreach (var index in affectedIndices)
        {
            var acceleration = (targetOffsets[index] - offsets[index]) * spring - velocities[index] * damping;
            velocities[index] += acceleration * deltaTime;
            offsets[index] += velocities[index] * deltaTime;
            workingVertices[index] = restVertices[index] + offsets[index];

            if ((targetOffsets[index] - offsets[index]).sqrMagnitude > 0.000001f ||
                velocities[index].sqrMagnitude > 0.000001f)
                moving = true;
        }

        mesh.vertices = workingVertices;
        mesh.RecalculateBounds();
        if (++normalUpdateCounter % 3 == 0) mesh.RecalculateNormals();

        if (!moving && Time.time >= activeUntil) enabled = false;
    }
}

/// <summary>Applies a non-accumulating visual alignment correction after the vehicle animates its wheel.</summary>
internal sealed class BeamNgBentWheelVisual : MonoBehaviour
{
    private float bendDegrees;
    private float phase;
    private Quaternion previousCorrection = Quaternion.identity;
    private Vector3 healthyScale;
    private bool scaleCaptured;

    internal void AddDamage(float degrees)
    {
        if (!scaleCaptured)
        {
            scaleCaptured = true;
            healthyScale = transform.localScale;
        }
        bendDegrees = Mathf.Clamp(bendDegrees + degrees, 0f, 35f);
        if (Mathf.Approximately(phase, 0f)) phase = UnityEngine.Random.Range(0.1f, Mathf.PI * 2f);
    }

    private void LateUpdate()
    {
        transform.localRotation *= Quaternion.Inverse(previousCorrection);
        var wobble = Mathf.Sin(Time.time * 11f + phase) * bendDegrees * 0.16f;
        previousCorrection = Quaternion.Euler(bendDegrees + wobble, 0f, bendDegrees * 0.35f);
        transform.localRotation *= previousCorrection;
        if (scaleCaptured)
        {
            var collapse = Mathf.Lerp(1f, 0.62f, bendDegrees / 35f);
            transform.localScale = Vector3.Scale(healthyScale, new Vector3(1f, collapse, collapse));
        }
    }

    private void OnDisable()
    {
        transform.localRotation *= Quaternion.Inverse(previousCorrection);
        previousCorrection = Quaternion.identity;
    }
}

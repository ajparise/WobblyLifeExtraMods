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

/// <summary>A chain-lightning hitscan weapon with character, vehicle, and prop reactions.</summary>
public sealed class LightningGunMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Lightning Gun is unequipped.");
    private static float nextFireTime;

    public override string Name => "Lightning Gun";
    public override string Description =>
        "Fire branching electricity that chains through nearby Wobblies, vehicles, and physics props.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 20f, Max = 600f, Label = "Primary range")]
    public static Ref<float> Range = new(220f);

    [ModSetting(Order = 20, Min = 2f, Max = 30f, Label = "Chain distance")]
    public static Ref<float> ChainDistance = new(12f);

    [ModSetting(Order = 30, Min = 1f, Max = 12f, Label = "Maximum targets")]
    public static Ref<int> MaximumTargets = new(6);

    [ModSetting(Order = 40, Min = 2f, Max = 60f, Label = "Shock force")]
    public static Ref<float> ShockForce = new(18f);

    [ModSetting(Order = 50, Min = 0.25f, Max = 8f, Label = "Vehicle EMP duration")]
    public static Ref<float> EmpDuration = new(2.5f);

    [ModSetting(Order = 60, Min = 0.08f, Max = 2f, Label = "Fire cooldown")]
    public static Ref<float> Cooldown = new(0.35f);

    [ModSetting(Order = 70, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();

    [ModSetting(Order = 80, Label = "Chain requires line of sight")]
    public static Ref<bool> RequireLineOfSight = new(true);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("LightningGunHelp",
                "Equip, close F2, aim with the electric crosshair, and left-click. The first bolt hits the aimed surface, " +
                "then jumps to unique nearby targets. Wobblies ragdoll, cars receive an EMP jolt, and loose props launch and spin. " +
                "Physics effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("LightningGunActions",
                ActionMenu(new Button("Equip Lightning Gun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip Lightning Gun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("LightningGunStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void Equip()
    {
        WindCannonMod.Unequip();
        PropSpawnerGunMod.Unequip();
        VehicleAircraftSpawnerMod.Unequip();
        RocketLauncherMod.Unequip();
        PaintballGunMod.Unequip();
        MinecraftBuildingMod.Unequip();
        HeavyAutomaticGunMod.Unequip();
        GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip();
        LaserEyesMod.Unequip();
        CamouflagePropHuntMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        WobblyHeadHomingGunMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Lightning Gun equipped. Close F2, aim, and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Lightning Gun unequipped.";
    }

    public override void Update()
    {
        if (!EquippedState.Value || Cursor.visible) return;
        if (!GameInstance.InstanceExists || !GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter())
        {
            Unequip();
            return;
        }
        if (RapidFire.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can fire effect-enabled lightning.";
            return;
        }

        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var shooter = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the Lightning Gun.";
            return;
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.08f, Cooldown.Value);
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var hits = Physics.RaycastAll(ray, Mathf.Max(20f, Range.Value), ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        var hit = hits.FirstOrDefault(item => item.collider && !item.transform.IsChildOf(shooter.transform));
        if (!hit.collider)
        {
            CreateBolt(ray.origin, ray.origin + ray.direction * Range.Value, 0);
            Status.Value = "Lightning fired but found no conductor.";
            return;
        }

        var first = BuildTarget(hit.collider, hit.point, shooter);
        var targets = BuildChain(first, shooter);
        var previous = ray.origin;
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            CreateBolt(previous, target.Point, i);
            CreateSparks(target.Point, i);
            ApplyShock(target, shooter);
            previous = target.Point;
        }

        Status.Value = targets.Count == 1
            ? "Lightning struck 1 target."
            : $"Chain lightning struck {targets.Count} targets!";
    }

    private static List<LightningTarget> BuildChain(LightningTarget first, PlayerCharacter shooter)
    {
        var result = new List<LightningTarget> { first };
        var used = new HashSet<int> { first.Key.GetInstanceID() };
        var maxTargets = Mathf.Clamp(MaximumTargets.Value, 1, 12);

        while (result.Count < maxTargets)
        {
            var origin = result[result.Count - 1].Point;
            LightningTarget best = null;
            var bestDistance = float.MaxValue;
            foreach (var collider in Physics.OverlapSphere(origin, Mathf.Max(2f, ChainDistance.Value), ~0,
                         QueryTriggerInteraction.Ignore))
            {
                if (!collider || collider.transform.IsChildOf(shooter.transform)) continue;
                var candidate = BuildTarget(collider, collider.bounds.center, shooter);
                if (candidate == null || !candidate.IsReactive || used.Contains(candidate.Key.GetInstanceID())) continue;
                var distance = (candidate.Point - origin).sqrMagnitude;
                if (distance < 0.04f || distance >= bestDistance) continue;
                if (RequireLineOfSight.Value && IsBlocked(origin, candidate)) continue;
                best = candidate;
                bestDistance = distance;
            }
            if (best == null) break;
            used.Add(best.Key.GetInstanceID());
            result.Add(best);
        }
        return result;
    }

    private static LightningTarget BuildTarget(Collider collider, Vector3 fallbackPoint, PlayerCharacter shooter)
    {
        if (!collider) return null;
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        var character = vehicle ? null : collider.GetComponentInParent<PlayerCharacter>();
        if (character == shooter) return null;
        var body = vehicle
            ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true) ?? collider.attachedRigidbody
            : character
                ? character.GetComponentInChildren<PlayerBody>(true)?.GetRigidbody() ?? collider.attachedRigidbody
                : collider.attachedRigidbody;
        var key = vehicle ? vehicle.gameObject : character ? character.gameObject : body ? body.gameObject : collider.gameObject;
        return new LightningTarget
        {
            Key = key,
            Collider = collider,
            Vehicle = vehicle,
            Character = character,
            Body = body,
            Point = body ? body.worldCenterOfMass : fallbackPoint
        };
    }

    private static bool IsBlocked(Vector3 origin, LightningTarget target)
    {
        var offset = target.Point - origin;
        if (!Physics.Raycast(origin, offset.normalized, out var hit, offset.magnitude, ~0,
                QueryTriggerInteraction.Ignore)) return false;
        var hitTarget = BuildTarget(hit.collider, hit.point, null);
        return hitTarget == null || hitTarget.Key != target.Key;
    }

    private static void ApplyShock(LightningTarget target, PlayerCharacter shooter)
    {
        var force = Mathf.Max(2f, ShockForce.Value);
        if (target.Character)
        {
            if (target.Character.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("lightning hit");
            var ragdoll = target.Character.GetRagdollController();
            var playerBody = target.Character.GetComponentInChildren<PlayerBody>(true);
            if (ragdoll) ragdoll.Ragdoll();
            if (playerBody)
                playerBody.SetRagdollVelocity(Vector3.up * force * 0.55f + UnityEngine.Random.onUnitSphere * force * 0.2f);
            foreach (var limb in target.Character.GetComponentsInChildren<Rigidbody>(true))
                if (limb && !limb.isKinematic)
                    limb.AddTorque(UnityEngine.Random.onUnitSphere * force, ForceMode.VelocityChange);
            return;
        }

        if (!target.Body || target.Body.isKinematic) return;
        if (target.Vehicle)
        {
            var emp = target.Body.GetComponent<LightningEmpEffect>() ??
                      target.Body.gameObject.AddComponent<LightningEmpEffect>();
            emp.Configure(Mathf.Clamp(EmpDuration.Value, 0.25f, 8f));
            target.Body.AddForce(Vector3.up * force * 0.28f + UnityEngine.Random.onUnitSphere * force * 0.16f,
                ForceMode.VelocityChange);
            target.Body.AddTorque(UnityEngine.Random.onUnitSphere * force * 0.75f, ForceMode.VelocityChange);
            return;
        }

        target.Body.AddForce((Vector3.up * 0.75f + UnityEngine.Random.onUnitSphere * 0.55f).normalized * force,
            ForceMode.VelocityChange);
        target.Body.AddTorque(UnityEngine.Random.onUnitSphere * force * 1.5f, ForceMode.VelocityChange);
    }

    private static void CreateBolt(Vector3 start, Vector3 end, int chainIndex)
    {
        var root = new GameObject("ExtraMods Lightning Bolt");
        var line = root.AddComponent<LineRenderer>();
        const int points = 10;
        line.positionCount = points;
        line.useWorldSpace = true;
        line.startWidth = chainIndex == 0 ? 0.095f : 0.065f;
        line.endWidth = 0.018f;
        line.startColor = Color.white;
        line.endColor = chainIndex % 2 == 0 ? new Color(0.05f, 0.65f, 1f, 1f) :
            new Color(1f, 0.9f, 0.15f, 1f);
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader) line.material = new Material(shader);

        var direction = end - start;
        var right = Vector3.Cross(direction.normalized, Vector3.up);
        if (right.sqrMagnitude < 0.01f) right = Vector3.right;
        var up = Vector3.Cross(right, direction.normalized).normalized;
        right.Normalize();
        for (var i = 0; i < points; i++)
        {
            var progress = i / (points - 1f);
            var taper = Mathf.Sin(progress * Mathf.PI);
            var jitter = (right * UnityEngine.Random.Range(-0.28f, 0.28f) +
                          up * UnityEngine.Random.Range(-0.28f, 0.28f)) * taper;
            line.SetPosition(i, Vector3.Lerp(start, end, progress) + jitter);
        }
        UnityEngine.Object.Destroy(root, 0.18f);
    }

    private static void CreateSparks(Vector3 point, int colorOffset)
    {
        for (var i = 0; i < 8; i++)
        {
            var spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            spark.name = "Lightning Spark";
            var collider = spark.GetComponent<Collider>();
            if (collider) collider.enabled = false;
            spark.transform.position = point + UnityEngine.Random.insideUnitSphere * 0.18f;
            spark.transform.localScale = Vector3.one * UnityEngine.Random.Range(0.035f, 0.09f);
            spark.GetComponent<Renderer>().material.color = (i + colorOffset) % 2 == 0
                ? new Color(0.1f, 0.75f, 1f, 1f)
                : new Color(1f, 0.9f, 0.15f, 1f);
            var body = spark.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.velocity = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(2f, 6f);
            UnityEngine.Object.Destroy(spark, 0.35f);
        }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = Mathf.Sin(Time.unscaledTime * 12f) > 0f
            ? new Color(0.1f, 0.75f, 1f, 1f)
            : new Color(1f, 0.9f, 0.12f, 1f);
        GUI.DrawTexture(new Rect(x - 19f, y - 2f, 13f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 6f, y - 2f, 13f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 19f, 4f, 13f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 6f, 4f, 13f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.color = old;
    }

    private sealed class LightningTarget
    {
        internal GameObject Key;
        internal Collider Collider;
        internal PlayerVehicle Vehicle;
        internal PlayerCharacter Character;
        internal Rigidbody Body;
        internal Vector3 Point;
        internal bool IsReactive => Vehicle || Character || (Body && !Body.isKinematic);
    }
}

internal sealed class LightningEmpEffect : MonoBehaviour
{
    private Rigidbody body;
    private float expiresAt;

    internal void Configure(float duration)
    {
        body = GetComponent<Rigidbody>();
        expiresAt = Mathf.Max(expiresAt, Time.time + duration);
    }

    private void FixedUpdate()
    {
        if (!body || Time.time >= expiresAt)
        {
            Destroy(this);
            return;
        }
        body.velocity *= 0.91f;
        body.angularVelocity *= 0.88f;
    }
}

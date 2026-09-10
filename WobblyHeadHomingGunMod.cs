using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FMODUnity;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>A visible head-shaped launcher whose head projectiles use limited-turn predictive homing.</summary>
public sealed class WobblyHeadHomingGunMod : BaseMod
{
    private const string NativeShotEvent = "event:/Objects/Objects_PaperCannon";
    private static readonly Vector3 HandOffset = new(0f, 0.2f, 0.16f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Wobbly Head Homing Gun is unequipped.");
    private static GameObject gunRoot;
    private static Transform muzzle;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextFireTime;
    private static float recoilUntil;

    public override string Name => "Wobbly Head Homing Gun";
    public override string Description =>
        "Hold a Wobbly-head-shaped gun that fires smaller heads with smooth predictive homing.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 20f, Max = 250f, Label = "Projectile speed")]
    public static Ref<float> ProjectileSpeed = new(72f);

    [ModSetting(Order = 20, Min = 30f, Max = 720f, Label = "Homing turn speed")]
    public static Ref<float> TurnSpeed = new(190f);

    [ModSetting(Order = 30, Min = 10f, Max = 400f, Label = "Target search range")]
    public static Ref<float> TargetRange = new(180f);

    [ModSetting(Order = 40, Min = 5f, Max = 180f, Label = "Target lock angle")]
    public static Ref<float> LockAngle = new(75f);

    [ModSetting(Order = 50, Min = 0f, Max = 1f, Label = "Movement prediction")]
    public static Ref<float> Prediction = new(0.65f);

    [ModSetting(Order = 55, Min = 2f, Max = 40f, Label = "Maximum prop size",
        Description = "Oversized physics mechanisms and world geometry are never selected by homing.")]
    public static Ref<float> MaximumPropSize = new(14f);

    [ModSetting(Order = 60, Min = 2f, Max = 45f, Label = "Impact force")]
    public static Ref<float> ImpactForce = new(17f);

    [ModSetting(Order = 70, Min = 0.05f, Max = 1f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.18f);

    [ModSetting(Order = 80, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();

    [ModSetting(Order = 90, Label = "Require visible target")]
    public static Ref<bool> RequireLineOfSight = new(true);

    [ModSetting(Order = 100, Min = 0f, Max = 1f, Label = "Head color hue")]
    public static Ref<float> HeadHue = new(0.08f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("WobblyHeadGunHelp",
                "Equip and close F2. The gun in your hand looks like a large Wobbly head and fires smaller Wobbly heads. " +
                "Each projectile selects the closest visible Wobbly, car, or movable prop inside the lock cone, predicts its " +
                "movement, and turns gradually instead of snapping. Hold left-click when Rapid fire is enabled."),
            base.BuildPanel(id),
            new HStack("WobblyHeadGunActions",
                ActionMenu(new Button("Equip head gun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip head gun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("WobblyHeadGunStatus", "").WithText(Status));
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
        LightningGunMod.Unequip();
        EquippedState.Value = true;
        EnsureGunModel();
        Status.Value = "Wobbly Head Homing Gun equipped. Close F2 and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (gunRoot) Object.Destroy(gunRoot);
        gunRoot = null;
        muzzle = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Wobbly Head Homing Gun unequipped.";
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;
        if (!HasLocalPlayer())
        {
            Unequip();
            return;
        }
        EnsureGunModel();
        UpdateGunModel();
        if (Cursor.visible) return;
        if (RapidFire.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can fire homing heads.";
            return;
        }

        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var shooter = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the head gun.";
            return;
        }

        EnsureGunModel();
        nextFireTime = Time.unscaledTime + Mathf.Max(0.05f, FireInterval.Value);
        recoilUntil = Time.unscaledTime + 0.08f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var position = muzzle ? muzzle.position : ray.origin + ray.direction * 1.4f;
        var target = FindBestTarget(position, ray.direction, shooter);
        SpawnHeadProjectile(position, ray.direction, shooter, target);
        PlayShot(position);
        Status.Value = target != null
            ? $"Homing head locked onto {CleanName(target.Name)}."
            : "Head fired without a target lock.";
    }

    private static HomingTarget FindBestTarget(Vector3 origin, Vector3 forward, PlayerCharacter shooter)
    {
        var maxRange = Mathf.Max(10f, TargetRange.Value);
        var maxAngle = Mathf.Clamp(LockAngle.Value, 5f, 180f);
        var seen = new HashSet<int>();
        HomingTarget best = null;
        var bestDistance = float.MaxValue;

        foreach (var collider in Physics.OverlapSphere(origin, maxRange, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!collider || collider.transform.IsChildOf(shooter.transform)) continue;
            var candidate = BuildTarget(collider);
            if (candidate == null || !seen.Add(candidate.Key.GetInstanceID())) continue;
            var offset = candidate.Position - origin;
            var distance = offset.magnitude;
            if (distance < 1f || distance >= bestDistance || Vector3.Angle(forward, offset) > maxAngle) continue;
            if (RequireLineOfSight.Value && IsBlocked(origin, candidate, shooter)) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    private static HomingTarget BuildTarget(Collider collider)
    {
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        var character = vehicle ? null : collider.GetComponentInParent<PlayerCharacter>();
        var body = vehicle
            ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true) ?? collider.attachedRigidbody
            : character
                ? character.GetComponentInChildren<PlayerBody>(true)?.GetRigidbody() ?? collider.attachedRigidbody
                : collider.attachedRigidbody;
        if (!vehicle && !character && (!body || body.isKinematic || !IsValidMovableProp(body))) return null;
        var key = vehicle ? vehicle.gameObject : character ? character.gameObject : body.gameObject;
        return new HomingTarget
        {
            Key = key,
            Body = body,
            AimTransform = body ? body.transform : key.transform,
            Name = key.name
        };
    }

    private static bool IsValidMovableProp(Rigidbody body)
    {
        if (!body || body.isKinematic || IsWorldGeometryHierarchy(body.transform)) return false;
        var bounds = CalculateTargetBounds(body.gameObject);
        var largestDimension = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
        return largestDimension <= Mathf.Max(2f, MaximumPropSize.Value);
    }

    private static bool IsWorldGeometryHierarchy(Transform transform)
    {
        for (var current = transform; current; current = current.parent)
        {
            var value = current.name.ToLowerInvariant();
            if (value.Contains("building") || value.Contains("house") || value.Contains("terrain") ||
                value.Contains("mountain") || value.Contains("road") || value.Contains("bridge") ||
                value.Contains("island") || value.Contains("world") || value.Contains("level geometry"))
                return true;
        }
        return false;
    }

    private static Bounds CalculateTargetBounds(GameObject target)
    {
        var found = false;
        var bounds = new Bounds(target.transform.position, Vector3.zero);
        foreach (var collider in target.GetComponentsInChildren<Collider>(true))
        {
            if (!collider || collider.isTrigger) continue;
            if (!found)
            {
                bounds = collider.bounds;
                found = true;
            }
            else bounds.Encapsulate(collider.bounds);
        }
        if (found) return bounds;
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer) continue;
            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else bounds.Encapsulate(renderer.bounds);
        }
        return bounds;
    }

    private static bool IsBlocked(Vector3 origin, HomingTarget target, PlayerCharacter shooter)
    {
        var offset = target.Position - origin;
        var hits = Physics.RaycastAll(origin, offset.normalized, offset.magnitude, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider) continue;
            if (hit.transform.IsChildOf(shooter.transform)) continue;
            var hitTarget = BuildTarget(hit.collider);
            return hitTarget == null || hitTarget.Key != target.Key;
        }
        return false;
    }

    private static void SpawnHeadProjectile(Vector3 position, Vector3 direction, PlayerCharacter shooter, HomingTarget target)
    {
        var projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        projectile.name = "ExtraMods Homing Wobbly Head";
        projectile.transform.position = position;
        projectile.transform.rotation = Quaternion.LookRotation(direction);
        projectile.transform.localScale = Vector3.one * 0.52f;
        SetPartColor(projectile, SkinColor());
        CreateFace(projectile.transform, 1f, true);

        var body = projectile.AddComponent<Rigidbody>();
        body.mass = 0.35f;
        body.useGravity = false;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.velocity = direction.normalized * Mathf.Max(20f, ProjectileSpeed.Value);

        var homing = projectile.AddComponent<WobblyHeadProjectile>();
        homing.Initialize(shooter, target?.AimTransform, target?.Body);
        var projectileCollider = projectile.GetComponent<Collider>();
        foreach (var collider in shooter.GetComponentsInChildren<Collider>(true))
            if (projectileCollider && collider) Physics.IgnoreCollision(projectileCollider, collider, true);
        Object.Destroy(projectile, 12f);
    }

    internal static void HandleImpact(Collision collision, PlayerCharacter shooter, GameObject projectile)
    {
        if (!collision.collider) return;
        var point = collision.contactCount > 0 ? collision.GetContact(0).point : projectile.transform.position;
        var character = collision.collider.GetComponentInParent<PlayerCharacter>();
        if (character && character != shooter)
        {
            if (character.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("homing head hit");
            var ragdoll = character.GetRagdollController();
            var body = character.GetComponentInChildren<PlayerBody>(true);
            if (ragdoll) ragdoll.Ragdoll();
            if (body)
            {
                var direction = (body.transform.position - point).normalized;
                body.SetRagdollVelocity(direction * ImpactForce.Value * 0.6f + Vector3.up * ImpactForce.Value * 0.55f);
            }
        }
        else
        {
            var vehicle = collision.collider.GetComponentInParent<PlayerVehicle>();
            if (vehicle)
            {
                CreateVehicleExplosion(point, vehicle);
                vehicle.DestroyGameObject();
                Status.Value = "Homing head instantly exploded the vehicle; rusty wreck skipped.";
                CreateHeadBurst(point);
                return;
            }
            var body = vehicle
                ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true)
                : collision.rigidbody;
            if (body && !body.isKinematic)
            {
                var direction = (body.worldCenterOfMass - point).normalized;
                body.AddForceAtPosition((direction + Vector3.up * 0.35f).normalized * ImpactForce.Value,
                    point, ForceMode.VelocityChange);
                body.AddTorque(UnityEngine.Random.onUnitSphere * ImpactForce.Value * 0.45f,
                    ForceMode.VelocityChange);
            }
        }
        CreateHeadBurst(point);
    }

    private static void CreateVehicleExplosion(Vector3 point, PlayerVehicle vehicle)
    {
        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "Homing Head Vehicle Explosion";
        flash.transform.position = point;
        flash.transform.localScale = Vector3.one * 0.35f;
        var flashCollider = flash.GetComponent<Collider>();
        if (flashCollider) flashCollider.enabled = false;
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        var material = new Material(shader) { color = new Color(1f, 0.24f, 0.015f, 0.96f) };
        flash.GetComponent<Renderer>().material = material;
        Plugin.RunCoroutine(AnimateVehicleExplosion(flash, material));

        foreach (var nearby in Physics.OverlapSphere(point, 6f, ~0, QueryTriggerInteraction.Ignore))
        {
            var body = nearby.attachedRigidbody;
            if (!body || body.isKinematic || body.GetComponentInParent<PlayerVehicle>() == vehicle) continue;
            body.AddExplosionForce(1100f, point, 6f, 2.5f, ForceMode.Impulse);
        }
    }

    private static IEnumerator AnimateVehicleExplosion(GameObject flash, Material material)
    {
        var elapsed = 0f;
        while (flash && elapsed < 0.38f)
        {
            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / 0.38f);
            flash.transform.localScale = Vector3.one * Mathf.Lerp(0.35f, 8f, progress);
            material.color = new Color(1f, Mathf.Lerp(0.45f, 0.03f, progress), 0.01f, 1f - progress);
            yield return null;
        }
        if (flash) Object.Destroy(flash);
        if (material) Object.Destroy(material);
    }

    private static void EnsureGunModel()
    {
        var hand = ResolveRightHand();
        if (!hand) return;
        if (activeHand != hand)
        {
            if (activeHand && handPoseRequested) activeHand.ResetPointing();
            activeHand = hand;
            handPoseRequested = false;
        }
        if (!handPoseRequested)
        {
            activeHand.SetPointing(true, true);
            handPoseRequested = true;
        }
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        if (gunRoot)
        {
            if (gunRoot.transform.parent != anchor) gunRoot.transform.SetParent(anchor, true);
            return;
        }

        gunRoot = new GameObject("ExtraMods Wobbly Head Homing Gun");
        gunRoot.transform.SetParent(anchor, true);
        var head = CreatePart("Large Wobbly Head Gun", PrimitiveType.Sphere, new Vector3(0f, 0.02f, 0.35f),
            Vector3.zero, new Vector3(0.72f, 0.68f, 0.82f), SkinColor());
        CreateFace(head, 1.35f, false);
        CreatePart("Head Gun Grip", PrimitiveType.Cube, new Vector3(0f, -0.42f, 0.15f),
            new Vector3(-12f, 0f, 0f), new Vector3(0.18f, 0.5f, 0.22f), new Color(0.12f, 0.1f, 0.09f));
        muzzle = new GameObject("Head Mouth Muzzle").transform;
        muzzle.SetParent(gunRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, -0.11f, 0.92f);
    }

    private static void UpdateGunModel()
    {
        if (!gunRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var position = anchor.position + rotation * HandOffset;
        if (Time.unscaledTime < recoilUntil) position -= ray.direction * 0.12f;
        gunRoot.transform.position = Vector3.Lerp(gunRoot.transform.position, position, 0.65f);
        gunRoot.transform.rotation = Quaternion.Slerp(gunRoot.transform.rotation, rotation, 0.65f);
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!HasLocalPlayer() || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController().GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static bool HasLocalPlayer() => GameInstance.InstanceExists &&
        GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();

    private static Transform CreatePart(string name, PrimitiveType primitive, Vector3 position,
        Vector3 rotation, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        Object.Destroy(collider);
        part.transform.SetParent(gunRoot.transform, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        SetPartColor(part, color);
        return part.transform;
    }

    private static void CreateFace(Transform head, float scale, bool projectile)
    {
        CreateFacialPart(head, "Left Eye", PrimitiveType.Sphere, new Vector3(-0.18f, 0.12f, 0.43f) * scale,
            new Vector3(0.13f, 0.16f, 0.07f) * scale, Color.white);
        CreateFacialPart(head, "Right Eye", PrimitiveType.Sphere, new Vector3(0.18f, 0.12f, 0.43f) * scale,
            new Vector3(0.13f, 0.16f, 0.07f) * scale, Color.white);
        CreateFacialPart(head, "Left Pupil", PrimitiveType.Sphere, new Vector3(-0.18f, 0.12f, 0.49f) * scale,
            Vector3.one * 0.065f * scale, new Color(0.03f, 0.03f, 0.04f));
        CreateFacialPart(head, "Right Pupil", PrimitiveType.Sphere, new Vector3(0.18f, 0.12f, 0.49f) * scale,
            Vector3.one * 0.065f * scale, new Color(0.03f, 0.03f, 0.04f));
        CreateFacialPart(head, projectile ? "Projectile Mouth" : "Gun Mouth", PrimitiveType.Sphere,
            new Vector3(0f, -0.18f, 0.45f) * scale, new Vector3(0.18f, 0.1f, 0.055f) * scale,
            new Color(0.12f, 0.025f, 0.025f));
    }

    private static void CreateFacialPart(Transform parent, string name, PrimitiveType primitive,
        Vector3 position, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        Object.Destroy(collider);
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        SetPartColor(part, color);
    }

    private static void SetPartColor(GameObject part, Color color)
    {
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    private static Color SkinColor() => Color.HSVToRGB(Mathf.Repeat(HeadHue.Value, 1f), 0.42f, 1f);

    private static void PlayShot(Vector3 position)
    {
        try
        {
            var shot = RuntimeManager.CreateInstance(NativeShotEvent);
            shot.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            shot.setVolume(0.7f);
            shot.start();
            shot.release();
        }
        catch (Exception exception)
        {
            Plugin.Log?.LogWarning($"Head gun sound failed: {exception.Message}");
        }
    }

    private static void CreateHeadBurst(Vector3 point)
    {
        for (var i = 0; i < 10; i++)
        {
            var piece = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            piece.name = "Wobbly Head Impact Pop";
            piece.transform.position = point + UnityEngine.Random.insideUnitSphere * 0.2f;
            piece.transform.localScale = Vector3.one * UnityEngine.Random.Range(0.04f, 0.1f);
            var collider = piece.GetComponent<Collider>();
            if (collider) collider.enabled = false;
            SetPartColor(piece, i % 3 == 0 ? Color.white : SkinColor());
            var body = piece.AddComponent<Rigidbody>();
            body.velocity = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(2f, 6f);
            Object.Destroy(piece, 0.8f);
        }
    }

    private static string CleanName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "moving target" : value.Replace("(Clone)", string.Empty).Trim();

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = SkinColor();
        GUI.DrawTexture(new Rect(x - 18f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 6f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 18f, 4f, 12f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 6f, 4f, 12f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(x - 5f, y - 4f, 3f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 2f, y - 4f, 3f, 3f), Texture2D.whiteTexture);
        GUI.color = old;
    }

    private sealed class HomingTarget
    {
        internal GameObject Key;
        internal Rigidbody Body;
        internal Transform AimTransform;
        internal string Name;
        internal Vector3 Position => Body ? Body.worldCenterOfMass : AimTransform.position;
    }
}

internal sealed class WobblyHeadProjectile : MonoBehaviour
{
    private PlayerCharacter shooter;
    private Transform target;
    private Rigidbody targetBody;
    private Rigidbody body;
    private bool hit;

    internal void Initialize(PlayerCharacter owner, Transform targetTransform, Rigidbody movingTarget)
    {
        shooter = owner;
        target = targetTransform;
        targetBody = movingTarget;
        body = GetComponent<Rigidbody>();
    }

    private void FixedUpdate()
    {
        if (!body || !target) return;
        var speed = Mathf.Max(20f, WobblyHeadHomingGunMod.ProjectileSpeed.Value);
        var targetVelocity = targetBody ? targetBody.velocity : Vector3.zero;
        var distance = Vector3.Distance(body.position, target.position);
        var flightTime = Mathf.Clamp(distance / speed, 0f, 2f);
        var predicted = target.position + targetVelocity * flightTime *
            Mathf.Clamp01(WobblyHeadHomingGunMod.Prediction.Value);
        var desired = (predicted - body.position).normalized;
        if (desired.sqrMagnitude < 0.01f) return;
        var current = body.velocity.sqrMagnitude > 0.1f ? body.velocity.normalized : transform.forward;
        var direction = Vector3.RotateTowards(current, desired,
            Mathf.Deg2Rad * WobblyHeadHomingGunMod.TurnSpeed.Value * Time.fixedDeltaTime, 0f).normalized;
        body.velocity = direction * speed;
        body.MoveRotation(Quaternion.LookRotation(direction, Vector3.up));
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (hit) return;
        hit = true;
        WobblyHeadHomingGunMod.HandleImpact(collision, shooter, gameObject);
        Destroy(gameObject);
    }
}

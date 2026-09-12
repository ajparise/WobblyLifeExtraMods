using System;
using System.Collections.Generic;
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

/// <summary>An unlimited-ammo launcher for giant peel projectiles that become high-speed slip traps.</summary>
public sealed class BananaPeelLauncherMod : BaseMod
{
    private const string NativeShotEvent = "event:/Objects/Objects_PaperCannon";
    private const float NormalPeelSize = 0.3f;
    private const float NormalSlipSpeed = 6f;
    private static readonly Vector3 HandOffset = new(0f, 0.2f, 0.12f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Banana Peel Launcher is unequipped.");
    private static GameObject gunRoot;
    private static Transform muzzle;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextFireTime;
    private static float recoilUntil;

    public override string Name => "Banana Peel Launcher";
    public override string Description =>
        "Fire unlimited giant banana peels that make Wobblies, vehicles, and physics props slip at extreme speed.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 1f, Max = 15f, Label = "Peel size multiplier")]
    public static Ref<float> PeelSizeMultiplier = new(10f);

    [ModSetting(Order = 20, Min = 1f, Max = 15f, Label = "Slip speed multiplier")]
    public static Ref<float> SlipSpeedMultiplier = new(10f);

    [ModSetting(Order = 30, Min = 10f, Max = 150f, Label = "Launch speed")]
    public static Ref<float> LaunchSpeed = new(48f);

    [ModSetting(Order = 40, Min = 5f, Max = 180f, Label = "Peel trap lifetime")]
    public static Ref<float> TrapLifetime = new(60f);

    [ModSetting(Order = 50, Min = 0.05f, Max = 1f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.2f);

    [ModSetting(Order = 60, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();

    [ModSetting(Order = 70, Min = 0f, Max = 1f, Label = "Banana ripeness")]
    public static Ref<float> Ripeness = new(0.75f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("BananaPeelLauncherHelp",
                "Equip and close F2. The launcher has a banana peel mounted at its muzzle and unlimited ammunition. " +
                "Left-click launches a peel; enable Rapid fire to hold the trigger. Peels default to 10x normal size and " +
                "10x normal slip speed. A direct hit slips its target immediately, while a ground hit becomes a temporary trap."),
            base.BuildPanel(id),
            new HStack("BananaPeelLauncherActions",
                ActionMenu(new Button("Equip peel launcher", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip peel launcher", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("BananaPeelLauncherStatus", "").WithText(Status));
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
        WobblyHeadHomingGunMod.Unequip();
        TornadoGunMod.Unequip();
        FireworkMinigunMod.Unequip();
        JellyGunMod.Unequip();
        EquippedState.Value = true;
        EnsureGunModel();
        Status.Value = "Banana Peel Launcher equipped. Ammo: unlimited.";
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
        Status.Value = "Banana Peel Launcher unequipped.";
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
            Status.Value = "Only the offline player or lobby host can fire physics-enabled peels.";
            return;
        }

        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var shooter = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the Banana Peel Launcher.";
            return;
        }

        EnsureGunModel();
        nextFireTime = Time.unscaledTime + Mathf.Max(0.05f, FireInterval.Value);
        recoilUntil = Time.unscaledTime + 0.08f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var size = Mathf.Clamp(PeelSizeMultiplier.Value, 1f, 15f);
        var spawnDistance = NormalPeelSize * size + 1.2f;
        var position = muzzle ? muzzle.position + ray.direction * NormalPeelSize * size :
            ray.origin + ray.direction * spawnDistance;
        SpawnPeel(position, ray.direction, shooter, size);
        PlayShot(position);
        Status.Value = $"Giant peel fired at {SlipSpeedMultiplier.Value:0.#}x slip speed. Ammo: unlimited.";
    }

    private static void SpawnPeel(Vector3 position, Vector3 direction, PlayerCharacter shooter, float sizeMultiplier)
    {
        var root = new GameObject("ExtraMods Giant Banana Peel");
        root.transform.position = position;
        root.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        BuildPeelVisual(root.transform, sizeMultiplier, true);

        var collider = root.AddComponent<SphereCollider>();
        collider.radius = NormalPeelSize * sizeMultiplier * 0.55f;
        var body = root.AddComponent<Rigidbody>();
        body.mass = Mathf.Max(0.15f, sizeMultiplier * 0.08f);
        body.useGravity = true;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.velocity = direction.normalized * Mathf.Max(10f, LaunchSpeed.Value);
        body.angularVelocity = UnityEngine.Random.onUnitSphere * 6f;

        var behavior = root.AddComponent<BananaPeelProjectile>();
        behavior.Initialize(shooter, direction, Mathf.Clamp(TrapLifetime.Value, 5f, 180f));
        Object.Destroy(root, Mathf.Clamp(TrapLifetime.Value, 5f, 180f) + 12f);
    }

    internal static bool ApplySlip(Collider collider, Vector3 fallbackDirection, PlayerCharacter shooter)
    {
        if (!collider) return false;
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        var character = vehicle ? null : collider.GetComponentInParent<PlayerCharacter>();
        if (character == shooter) return false;
        var speed = NormalSlipSpeed * Mathf.Clamp(SlipSpeedMultiplier.Value, 1f, 15f);

        if (character)
        {
            if (character.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("banana peel slip");
            var ragdoll = character.GetRagdollController();
            var playerBody = character.GetComponentInChildren<PlayerBody>(true);
            var body = playerBody ? playerBody.GetRigidbody() : collider.attachedRigidbody;
            var direction = SlipDirection(body, fallbackDirection);
            if (ragdoll) ragdoll.Ragdoll();
            if (playerBody) playerBody.SetRagdollVelocity(direction * speed + Vector3.up * Mathf.Min(8f, speed * 0.12f));
            Status.Value = $"Wobbly slipped at {SlipSpeedMultiplier.Value:0.#}x speed!";
            return true;
        }

        var physicsBody = vehicle
            ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true) ?? collider.attachedRigidbody
            : collider.attachedRigidbody;
        if (!physicsBody || physicsBody.isKinematic) return false;
        var slipDirection = SlipDirection(physicsBody, fallbackDirection);
        physicsBody.WakeUp();
        physicsBody.AddForce(slipDirection * speed + Vector3.up * Mathf.Min(5f, speed * 0.06f),
            ForceMode.VelocityChange);
        physicsBody.AddTorque(UnityEngine.Random.onUnitSphere * speed * (vehicle ? 0.35f : 0.65f),
            ForceMode.VelocityChange);
        Status.Value = vehicle
            ? $"Vehicle slipped at {SlipSpeedMultiplier.Value:0.#}x speed!"
            : $"Physics prop slipped at {SlipSpeedMultiplier.Value:0.#}x speed!";
        return true;
    }

    private static Vector3 SlipDirection(Rigidbody body, Vector3 fallback)
    {
        var direction = body ? Vector3.ProjectOnPlane(body.velocity, Vector3.up) : Vector3.zero;
        if (direction.sqrMagnitude < 1f) direction = Vector3.ProjectOnPlane(fallback, Vector3.up);
        if (direction.sqrMagnitude < 0.01f) direction = UnityEngine.Random.insideUnitSphere;
        return Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
    }

    internal static int TargetId(Collider collider)
    {
        if (!collider) return 0;
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        if (vehicle) return vehicle.gameObject.GetInstanceID();
        var character = collider.GetComponentInParent<PlayerCharacter>();
        if (character) return character.gameObject.GetInstanceID();
        return collider.attachedRigidbody ? collider.attachedRigidbody.gameObject.GetInstanceID() : 0;
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

        gunRoot = new GameObject("ExtraMods Banana Peel Launcher");
        gunRoot.transform.SetParent(anchor, true);
        CreateGunPart("Banana Receiver", PrimitiveType.Cube, new Vector3(0f, 0f, 0.2f), Vector3.zero,
            new Vector3(0.28f, 0.24f, 0.95f), new Color(0.95f, 0.7f, 0.05f));
        CreateGunPart("Green Barrel", PrimitiveType.Cylinder, new Vector3(0f, 0.01f, 0.78f),
            new Vector3(90f, 0f, 0f), new Vector3(0.13f, 0.46f, 0.13f), new Color(0.28f, 0.48f, 0.08f));
        CreateGunPart("Launcher Grip", PrimitiveType.Cube, new Vector3(0f, -0.28f, 0f),
            new Vector3(-15f, 0f, 0f), new Vector3(0.16f, 0.42f, 0.2f), new Color(0.14f, 0.1f, 0.05f));
        var muzzlePeel = new GameObject("Muzzle Banana Peel");
        muzzlePeel.transform.SetParent(gunRoot.transform, false);
        muzzlePeel.transform.localPosition = new Vector3(0f, 0.02f, 1.18f);
        muzzlePeel.transform.localRotation = Quaternion.Euler(78f, 0f, 0f);
        BuildPeelVisual(muzzlePeel.transform, 1.5f, false);
        muzzle = new GameObject("Banana Muzzle").transform;
        muzzle.SetParent(gunRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, 0.02f, 1.55f);
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

    private static void BuildPeelVisual(Transform root, float multiplier, bool projectile)
    {
        var yellow = BananaColor();
        CreatePeelPart(root, "Banana Center", PrimitiveType.Sphere, Vector3.zero,
            Vector3.zero, Vector3.one * NormalPeelSize * multiplier * 0.42f, yellow);
        for (var i = 0; i < 4; i++)
        {
            var angle = i * 90f;
            var radians = angle * Mathf.Deg2Rad;
            var radial = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            CreatePeelPart(root, $"Banana Peel Strip {i + 1}", PrimitiveType.Capsule,
                radial * NormalPeelSize * multiplier * 0.38f - Vector3.up * NormalPeelSize * multiplier * 0.16f,
                new Vector3(48f, angle, 0f),
                new Vector3(NormalPeelSize * multiplier * 0.18f, NormalPeelSize * multiplier * 0.5f,
                    NormalPeelSize * multiplier * 0.18f), yellow);
        }
        if (projectile)
            CreatePeelPart(root, "Banana Stem", PrimitiveType.Cylinder,
                Vector3.up * NormalPeelSize * multiplier * 0.28f, Vector3.zero,
                new Vector3(NormalPeelSize * multiplier * 0.08f, NormalPeelSize * multiplier * 0.22f,
                    NormalPeelSize * multiplier * 0.08f), new Color(0.25f, 0.18f, 0.04f));
    }

    private static void CreatePeelPart(Transform parent, string name, PrimitiveType primitive,
        Vector3 position, Vector3 rotation, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        Object.Destroy(collider);
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    private static void CreateGunPart(string name, PrimitiveType primitive, Vector3 position,
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
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    private static Color BananaColor()
    {
        var ripe = Mathf.Clamp01(Ripeness.Value);
        return Color.Lerp(new Color(0.35f, 0.65f, 0.08f), new Color(1f, 0.73f, 0.03f), ripe);
    }

    private static void PlayShot(Vector3 position)
    {
        try
        {
            var shot = RuntimeManager.CreateInstance(NativeShotEvent);
            shot.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            shot.setVolume(0.75f);
            shot.start();
            shot.release();
        }
        catch (Exception exception)
        {
            Plugin.Log?.LogWarning($"Banana launcher sound failed: {exception.Message}");
        }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = BananaColor();
        GUI.DrawTexture(new Rect(x - 18f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 6f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 18f, 4f, 12f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 6f, 4f, 12f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class BananaPeelProjectile : MonoBehaviour
{
    private readonly HashSet<int> affectedTargets = new();
    private readonly List<Collider> ignoredShooterColliders = new();
    private PlayerCharacter shooter;
    private Rigidbody body;
    private SphereCollider peelCollider;
    private Vector3 launchDirection;
    private float armedAt;
    private float lifetime;
    private bool trap;

    internal void Initialize(PlayerCharacter owner, Vector3 direction, float trapLifetime)
    {
        shooter = owner;
        launchDirection = direction.normalized;
        lifetime = trapLifetime;
        body = GetComponent<Rigidbody>();
        peelCollider = GetComponent<SphereCollider>();
        foreach (var collider in owner.GetComponentsInChildren<Collider>(true))
        {
            if (!collider || !peelCollider) continue;
            Physics.IgnoreCollision(peelCollider, collider, true);
            ignoredShooterColliders.Add(collider);
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (trap || !collision.collider) return;
        var targetId = BananaPeelLauncherMod.TargetId(collision.collider);
        var slipped = BananaPeelLauncherMod.ApplySlip(collision.collider, body ? body.velocity : launchDirection, shooter);
        if (slipped)
        {
            if (targetId != 0) affectedTargets.Add(targetId);
            Destroy(gameObject, 0.12f);
            return;
        }

        var contact = collision.contactCount > 0 ? collision.GetContact(0) : default;
        transform.position = collision.contactCount > 0 ? contact.point + contact.normal * 0.08f : transform.position;
        if (collision.contactCount > 0)
            transform.rotation = Quaternion.FromToRotation(Vector3.up, contact.normal);
        if (body)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }
        if (peelCollider) peelCollider.isTrigger = true;
        trap = true;
        armedAt = Time.time + 0.4f;
        Destroy(gameObject, lifetime);
    }

    private void Update()
    {
        if (!trap || Time.time < armedAt || ignoredShooterColliders.Count == 0) return;
        foreach (var collider in ignoredShooterColliders)
            if (peelCollider && collider) Physics.IgnoreCollision(peelCollider, collider, false);
        ignoredShooterColliders.Clear();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!trap || Time.time < armedAt || !other) return;
        var targetId = BananaPeelLauncherMod.TargetId(other);
        if (targetId == 0 || affectedTargets.Contains(targetId)) return;
        if (BananaPeelLauncherMod.ApplySlip(other, launchDirection, null)) affectedTargets.Add(targetId);
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using FMODUnity;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>A fully automatic launcher with physical rockets and the game's native firework bursts.</summary>
public sealed class FireworkMinigunMod : BaseMod
{
    private const string NativeShotEvent = "event:/Objects/Objects_PaperCannon";
    private static readonly string[] NativeFireworks =
    {
        "Assets/Content/Game/Prefabs/Particles/Fireworks/Fireworks Blue.prefab",
        "Assets/Content/Game/Prefabs/Particles/Fireworks/Fireworks Green.prefab",
        "Assets/Content/Game/Prefabs/Particles/Fireworks/Fireworks Purple.prefab",
        "Assets/Content/Game/Prefabs/Particles/Fireworks/Fireworks Red.prefab"
    };
    private static readonly Color[] FireworkColors =
    {
        new(0.12f, 0.55f, 1f), new(0.15f, 1f, 0.32f), new(0.7f, 0.18f, 1f), new(1f, 0.12f, 0.08f)
    };
    private static readonly Vector3 HandOffset = new(0f, 0.2f, 0.2f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Firework Minigun is unequipped.");
    private static readonly List<FireworkRocket> ActiveRockets = new();
    private static GameObject gunRoot;
    private static Transform muzzle;
    private static Transform barrelRotor;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextFireTime;
    private static float recoilUntil;

    public override string Name => "Firework Minigun";
    public override string Description =>
        "Rapid-fire unlimited rockets with native Wobbly Life firework bursts that launch nearby targets skyward.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 25f, Max = 180f, Label = "Rocket speed")]
    public static Ref<float> RocketSpeed = new(85f);
    [ModSetting(Order = 20, Min = 0.04f, Max = 0.5f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.09f);
    [ModSetting(Order = 30, Min = 0.4f, Max = 8f, Label = "Rocket fuse")]
    public static Ref<float> Fuse = new(2.2f);
    [ModSetting(Order = 40, Min = 1f, Max = 15f, Label = "Blast radius")]
    public static Ref<float> BlastRadius = new(6f);
    [ModSetting(Order = 50, Min = 5f, Max = 100f, Label = "Sky launch strength")]
    public static Ref<float> LaunchStrength = new(42f);
    [ModSetting(Order = 60, Label = "Explode on impact")]
    public static Ref<bool> ImpactDetonation = new(true);
    [ModSetting(Order = 70, Label = "Random firework colors")]
    public static Ref<bool> RandomColors = new(true);
    [ModSetting(Order = 80, Min = 5f, Max = 80f, Label = "Maximum active rockets")]
    public static Ref<int> MaximumRockets = new(45);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("FireworkMinigunHelp",
                "Equip and close F2, then hold left-click for fully automatic fire with unlimited ammunition. Rockets use " +
                "Wobbly Life's built-in blue, green, purple, and red firework prefabs. Impact or fuse explosions launch " +
                "Wobblies, vehicles, and movable physics props into the sky while ignoring the firing player. Host/offline only."),
            base.BuildPanel(id),
            new HStack("FireworkMinigunActions",
                ActionMenu(new Button("Equip Firework Minigun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip Firework Minigun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire)),
                ActionMenu(new Button("Clear rockets", ClearRockets), nameof(ClearRockets))
            ).WithContentWidth(),
            new TextWrapped("FireworkMinigunStatus", "").WithText(Status));
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
        BananaPeelLauncherMod.Unequip();
        TornadoGunMod.Unequip();
        JellyGunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip();
        EquippedState.Value = true;
        EnsureGunModel();
        Status.Value = "Firework Minigun equipped. Ammo: unlimited. Hold left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (gunRoot) Object.Destroy(gunRoot);
        gunRoot = null;
        muzzle = null;
        barrelRotor = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Firework Minigun unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearRockets()
    {
        for (var i = ActiveRockets.Count - 1; i >= 0; i--)
            if (ActiveRockets[i]) Object.Destroy(ActiveRockets[i].gameObject);
        ActiveRockets.Clear();
        Status.Value = "All active firework rockets cleared.";
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;
        if (!HasLocalPlayer())
        {
            Unequip();
            ClearRockets();
            return;
        }
        EnsureGunModel();
        UpdateGunModel();
        if (Cursor.visible) return;
        if (Input.GetMouseButton(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can fire physics-enabled fireworks.";
            return;
        }
        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var shooter = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the Firework Minigun.";
            return;
        }

        PruneRockets();
        var limit = Mathf.Clamp(MaximumRockets.Value, 5, 80);
        while (ActiveRockets.Count >= limit)
        {
            var oldest = ActiveRockets[0];
            ActiveRockets.RemoveAt(0);
            if (oldest) Object.Destroy(oldest.gameObject);
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.04f, FireInterval.Value);
        recoilUntil = Time.unscaledTime + 0.055f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var colorIndex = RandomColors.Value ? UnityEngine.Random.Range(0, NativeFireworks.Length) : 0;
        var position = muzzle ? muzzle.position + ray.direction * 0.3f : ray.origin + ray.direction * 1.6f;
        SpawnRocket(position, ray.direction, shooter, colorIndex);
        PlayShot(position);
        Status.Value = $"Firework fired. Active rockets: {ActiveRockets.Count}/{limit}. Ammo: unlimited.";
    }

    private static void SpawnRocket(Vector3 position, Vector3 direction, PlayerCharacter shooter, int colorIndex)
    {
        var rocket = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        rocket.name = "ExtraMods Firework Rocket";
        rocket.transform.position = position;
        rocket.transform.rotation = Quaternion.FromToRotation(Vector3.up, direction.normalized);
        rocket.transform.localScale = new Vector3(0.16f, 0.42f, 0.16f);
        var renderer = rocket.GetComponent<Renderer>();
        if (renderer) renderer.material.color = FireworkColors[colorIndex];

        var light = rocket.AddComponent<Light>();
        light.color = FireworkColors[colorIndex];
        light.range = 3.5f;
        light.intensity = 1.8f;

        var trail = rocket.AddComponent<TrailRenderer>();
        trail.time = 0.28f;
        trail.startWidth = 0.13f;
        trail.endWidth = 0.01f;
        trail.startColor = FireworkColors[colorIndex];
        trail.endColor = new Color(FireworkColors[colorIndex].r, FireworkColors[colorIndex].g,
            FireworkColors[colorIndex].b, 0f);
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader) trail.material = new Material(shader);

        var body = rocket.AddComponent<Rigidbody>();
        body.mass = 0.12f;
        body.useGravity = true;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.velocity = direction.normalized * Mathf.Max(25f, RocketSpeed.Value);
        body.angularVelocity = UnityEngine.Random.insideUnitSphere * 2f;

        var behavior = rocket.AddComponent<FireworkRocket>();
        behavior.Initialize(shooter, colorIndex, Mathf.Clamp(Fuse.Value, 0.4f, 8f));
        foreach (var shooterCollider in shooter.GetComponentsInChildren<Collider>(true))
            if (shooterCollider) Physics.IgnoreCollision(rocket.GetComponent<Collider>(), shooterCollider, true);
        ActiveRockets.Add(behavior);
        Object.Destroy(rocket, Mathf.Clamp(Fuse.Value, 0.4f, 8f) + 2f);
    }

    internal static void Detonate(FireworkRocket rocket, Vector3 point, PlayerCharacter shooter, int colorIndex)
    {
        if (!rocket) return;
        SpawnNativeFirework(point, colorIndex);
        var radius = Mathf.Clamp(BlastRadius.Value, 1f, 15f);
        var strength = Mathf.Clamp(LaunchStrength.Value, 5f, 100f);
        var processed = new HashSet<int>();
        foreach (var collider in Physics.OverlapSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!collider || (shooter && collider.transform.IsChildOf(shooter.transform))) continue;
            var vehicle = collider.GetComponentInParent<PlayerVehicle>();
            var character = vehicle ? null : collider.GetComponentInParent<PlayerCharacter>();
            if (character == shooter) continue;
            var playerBody = character ? character.GetComponentInChildren<PlayerBody>(true) : null;
            var body = vehicle
                ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true) ?? collider.attachedRigidbody
                : playerBody ? playerBody.GetRigidbody() : collider.attachedRigidbody;
            if (!body || body.isKinematic) continue;
            var id = vehicle ? vehicle.GetInstanceID() : character ? character.GetInstanceID() : body.GetInstanceID();
            if (!processed.Add(id)) continue;

            var away = Vector3.ProjectOnPlane(body.worldCenterOfMass - point, Vector3.up);
            if (away.sqrMagnitude < 0.05f) away = UnityEngine.Random.insideUnitSphere;
            var falloff = Mathf.Lerp(0.45f, 1f, 1f - Mathf.Clamp01(Vector3.Distance(body.worldCenterOfMass, point) / radius));
            var velocity = Vector3.up * strength * falloff + away.normalized * strength * 0.24f * falloff;
            if (character)
            {
                if (character.GetComponentInParent<PlayerNPCController>())
                    PoliceChaseMod.ReportNpcHarassment("firework blast");
                var ragdoll = character.GetRagdollController();
                if (ragdoll) ragdoll.Ragdoll();
                if (playerBody) playerBody.SetRagdollVelocity(velocity);
            }
            else
            {
                body.WakeUp();
                body.AddForce(velocity, ForceMode.VelocityChange);
                body.AddTorque(UnityEngine.Random.onUnitSphere * strength * 0.65f, ForceMode.VelocityChange);
            }
        }
        Unregister(rocket);
        Object.Destroy(rocket.gameObject);
    }

    private static void SpawnNativeFirework(Vector3 point, int colorIndex)
    {
        Plugin.RunCoroutine(SpawnNativeFireworkRoutine(point, Mathf.Clamp(colorIndex, 0, NativeFireworks.Length - 1)));
    }

    private static IEnumerator SpawnNativeFireworkRoutine(Vector3 point, int colorIndex)
    {
        var handle = Addressables.InstantiateAsync(NativeFireworks[colorIndex], point, Quaternion.identity);
        yield return handle;
        if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result)
            handle.Result.AddComponent<FireworkAddressableCleanup>().Configure(5f);
        else
        {
            if (handle.IsValid()) Addressables.Release(handle);
            Plugin.Log?.LogWarning($"Could not load native firework prefab: {NativeFireworks[colorIndex]}");
        }
    }

    internal static void Unregister(FireworkRocket rocket) => ActiveRockets.Remove(rocket);

    private static void PruneRockets()
    {
        for (var i = ActiveRockets.Count - 1; i >= 0; i--)
            if (!ActiveRockets[i]) ActiveRockets.RemoveAt(i);
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

        gunRoot = new GameObject("ExtraMods Firework Minigun");
        gunRoot.transform.SetParent(anchor, false);
        CreateGunPart(gunRoot.transform, "Minigun Body", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.28f),
            new Vector3(90f, 0f, 0f), new Vector3(0.3f, 0.48f, 0.3f), new Color(0.12f, 0.14f, 0.17f));
        CreateGunPart(gunRoot.transform, "Rear Firework Drum", PrimitiveType.Cylinder, new Vector3(0f, 0f, -0.2f),
            new Vector3(90f, 0f, 0f), new Vector3(0.43f, 0.24f, 0.43f), new Color(0.45f, 0.12f, 0.62f));
        CreateGunPart(gunRoot.transform, "Minigun Grip", PrimitiveType.Cube, new Vector3(0f, -0.32f, -0.04f),
            new Vector3(-14f, 0f, 0f), new Vector3(0.18f, 0.45f, 0.22f), new Color(0.08f, 0.09f, 0.1f));
        barrelRotor = new GameObject("Firework Barrel Rotor").transform;
        barrelRotor.SetParent(gunRoot.transform, false);
        for (var i = 0; i < 6; i++)
        {
            var angle = i / 6f * Mathf.PI * 2f;
            CreateGunPart(barrelRotor, $"Firework Barrel {i + 1}", PrimitiveType.Cylinder,
                new Vector3(Mathf.Cos(angle) * 0.19f, Mathf.Sin(angle) * 0.19f, 0.92f),
                new Vector3(90f, 0f, 0f), new Vector3(0.055f, 0.62f, 0.055f), FireworkColors[i % 4]);
        }
        muzzle = new GameObject("Firework Minigun Muzzle").transform;
        muzzle.SetParent(gunRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, 0f, 1.58f);
    }

    private static void UpdateGunModel()
    {
        if (!gunRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var position = anchor.position + rotation * HandOffset;
        if (Time.unscaledTime < recoilUntil) position -= ray.direction * 0.08f;
        gunRoot.transform.position = Vector3.Lerp(gunRoot.transform.position, position, 0.7f);
        gunRoot.transform.rotation = Quaternion.Slerp(gunRoot.transform.rotation, rotation, 0.7f);
        if (barrelRotor && Input.GetMouseButton(0) && !Cursor.visible)
            barrelRotor.Rotate(0f, 0f, 1500f * Time.unscaledDeltaTime, Space.Self);
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

    private static void CreateGunPart(Transform parent, string name, PrimitiveType primitive, Vector3 position,
        Vector3 rotation, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.Destroy(collider);
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    private static void PlayShot(Vector3 position)
    {
        try
        {
            var shot = RuntimeManager.CreateInstance(NativeShotEvent);
            shot.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            shot.setVolume(0.48f);
            shot.start();
            shot.release();
        }
        catch (Exception exception)
        {
            Plugin.Log?.LogWarning($"Firework minigun sound failed: {exception.Message}");
        }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = Color.HSVToRGB(Mathf.Repeat(Time.unscaledTime * 0.45f, 1f), 0.9f, 1f);
        GUI.DrawTexture(new Rect(x - 21f, y - 2f, 14f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 7f, y - 2f, 14f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 21f, 4f, 14f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 7f, 4f, 14f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class FireworkRocket : MonoBehaviour
{
    private PlayerCharacter shooter;
    private int colorIndex;
    private float detonateAt;
    private bool detonated;

    internal void Initialize(PlayerCharacter owner, int color, float fuse)
    {
        shooter = owner;
        colorIndex = color;
        detonateAt = Time.time + fuse;
    }

    private void Update()
    {
        if (!detonated && Time.time >= detonateAt) Detonate(transform.position);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (detonated || !FireworkMinigunMod.ImpactDetonation.Value) return;
        var point = collision.contactCount > 0 ? collision.GetContact(0).point : transform.position;
        Detonate(point);
    }

    private void Detonate(Vector3 point)
    {
        if (detonated) return;
        detonated = true;
        FireworkMinigunMod.Detonate(this, point, shooter, colorIndex);
    }

    private void OnDestroy() => FireworkMinigunMod.Unregister(this);
}

internal sealed class FireworkAddressableCleanup : MonoBehaviour
{
    private float releaseAt;
    internal void Configure(float duration) => releaseAt = Time.time + duration;

    private void Update()
    {
        if (Time.time >= releaseAt) Addressables.ReleaseInstance(gameObject);
    }
}

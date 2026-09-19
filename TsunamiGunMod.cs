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

/// <summary>Rapid-fires enormous moving water walls that sweep away physics targets.</summary>
public sealed class TsunamiGunMod : BaseMod
{
    private const string NativeShotSound = "event:/Objects/Objects_PaperCannon";
    private static readonly Vector3 HandOffset = new(0f, 0.18f, 0.16f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Tsunami Gun is unequipped.");
    private static readonly List<TsunamiWave> ActiveWaves = new();
    private static GameObject gunRoot;
    private static Transform waterTank;
    private static Transform muzzle;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextFireTime;
    private static float recoilUntil;

    public override string Name => "Tsunami Gun";
    public override string Description =>
        "Rapid-fire enormous moving walls of water that sweep Wobblies, vehicles, and physics props away.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 45f, Label = "Wave width")]
    public static Ref<float> WaveWidth = new(18f);
    [ModSetting(Order = 20, Min = 2f, Max = 25f, Label = "Wave height")]
    public static Ref<float> WaveHeight = new(8f);
    [ModSetting(Order = 30, Min = 1f, Max = 10f, Label = "Wave thickness")]
    public static Ref<float> WaveThickness = new(3.5f);
    [ModSetting(Order = 40, Min = 5f, Max = 100f, Label = "Wave travel speed")]
    public static Ref<float> TravelSpeed = new(34f);
    [ModSetting(Order = 50, Min = 5f, Max = 120f, Label = "Tsunami force")]
    public static Ref<float> WaveForce = new(48f);
    [ModSetting(Order = 60, Min = 20f, Max = 300f, Label = "Travel distance")]
    public static Ref<float> TravelDistance = new(130f);
    [ModSetting(Order = 70, Min = 0.05f, Max = 1.5f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.18f);
    [ModSetting(Order = 80, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new(true);
    [ModSetting(Order = 90, Min = 1f, Max = 16f, Label = "Maximum active waves")]
    public static Ref<int> MaximumWaves = new(8);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("TsunamiGunHelp",
                "Equip and close F2, then hold left-click to rapid-fire huge moving walls of water. Each wave travels along " +
                "the ground and sweeps Wobblies, cars, and movable physics objects forward and upward. Your own Wobbly is " +
                "protected. Physics effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("TsunamiGunActions",
                ActionMenu(new Button("Equip Tsunami Gun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip gun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire one wave", Fire), nameof(Fire)),
                ActionMenu(new Button("Clear waves", ClearWaves), nameof(ClearWaves))
            ).WithContentWidth(),
            new TextWrapped("TsunamiGunStatus", "").WithText(Status));
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
        FireworkMinigunMod.Unequip();
        JellyGunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip();
        MosesStaffMod.Unequip();
        PowerSwordMod.Unequip();
        BubbleBlasterMod.Unequip();
        EquippedState.Value = true;
        EnsureGunModel();
        Status.Value = "Tsunami Gun equipped. Close F2 and hold left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (gunRoot) Object.Destroy(gunRoot);
        gunRoot = null;
        waterTank = null;
        muzzle = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Tsunami Gun unequipped; fired waves continue until they expire.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearWaves()
    {
        for (var i = ActiveWaves.Count - 1; i >= 0; i--)
            if (ActiveWaves[i]) Object.Destroy(ActiveWaves[i].gameObject);
        ActiveWaves.Clear();
        Status.Value = "All tsunami waves cleared.";
    }

    public override void Update()
    {
        var character = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!character)
        {
            if (EquippedState.Value) Unequip();
            if (ActiveWaves.Count > 0) ClearWaves();
            return;
        }
        if (!EquippedState.Value) return;
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
            Status.Value = "Only the offline player or lobby host can create physics waves.";
            return;
        }
        var camera = Camera.main;
        var shooter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the Tsunami Gun.";
            return;
        }

        PruneWaves();
        var limit = Mathf.Clamp(MaximumWaves.Value, 1, 16);
        while (ActiveWaves.Count >= limit)
        {
            var oldest = ActiveWaves[0];
            ActiveWaves.RemoveAt(0);
            if (oldest) Object.Destroy(oldest.gameObject);
        }

        nextFireTime = Time.unscaledTime + Mathf.Clamp(FireInterval.Value, 0.05f, 1.5f);
        recoilUntil = Time.unscaledTime + 0.08f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var direction = Vector3.ProjectOnPlane(ray.direction, Vector3.up);
        if (direction.sqrMagnitude < 0.01f) direction = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
        if (direction.sqrMagnitude < 0.01f) direction = shooter.transform.forward;
        direction.Normalize();
        var root = new GameObject("ExtraMods Rapid Tsunami Wave");
        root.transform.position = FindGround(camera.transform.position + direction * 5f, shooter);
        root.transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        var wave = root.AddComponent<TsunamiWave>();
        wave.Initialize(shooter, direction);
        ActiveWaves.Add(wave);
        PlayShotSound(muzzle ? muzzle.position : camera.transform.position);
        Status.Value = $"Tsunami fired. Active waves: {ActiveWaves.Count}/{limit}.";
    }

    private static Vector3 FindGround(Vector3 position, PlayerCharacter shooter)
    {
        var hits = Physics.RaycastAll(position + Vector3.up * 30f, Vector3.down, 80f, ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(shooter.transform)) continue;
            if (hit.collider.attachedRigidbody && !hit.collider.attachedRigidbody.isKinematic) continue;
            return hit.point + Vector3.up * 0.1f;
        }
        return position;
    }

    internal static Vector3 FollowGround(Vector3 position, PlayerCharacter shooter)
    {
        var ground = FindGround(position, shooter);
        if (Mathf.Abs(ground.y - position.y) <= 6f) position.y = ground.y;
        return position;
    }

    internal static void Unregister(TsunamiWave wave) => ActiveWaves.Remove(wave);

    private static void PruneWaves()
    {
        for (var i = ActiveWaves.Count - 1; i >= 0; i--)
            if (!ActiveWaves[i]) ActiveWaves.RemoveAt(i);
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

        gunRoot = new GameObject("ExtraMods Tsunami Gun");
        gunRoot.transform.SetParent(anchor, true);
        CreatePart("Ocean Receiver", PrimitiveType.Cube, new Vector3(0f, 0f, 0.18f), Vector3.zero,
            new Vector3(0.38f, 0.32f, 0.95f), new Color(0.025f, 0.22f, 0.48f), true);
        CreatePart("Wave Grip", PrimitiveType.Cube, new Vector3(0f, -0.31f, -0.02f), new Vector3(-15f, 0f, 0f),
            new Vector3(0.19f, 0.46f, 0.24f), new Color(0.04f, 0.09f, 0.16f), false);
        waterTank = CreatePart("Ocean Tank", PrimitiveType.Sphere, new Vector3(0f, 0.28f, 0.08f), Vector3.zero,
            new Vector3(0.56f, 0.5f, 0.56f), new Color(0.03f, 0.62f, 0.95f, 0.72f), true).transform;
        ConfigureWaterMaterial(waterTank.GetComponent<Renderer>(), 0.72f);
        for (var i = 0; i < 3; i++)
            CreatePart("Tsunami Muzzle Ring", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.86f + i * 0.14f),
                new Vector3(90f, 0f, 0f), new Vector3(0.28f + i * 0.055f, 0.045f, 0.28f + i * 0.055f),
                new Color(0.18f, 0.82f, 1f), true);
        muzzle = new GameObject("Tsunami Muzzle").transform;
        muzzle.SetParent(gunRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, 0f, 1.23f);
    }

    private static void UpdateGunModel()
    {
        if (!gunRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var position = anchor.position + rotation * HandOffset;
        if (Time.unscaledTime < recoilUntil) position -= ray.direction * 0.13f;
        gunRoot.transform.position = Vector3.Lerp(gunRoot.transform.position, position, 0.68f);
        gunRoot.transform.rotation = Quaternion.Slerp(gunRoot.transform.rotation, rotation, 0.68f);
        if (waterTank)
        {
            var pulse = 1f + Mathf.Sin(Time.unscaledTime * 6f) * 0.07f;
            waterTank.localScale = new Vector3(0.56f, 0.5f, 0.56f) * pulse;
            waterTank.Rotate(0f, 90f * Time.unscaledDeltaTime, 0f, Space.Self);
        }
    }

    private static GameObject CreatePart(string name, PrimitiveType primitive, Vector3 position, Vector3 rotation,
        Vector3 scale, Color color, bool emissive)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.DestroyImmediate(collider);
        part.transform.SetParent(gunRoot.transform, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = color;
            if (emissive && renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", color * 1.35f);
            }
        }
        return part;
    }

    internal static void ConfigureWaterMaterial(Renderer renderer, float alpha)
    {
        if (!renderer) return;
        var material = renderer.material;
        material.color = new Color(0.02f, 0.46f, 0.92f, alpha);
        material.SetFloat("_Mode", 3f);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", new Color(0.01f, 0.16f, 0.38f));
        }
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!GameInstance.InstanceExists || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static void PlayShotSound(Vector3 position)
    {
        try
        {
            var sound = RuntimeManager.CreateInstance(NativeShotSound);
            sound.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            sound.setPitch(0.72f);
            sound.setVolume(1.25f);
            sound.start();
            sound.release();
        }
        catch (Exception exception) { Plugin.Log?.LogWarning($"Tsunami shot sound failed: {exception.Message}"); }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var oldColor = GUI.color;
        var oldMatrix = GUI.matrix;
        GUI.color = new Color(0.08f, 0.72f, 1f, 1f);
        GUIUtility.RotateAroundPivot(Mathf.Sin(Time.unscaledTime * 3f) * 12f, new Vector2(x, y));
        GUI.DrawTexture(new Rect(x - 25f, y - 2f, 17f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 8f, y - 2f, 17f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 18f, 4f, 10f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 8f, 4f, 10f), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }
}

internal sealed class TsunamiWave : MonoBehaviour
{
    private readonly HashSet<int> hitTargets = new();
    private readonly List<Transform> foam = new();
    private PlayerCharacter owner;
    private Vector3 direction;
    private Vector3 start;
    private Transform waterWall;
    private float bornAt;

    internal void Initialize(PlayerCharacter shooter, Vector3 travelDirection)
    {
        owner = shooter;
        direction = Vector3.ProjectOnPlane(travelDirection, Vector3.up).normalized;
        start = transform.position;
        bornAt = Time.time;
        BuildVisual();
    }

    private void Update()
    {
        if (!owner || Vector3.Distance(start, transform.position) >= TsunamiGunMod.TravelDistance.Value ||
            Time.time - bornAt > 20f)
        {
            Destroy(gameObject);
            return;
        }
        var next = transform.position + direction * Mathf.Clamp(TsunamiGunMod.TravelSpeed.Value, 5f, 100f) * Time.deltaTime;
        transform.position = TsunamiGunMod.FollowGround(next, owner);
        AnimateVisual();
    }

    private void FixedUpdate()
    {
        if (!owner) return;
        var width = Mathf.Clamp(TsunamiGunMod.WaveWidth.Value, 5f, 45f);
        var height = Mathf.Clamp(TsunamiGunMod.WaveHeight.Value, 2f, 25f);
        var thickness = Mathf.Clamp(TsunamiGunMod.WaveThickness.Value, 1f, 10f);
        var center = transform.position + Vector3.up * height * 0.48f;
        var colliders = Physics.OverlapBox(center, new Vector3(width * 0.5f, height * 0.55f, thickness * 0.55f),
            transform.rotation, ~0, QueryTriggerInteraction.Ignore);
        foreach (var collider in colliders)
        {
            if (!collider || collider.transform.IsChildOf(owner.transform)) continue;
            var vehicle = collider.GetComponentInParent<PlayerVehicle>();
            var character = vehicle ? null : collider.GetComponentInParent<PlayerCharacter>();
            var playerBody = character ? character.GetComponentInChildren<PlayerBody>(true) : null;
            var body = vehicle
                ? vehicle.GetVehicleMovementBase()?.GetRigidbody() ?? vehicle.GetComponentInChildren<Rigidbody>(true)
                : playerBody ? playerBody.GetRigidbody() : collider.attachedRigidbody;
            if (!body || body.isKinematic) continue;
            var id = vehicle ? vehicle.GetInstanceID() : character ? character.GetInstanceID() : body.GetInstanceID();
            if (!hitTargets.Add(id)) continue;
            var force = Mathf.Clamp(TsunamiGunMod.WaveForce.Value, 5f, 120f);
            var push = (direction * 0.84f + Vector3.up * 0.36f).normalized * force;
            if (character && playerBody)
            {
                character.GetRagdollController()?.Ragdoll();
                playerBody.SetRagdollVelocity(push);
                if (collider.GetComponentInParent<PlayerNPCController>())
                    PoliceChaseMod.ReportNpcHarassment("tsunami gun hit");
            }
            else
            {
                body.WakeUp();
                body.AddForce(push, ForceMode.VelocityChange);
                body.AddTorque(UnityEngine.Random.onUnitSphere * force * 0.28f, ForceMode.VelocityChange);
            }
        }
    }

    private void BuildVisual()
    {
        var width = Mathf.Clamp(TsunamiGunMod.WaveWidth.Value, 5f, 45f);
        var height = Mathf.Clamp(TsunamiGunMod.WaveHeight.Value, 2f, 25f);
        var thickness = Mathf.Clamp(TsunamiGunMod.WaveThickness.Value, 1f, 10f);
        var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
        wall.name = "Tsunami Water Wall";
        Object.DestroyImmediate(wall.GetComponent<Collider>());
        wall.transform.SetParent(transform, false);
        wall.transform.localPosition = new Vector3(0f, height * 0.45f, 0f);
        wall.transform.localScale = new Vector3(width, height * 0.9f, thickness);
        TsunamiGunMod.ConfigureWaterMaterial(wall.GetComponent<Renderer>(), 0.62f);
        waterWall = wall.transform;

        var count = Mathf.Clamp(Mathf.CeilToInt(width / 1.8f), 5, 25);
        for (var i = 0; i < count; i++)
        {
            var crest = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            crest.name = "Tsunami Foam Crest";
            Object.DestroyImmediate(crest.GetComponent<Collider>());
            crest.transform.SetParent(transform, false);
            var x = Mathf.Lerp(-width * 0.5f, width * 0.5f, i / (float)(count - 1));
            crest.transform.localPosition = new Vector3(x, height * 0.92f, -thickness * 0.05f);
            crest.transform.localScale = new Vector3(1.8f, 0.75f, thickness * 0.8f);
            var renderer = crest.GetComponent<Renderer>();
            if (renderer) renderer.material.color = new Color(0.82f, 0.97f, 1f, 0.92f);
            foam.Add(crest.transform);
        }
        var lightObject = new GameObject("Tsunami Blue Glow");
        lightObject.transform.SetParent(transform, false);
        lightObject.transform.localPosition = new Vector3(0f, height * 0.55f, 0f);
        var light = lightObject.AddComponent<Light>();
        light.color = new Color(0.05f, 0.5f, 1f);
        light.range = Mathf.Max(width, height) * 0.75f;
        light.intensity = 1f;
    }

    private void AnimateVisual()
    {
        var height = Mathf.Clamp(TsunamiGunMod.WaveHeight.Value, 2f, 25f);
        if (waterWall)
        {
            var position = waterWall.localPosition;
            position.y = height * 0.45f + Mathf.Sin(Time.time * 5f) * 0.24f;
            waterWall.localPosition = position;
        }
        for (var i = 0; i < foam.Count; i++)
        {
            if (!foam[i]) continue;
            var position = foam[i].localPosition;
            position.y = height * 0.92f + Mathf.Sin(Time.time * 7f + i * 0.7f) * 0.35f;
            position.z = Mathf.Cos(Time.time * 5f + i) * 0.22f;
            foam[i].localPosition = position;
        }
    }

    private void OnDestroy() => TsunamiGunMod.Unregister(this);
}

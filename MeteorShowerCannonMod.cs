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

/// <summary>Calls down targeted storms of explosive physics meteors.</summary>
public sealed class MeteorShowerCannonMod : BaseMod
{
    private const string NativeShotSound = "event:/Objects/Objects_PaperCannon";
    private static readonly Vector3 HandOffset = new(0f, 0.17f, 0.18f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Meteor Shower Cannon is unequipped.");
    private static readonly List<MeteorStorm> ActiveStorms = new();
    private static GameObject cannonRoot;
    private static Transform meteorCore;
    private static Transform muzzle;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextStormTime;
    private static float recoilUntil;

    public override string Name => "Meteor Shower Cannon";
    public override string Description =>
        "Aim at an area and call down a storm of explosive meteors that launch nearby physics targets.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 3f, Max = 40f, Label = "Meteors per storm")]
    public static Ref<int> MeteorCount = new(12);
    [ModSetting(Order = 20, Min = 3f, Max = 40f, Label = "Storm radius")]
    public static Ref<float> StormRadius = new(16f);
    [ModSetting(Order = 30, Min = 15f, Max = 100f, Label = "Meteor spawn height")]
    public static Ref<float> SpawnHeight = new(48f);
    [ModSetting(Order = 40, Min = 15f, Max = 140f, Label = "Meteor speed")]
    public static Ref<float> MeteorSpeed = new(58f);
    [ModSetting(Order = 50, Min = 2f, Max = 20f, Label = "Explosion radius")]
    public static Ref<float> ExplosionRadius = new(7f);
    [ModSetting(Order = 60, Min = 5f, Max = 120f, Label = "Explosion force")]
    public static Ref<float> ExplosionForce = new(42f);
    [ModSetting(Order = 70, Min = 0.05f, Max = 1f, Label = "Meteor interval")]
    public static Ref<float> MeteorInterval = new(0.16f);
    [ModSetting(Order = 80, Min = 0.5f, Max = 12f, Label = "Storm cooldown")]
    public static Ref<float> StormCooldown = new(3f);
    [ModSetting(Order = 90, Min = 1f, Max = 5f, Label = "Maximum active storms")]
    public static Ref<int> MaximumStorms = new(2);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("MeteorShowerHelp",
                "Equip and close F2, aim at the ground, and left-click. The cannon marks that area and rains fiery meteors " +
                "across it. Every impact creates a blast that ragdolls Wobblies and launches cars and movable props. The " +
                "firing Wobbly is protected. Physics effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("MeteorShowerActions",
                ActionMenu(new Button("Equip Meteor Cannon", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip cannon", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Call meteor storm", Fire), nameof(Fire)),
                ActionMenu(new Button("Clear storms", ClearStorms), nameof(ClearStorms))
            ).WithContentWidth(),
            new TextWrapped("MeteorShowerStatus", "").WithText(Status));
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
        TsunamiGunMod.Unequip();
        EquippedState.Value = true;
        EnsureCannonModel();
        Status.Value = "Meteor Shower Cannon equipped. Aim at the ground and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (cannonRoot) Object.Destroy(cannonRoot);
        cannonRoot = null;
        meteorCore = null;
        muzzle = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Meteor Shower Cannon unequipped; active storms continue until finished.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearStorms()
    {
        for (var i = ActiveStorms.Count - 1; i >= 0; i--)
            if (ActiveStorms[i]) Object.Destroy(ActiveStorms[i].gameObject);
        ActiveStorms.Clear();
        Status.Value = "All meteor storms cleared.";
    }

    public override void Update()
    {
        var character = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!character)
        {
            if (EquippedState.Value) Unequip();
            if (ActiveStorms.Count > 0) ClearStorms();
            return;
        }
        if (!EquippedState.Value) return;
        EnsureCannonModel();
        UpdateCannonModel();
        if (!Cursor.visible && Input.GetMouseButtonDown(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextStormTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can call a meteor storm.";
            return;
        }
        var camera = Camera.main;
        var shooter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before calling a meteor storm.";
            return;
        }

        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var center = ray.origin + ray.direction * 55f;
        var hits = Physics.RaycastAll(ray, 300f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(shooter.transform)) continue;
            center = hit.point;
            break;
        }

        PruneStorms();
        var limit = Mathf.Clamp(MaximumStorms.Value, 1, 5);
        while (ActiveStorms.Count >= limit)
        {
            var oldest = ActiveStorms[0];
            ActiveStorms.RemoveAt(0);
            if (oldest) Object.Destroy(oldest.gameObject);
        }

        nextStormTime = Time.unscaledTime + Mathf.Clamp(StormCooldown.Value, 0.5f, 12f);
        recoilUntil = Time.unscaledTime + 0.12f;
        var stormObject = new GameObject("ExtraMods Meteor Shower");
        stormObject.transform.position = center;
        var storm = stormObject.AddComponent<MeteorStorm>();
        storm.Initialize(shooter, Mathf.Clamp(MeteorCount.Value, 3, 40));
        ActiveStorms.Add(storm);
        PlayShotSound(muzzle ? muzzle.position : camera.transform.position);
        Status.Value = $"Meteor storm called: {MeteorCount.Value} meteors incoming.";
    }

    internal static void Unregister(MeteorStorm storm) => ActiveStorms.Remove(storm);

    private static void PruneStorms()
    {
        for (var i = ActiveStorms.Count - 1; i >= 0; i--)
            if (!ActiveStorms[i]) ActiveStorms.RemoveAt(i);
    }

    private static void EnsureCannonModel()
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
        if (cannonRoot)
        {
            if (cannonRoot.transform.parent != anchor) cannonRoot.transform.SetParent(anchor, true);
            return;
        }

        cannonRoot = new GameObject("ExtraMods Meteor Shower Cannon");
        cannonRoot.transform.SetParent(anchor, true);
        CreatePart("Meteor Receiver", PrimitiveType.Cube, new Vector3(0f, 0f, 0.22f), Vector3.zero,
            new Vector3(0.38f, 0.34f, 1.05f), new Color(0.16f, 0.08f, 0.22f), false);
        CreatePart("Meteor Grip", PrimitiveType.Cube, new Vector3(0f, -0.34f, -0.02f), new Vector3(-14f, 0f, 0f),
            new Vector3(0.2f, 0.48f, 0.25f), new Color(0.08f, 0.05f, 0.1f), false);
        meteorCore = CreatePart("Meteor Core", PrimitiveType.Sphere, new Vector3(0f, 0.28f, 0.1f), Vector3.zero,
            Vector3.one * 0.54f, new Color(1f, 0.16f, 0.015f), true).transform;
        for (var i = 0; i < 3; i++)
            CreatePart("Meteor Muzzle Ring", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.94f + i * 0.15f),
                new Vector3(90f, 0f, 0f), new Vector3(0.27f + i * 0.05f, 0.05f, 0.27f + i * 0.05f),
                new Color(1f, 0.32f, 0.03f), true);
        muzzle = new GameObject("Meteor Cannon Muzzle").transform;
        muzzle.SetParent(cannonRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, 0f, 1.32f);
        var light = meteorCore.gameObject.AddComponent<Light>();
        light.color = new Color(1f, 0.16f, 0.01f);
        light.range = 5f;
        light.intensity = 2f;
    }

    private static void UpdateCannonModel()
    {
        if (!cannonRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var position = anchor.position + rotation * HandOffset;
        if (Time.unscaledTime < recoilUntil) position -= ray.direction * 0.15f;
        cannonRoot.transform.position = Vector3.Lerp(cannonRoot.transform.position, position, 0.68f);
        cannonRoot.transform.rotation = Quaternion.Slerp(cannonRoot.transform.rotation, rotation, 0.68f);
        if (meteorCore)
        {
            var pulse = 1f + Mathf.Sin(Time.unscaledTime * 8f) * 0.12f;
            meteorCore.localScale = Vector3.one * 0.54f * pulse;
            meteorCore.Rotate(50f * Time.unscaledDeltaTime, 90f * Time.unscaledDeltaTime, 0f, Space.Self);
        }
    }

    private static GameObject CreatePart(string name, PrimitiveType primitive, Vector3 position, Vector3 rotation,
        Vector3 scale, Color color, bool emissive)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.DestroyImmediate(collider);
        part.transform.SetParent(cannonRoot.transform, false);
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
                renderer.material.SetColor("_EmissionColor", color * 2.4f);
            }
        }
        return part;
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
            sound.setPitch(0.58f);
            sound.setVolume(1.35f);
            sound.start();
            sound.release();
        }
        catch (Exception exception) { Plugin.Log?.LogWarning($"Meteor cannon sound failed: {exception.Message}"); }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var oldColor = GUI.color;
        var oldMatrix = GUI.matrix;
        GUI.color = Color.Lerp(new Color(1f, 0.18f, 0.02f), new Color(1f, 0.75f, 0.08f),
            Mathf.PingPong(Time.unscaledTime * 2f, 1f));
        GUIUtility.RotateAroundPivot(Time.unscaledTime * 90f, new Vector2(x, y));
        for (var i = 0; i < 4; i++)
        {
            var angle = i * 90f * Mathf.Deg2Rad;
            var dx = Mathf.Cos(angle) * 16f;
            var dy = Mathf.Sin(angle) * 16f;
            GUI.DrawTexture(new Rect(x + dx - 5f, y + dy - 2f, 10f, 4f), Texture2D.whiteTexture);
        }
        GUI.matrix = oldMatrix;
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }
}

internal sealed class MeteorStorm : MonoBehaviour
{
    private PlayerCharacter owner;
    private int meteorCount;
    private int spawned;
    private float nextMeteorTime;
    private float finishedAt = -1f;

    internal void Initialize(PlayerCharacter shooter, int count)
    {
        owner = shooter;
        meteorCount = count;
        nextMeteorTime = Time.time;
    }

    private void Update()
    {
        if (!owner) { Destroy(gameObject); return; }
        if (spawned < meteorCount && Time.time >= nextMeteorTime)
        {
            SpawnMeteor();
            spawned++;
            nextMeteorTime = Time.time + Mathf.Clamp(MeteorShowerCannonMod.MeteorInterval.Value, 0.05f, 1f);
            if (spawned >= meteorCount) finishedAt = Time.time;
        }
        if (finishedAt > 0f && transform.childCount == 0 && Time.time - finishedAt > 0.2f)
            Destroy(gameObject);
    }

    private void SpawnMeteor()
    {
        var radius = Mathf.Clamp(MeteorShowerCannonMod.StormRadius.Value, 3f, 40f);
        var offset2 = UnityEngine.Random.insideUnitCircle * radius;
        var target = transform.position + new Vector3(offset2.x, 0f, offset2.y);
        var start = target + Vector3.up * Mathf.Clamp(MeteorShowerCannonMod.SpawnHeight.Value, 15f, 100f) +
                    new Vector3(UnityEngine.Random.Range(-8f, 8f), 0f, UnityEngine.Random.Range(-8f, 8f));
        var meteorObject = new GameObject("Falling Meteor");
        meteorObject.transform.SetParent(transform, true);
        meteorObject.transform.position = start;
        var meteor = meteorObject.AddComponent<MeteorProjectile>();
        meteor.Initialize(owner, target);
    }

    private void OnDestroy() => MeteorShowerCannonMod.Unregister(this);
}

internal sealed class MeteorProjectile : MonoBehaviour
{
    private readonly HashSet<int> affected = new();
    private PlayerCharacter owner;
    private Vector3 velocity;
    private float expiresAt;
    private Transform rock;
    private TrailRenderer trail;
    private bool exploded;

    internal void Initialize(PlayerCharacter shooter, Vector3 target)
    {
        owner = shooter;
        velocity = (target - transform.position).normalized * Mathf.Clamp(MeteorShowerCannonMod.MeteorSpeed.Value, 15f, 140f);
        expiresAt = Time.time + 8f;
        BuildVisual();
    }

    private void Update()
    {
        if (exploded) return;
        if (!owner || Time.time >= expiresAt) { Explode(transform.position); return; }
        var previous = transform.position;
        velocity += Physics.gravity * 0.22f * Time.deltaTime;
        var movement = velocity * Time.deltaTime;
        if (Physics.SphereCast(previous, 0.55f, movement.normalized, out var hit, movement.magnitude, ~0,
                QueryTriggerInteraction.Ignore) && hit.collider && !hit.transform.IsChildOf(owner.transform))
        {
            Explode(hit.point);
            return;
        }
        transform.position = previous + movement;
        if (rock) rock.Rotate(380f * Time.deltaTime, 240f * Time.deltaTime, 160f * Time.deltaTime, Space.Self);
    }

    private void BuildVisual()
    {
        var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.name = "Burning Meteor Rock";
        Object.DestroyImmediate(visual.GetComponent<Collider>());
        visual.transform.SetParent(transform, false);
        visual.transform.localScale = Vector3.one * UnityEngine.Random.Range(1.1f, 2f);
        var renderer = visual.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = new Color(0.13f, 0.055f, 0.025f);
            if (renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", new Color(1f, 0.12f, 0.005f) * 2.4f);
            }
        }
        rock = visual.transform;
        trail = gameObject.AddComponent<TrailRenderer>();
        trail.time = 0.55f;
        trail.startWidth = 1.2f;
        trail.endWidth = 0.05f;
        trail.material = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color"));
        trail.startColor = new Color(1f, 0.16f, 0.01f, 0.95f);
        trail.endColor = new Color(1f, 0.75f, 0.03f, 0f);
        var light = gameObject.AddComponent<Light>();
        light.color = new Color(1f, 0.13f, 0.01f);
        light.range = 10f;
        light.intensity = 2.8f;
    }

    private void Explode(Vector3 point)
    {
        if (exploded) return;
        exploded = true;
        var radius = Mathf.Clamp(MeteorShowerCannonMod.ExplosionRadius.Value, 2f, 20f);
        var force = Mathf.Clamp(MeteorShowerCannonMod.ExplosionForce.Value, 5f, 120f);
        foreach (var collider in Physics.OverlapSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
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
            if (!affected.Add(id)) continue;
            var direction = (body.worldCenterOfMass - point + Vector3.up * 2f).normalized;
            var falloff = Mathf.Clamp01(1f - Vector3.Distance(body.worldCenterOfMass, point) / radius);
            var launch = direction * force * Mathf.Lerp(0.35f, 1f, falloff);
            if (character && playerBody)
            {
                character.GetRagdollController()?.Ragdoll();
                playerBody.SetRagdollVelocity(launch);
                if (collider.GetComponentInParent<PlayerNPCController>())
                    PoliceChaseMod.ReportNpcHarassment("meteor impact");
            }
            else
            {
                body.WakeUp();
                body.AddForce(launch, ForceMode.VelocityChange);
                body.AddTorque(UnityEngine.Random.onUnitSphere * force * 0.32f, ForceMode.VelocityChange);
            }
        }
        CreateBlastVisual(point, radius);
        if (trail) trail.transform.SetParent(null, true);
        Destroy(gameObject);
    }

    private static void CreateBlastVisual(Vector3 point, float radius)
    {
        var blast = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        blast.name = "Meteor Impact Blast";
        Object.DestroyImmediate(blast.GetComponent<Collider>());
        blast.transform.position = point;
        blast.transform.localScale = Vector3.one * radius * 1.6f;
        var renderer = blast.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = new Color(1f, 0.16f, 0.01f, 0.65f);
            if (renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", new Color(1f, 0.08f, 0.005f) * 2f);
            }
        }
        Object.Destroy(blast, 0.16f);
    }
}

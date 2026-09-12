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

/// <summary>Creates a timed walkable collision corridor through static scenery.</summary>
public sealed class TemporaryTunnelDrillMod : BaseMod
{
    private const string DrillSparksAddress =
        "Assets/Content/Space/Prefabs/Particles/Mining Colony/Giant Drill Sparks.prefab";
    private const string NativeDrillSound = "event:/Objects/Objects_Jackhammer";
    private static readonly Vector3 HandOffset = new(0f, 0.2f, 0.18f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Temporary Tunnel Drill is unequipped.");
    private static readonly List<TemporaryWalkTunnel> ActiveTunnels = new();
    private static GameObject drillRoot;
    private static Transform drillBit;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextDrillTime;

    public override string Name => "Temporary Tunnel Drill";
    public override string Description =>
        "Drill temporary illuminated, supported walkways through mountains and buildings, then restore collision.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 6f, Max = 100f, Label = "Tunnel length")]
    public static Ref<float> TunnelLength = new(30f);
    [ModSetting(Order = 20, Min = 1.2f, Max = 6f, Label = "Tunnel radius")]
    public static Ref<float> TunnelRadius = new(2.5f);
    [ModSetting(Order = 30, Min = 5f, Max = 180f, Label = "Tunnel lifetime")]
    public static Ref<float> TunnelLifetime = new(45f);
    [ModSetting(Order = 40, Min = 10f, Max = 500f, Label = "Drill range")]
    public static Ref<float> DrillRange = new(180f);
    [ModSetting(Order = 50, Min = 0.2f, Max = 3f, Label = "Drill cooldown")]
    public static Ref<float> Cooldown = new(0.8f);
    [ModSetting(Order = 60, Min = 1f, Max = 8f, Label = "Maximum tunnels")]
    public static Ref<int> MaximumTunnels = new(3);
    [ModSetting(Order = 70, Label = "Show tunnel lights")]
    public static Ref<bool> ShowLights = new(true);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("TemporaryTunnelHelp",
                "Equip and close F2, aim at a mountain or building, and left-click. The drill creates a temporary supported " +
                "collision corridor through the static structure so your Wobbly can walk through it. Illuminated ribs mark " +
                "the route. Collision is restored automatically after the timer, but expiry waits until you leave the tunnel. " +
                "Press R or use Clear tunnels to remove them safely."),
            base.BuildPanel(id),
            new HStack("TemporaryTunnelActions",
                ActionMenu(new Button("Equip tunnel drill", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip tunnel drill", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Drill once", Drill), nameof(Drill)),
                ActionMenu(new Button("Clear tunnels", ClearTunnels), nameof(ClearTunnels))
            ).WithContentWidth(),
            new TextWrapped("TemporaryTunnelStatus", "").WithText(Status));
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
        MosesStaffMod.Unequip();
        PowerSwordMod.Unequip();
        EquippedState.Value = true;
        EnsureDrillModel();
        Status.Value = "Tunnel drill equipped. Aim at a mountain or building and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (drillRoot) Object.Destroy(drillRoot);
        drillRoot = null;
        drillBit = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Tunnel drill unequipped; existing tunnels remain until their timers end.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearTunnels()
    {
        for (var i = ActiveTunnels.Count - 1; i >= 0; i--)
            if (ActiveTunnels[i]) ActiveTunnels[i].RemoveSafely();
        ActiveTunnels.Clear();
        Status.Value = "Temporary tunnels cleared and original collision restored.";
    }

    public override void Update()
    {
        if (!GameInstance.InstanceExists || !GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter())
        {
            if (EquippedState.Value) Unequip();
            if (ActiveTunnels.Count > 0) ClearTunnels();
            return;
        }
        if (!EquippedState.Value) return;
        EnsureDrillModel();
        UpdateDrillModel();
        if (Cursor.visible) return;
        if (Input.GetMouseButtonDown(0)) Drill();
        if (Input.GetKeyDown(KeyCode.R)) ClearTunnels();
    }

    [ModAction(ShowInUI = false)]
    public static void Drill()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextDrillTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Use the tunnel drill offline or as the lobby host.";
            return;
        }
        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !character)
        {
            Status.Value = "Enter a save before drilling a tunnel.";
            return;
        }
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var hits = Physics.RaycastAll(ray, Mathf.Max(10f, DrillRange.Value), ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        RaycastHit selected = default;
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(character.transform)) continue;
            if (hit.collider.GetComponentInParent<PlayerCharacter>() ||
                hit.collider.GetComponentInParent<PlayerVehicle>() ||
                hit.collider.GetComponentInParent<DynamicObject>() ||
                (hit.collider.attachedRigidbody && !hit.collider.attachedRigidbody.isKinematic))
            {
                Status.Value = "Aim directly at a static mountain or building surface.";
                return;
            }
            selected = hit;
            break;
        }
        if (!selected.collider)
        {
            Status.Value = "No drillable mountain or building was found under the crosshair.";
            return;
        }

        var direction = ray.direction.normalized;
        var length = Mathf.Clamp(TunnelLength.Value, 6f, 100f);
        var radius = Mathf.Clamp(TunnelRadius.Value, 1.2f, 6f);
        var entry = selected.point + direction * 0.18f;
        var exit = entry + direction * length;
        var blockers = Physics.OverlapCapsule(entry, exit, radius, ~0, QueryTriggerInteraction.Ignore);
        var staticBlockers = new List<Collider>();
        foreach (var collider in blockers)
        {
            if (!collider || collider.GetComponentInParent<PlayerCharacter>() ||
                collider.GetComponentInParent<PlayerVehicle>() || collider.GetComponentInParent<DynamicObject>()) continue;
            if (collider.attachedRigidbody && !collider.attachedRigidbody.isKinematic) continue;
            if (!staticBlockers.Contains(collider)) staticBlockers.Add(collider);
        }
        if (!staticBlockers.Contains(selected.collider)) staticBlockers.Add(selected.collider);

        CleanupTunnelSet();
        var limit = Mathf.Clamp(MaximumTunnels.Value, 1, 8);
        while (ActiveTunnels.Count >= limit)
        {
            var oldest = ActiveTunnels[0];
            ActiveTunnels.RemoveAt(0);
            if (oldest) oldest.RemoveSafely();
        }

        nextDrillTime = Time.unscaledTime + Mathf.Max(0.2f, Cooldown.Value);
        var tunnelObject = new GameObject("ExtraMods Temporary Walk Tunnel");
        var tunnel = tunnelObject.AddComponent<TemporaryWalkTunnel>();
        tunnel.Initialize(entry, exit, radius, staticBlockers,
            Mathf.Clamp(TunnelLifetime.Value, 5f, 180f), character, ShowLights.Value);
        ActiveTunnels.Add(tunnel);
        SpawnDrillSparks(entry);
        PlayDrillSound(entry);
        Status.Value = $"Tunnel drilled through {staticBlockers.Count} collider(s). Active: {ActiveTunnels.Count}/{limit}.";
    }

    private static void SpawnDrillSparks(Vector3 position)
    {
        Plugin.RunCoroutine(SpawnDrillSparksRoutine(position));
    }

    private static IEnumerator SpawnDrillSparksRoutine(Vector3 position)
    {
        var handle = Addressables.InstantiateAsync(DrillSparksAddress, position, Quaternion.identity);
        yield return handle;
        if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result)
            handle.Result.AddComponent<TunnelAddressableCleanup>().Configure(4f);
        else
        {
            if (handle.IsValid()) Addressables.Release(handle);
            Plugin.Log?.LogWarning($"Could not load native drill sparks: {DrillSparksAddress}");
        }
    }

    private static void PlayDrillSound(Vector3 position)
    {
        try
        {
            var sound = RuntimeManager.CreateInstance(NativeDrillSound);
            sound.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            sound.setVolume(0.7f);
            sound.start();
            sound.release();
        }
        catch (Exception exception) { Plugin.Log?.LogWarning($"Tunnel drill sound failed: {exception.Message}"); }
    }

    internal static void Unregister(TemporaryWalkTunnel tunnel) => ActiveTunnels.Remove(tunnel);

    private static void CleanupTunnelSet()
    {
        for (var i = ActiveTunnels.Count - 1; i >= 0; i--)
            if (!ActiveTunnels[i]) ActiveTunnels.RemoveAt(i);
    }

    private static void EnsureDrillModel()
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
        if (drillRoot)
        {
            if (drillRoot.transform.parent != anchor) drillRoot.transform.SetParent(anchor, true);
            return;
        }
        drillRoot = new GameObject("ExtraMods Temporary Tunnel Drill");
        drillRoot.transform.SetParent(anchor, false);
        CreateDrillPart(drillRoot.transform, "Drill Motor", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.15f),
            new Vector3(90f, 0f, 0f), new Vector3(0.34f, 0.52f, 0.34f), new Color(0.9f, 0.55f, 0.08f));
        CreateDrillPart(drillRoot.transform, "Drill Grip", PrimitiveType.Cube, new Vector3(0f, -0.34f, -0.05f),
            new Vector3(-15f, 0f, 0f), new Vector3(0.2f, 0.48f, 0.24f), new Color(0.12f, 0.13f, 0.15f));
        drillBit = new GameObject("Spinning Tunnel Drill Bit").transform;
        drillBit.SetParent(drillRoot.transform, false);
        for (var i = 0; i < 5; i++)
        {
            var size = 0.28f - i * 0.045f;
            CreateDrillPart(drillBit, "Drill Bit Stage", PrimitiveType.Cylinder,
                new Vector3(0f, 0f, 0.72f + i * 0.16f), new Vector3(90f, 0f, i * 35f),
                new Vector3(size, 0.12f, size), new Color(0.38f, 0.42f, 0.46f));
        }
    }

    private static void UpdateDrillModel()
    {
        if (!drillRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        drillRoot.transform.position = Vector3.Lerp(drillRoot.transform.position, anchor.position + rotation * HandOffset, 0.68f);
        drillRoot.transform.rotation = Quaternion.Slerp(drillRoot.transform.rotation, rotation, 0.68f);
        if (drillBit) drillBit.Rotate(0f, 0f, 900f * Time.unscaledDeltaTime, Space.Self);
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!GameInstance.InstanceExists || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static void CreateDrillPart(Transform parent, string name, PrimitiveType primitive, Vector3 position,
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

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        var oldMatrix = GUI.matrix;
        GUI.color = new Color(1f, 0.62f, 0.08f, 1f);
        GUIUtility.RotateAroundPivot(Time.unscaledTime * 150f, new Vector2(x, y));
        for (var i = 0; i < 3; i++)
        {
            var radius = 7f + i * 6f;
            GUI.DrawTexture(new Rect(x + radius, y - 1.5f, 8f, 3f), Texture2D.whiteTexture);
            GUI.DrawTexture(new Rect(x - radius - 8f, y - 1.5f, 8f, 3f), Texture2D.whiteTexture);
        }
        GUI.matrix = oldMatrix;
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class TemporaryWalkTunnel : MonoBehaviour
{
    private readonly List<Collider> blockers = new();
    private readonly List<CollisionPair> ignoredPairs = new();
    private Vector3 entry;
    private Vector3 exit;
    private Vector3 direction;
    private float radius;
    private float expiresAt;
    private PlayerCharacter player;
    private bool passageOpen;

    internal void Initialize(Vector3 tunnelEntry, Vector3 tunnelExit, float tunnelRadius,
        IEnumerable<Collider> staticBlockers, float lifetime, PlayerCharacter localPlayer, bool showLights)
    {
        entry = tunnelEntry;
        exit = tunnelExit;
        direction = (exit - entry).normalized;
        radius = tunnelRadius;
        expiresAt = Time.time + lifetime;
        player = localPlayer;
        blockers.AddRange(staticBlockers);
        transform.position = entry;
        transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        BuildWalkway(showLights);
    }

    private void Update()
    {
        if (!player)
        {
            Destroy(gameObject);
            return;
        }
        var point = PlayerPosition();
        var close = DistanceToSegment(point, entry - direction * 1.5f, exit + direction * 1.5f) <= radius + 0.85f;
        if (close != passageOpen) SetPassage(close);
        if (Time.time < expiresAt) return;
        if (close)
        {
            expiresAt = Time.time + 1f;
            return;
        }
        Destroy(gameObject);
    }

    internal void RemoveSafely()
    {
        if (passageOpen && player)
        {
            var point = PlayerPosition();
            var entryDistance = (point - entry).sqrMagnitude;
            var exitDistance = (point - exit).sqrMagnitude;
            player.SetPlayerPosition(entryDistance <= exitDistance ? entry - direction * 2f : exit + direction * 2f);
        }
        Destroy(gameObject);
    }

    private Vector3 PlayerPosition()
    {
        var body = player ? player.GetComponentInChildren<PlayerBody>(true) : null;
        var rigidbody = body ? body.GetRigidbody() : null;
        return rigidbody ? rigidbody.position : player.transform.position;
    }

    private void SetPassage(bool open)
    {
        if (open)
        {
            ignoredPairs.Clear();
            foreach (var playerCollider in player.GetComponentsInChildren<Collider>(true))
            foreach (var blocker in blockers)
            {
                if (!playerCollider || !blocker || playerCollider == blocker) continue;
                Physics.IgnoreCollision(playerCollider, blocker, true);
                ignoredPairs.Add(new CollisionPair(playerCollider, blocker));
            }
        }
        else RestoreCollision();
        passageOpen = open;
    }

    private void RestoreCollision()
    {
        foreach (var pair in ignoredPairs)
            if (pair.PlayerCollider && pair.Blocker)
                Physics.IgnoreCollision(pair.PlayerCollider, pair.Blocker, false);
        ignoredPairs.Clear();
        passageOpen = false;
    }

    private void BuildWalkway(bool showLights)
    {
        var length = Vector3.Distance(entry, exit);
        CreatePart("Tunnel Floor", new Vector3(0f, -radius * 0.72f, length * 0.5f),
            new Vector3(radius * 1.55f, 0.28f, length + 3f), new Color(0.16f, 0.17f, 0.18f), true);
        const int rails = 14;
        for (var i = 0; i < rails; i++)
        {
            var angle = i / (float)rails * Mathf.PI * 2f;
            if (Mathf.Sin(angle) < -0.5f) continue;
            var x = Mathf.Cos(angle) * radius;
            var y = Mathf.Sin(angle) * radius;
            CreatePart("Tunnel Rib", new Vector3(x, y, length * 0.5f),
                new Vector3(0.12f, 0.12f, length), new Color(0.3f, 0.34f, 0.38f), false);
        }
        for (var ring = 0; ring <= 5; ring++)
        {
            var z = length * ring / 5f;
            for (var i = 0; i < 14; i++)
            {
                var angle = i / 14f * Mathf.PI * 2f;
                var radial = new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f);
                CreatePart("Tunnel Ring", radial * radius + Vector3.forward * z,
                    new Vector3(0.16f, 0.16f, 0.35f), new Color(0.9f, 0.52f, 0.06f), false);
            }
            if (!showLights) continue;
            var lightObject = new GameObject("Tunnel Light");
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localPosition = new Vector3(0f, radius * 0.65f, z);
            var light = lightObject.AddComponent<Light>();
            light.color = new Color(1f, 0.62f, 0.18f);
            light.range = radius * 2.4f;
            light.intensity = 1.15f;
        }
    }

    private void CreatePart(string partName, Vector3 localPosition, Vector3 localScale, Color color, bool colliderEnabled)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        part.name = partName;
        part.transform.SetParent(transform, false);
        part.transform.localPosition = localPosition;
        part.transform.localScale = localScale;
        var collider = part.GetComponent<Collider>();
        if (collider && !colliderEnabled) Destroy(collider);
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    private static float DistanceToSegment(Vector3 point, Vector3 start, Vector3 end)
    {
        var line = end - start;
        var t = line.sqrMagnitude < 0.001f ? 0f : Mathf.Clamp01(Vector3.Dot(point - start, line) / line.sqrMagnitude);
        return Vector3.Distance(point, start + line * t);
    }

    private void OnDestroy()
    {
        RestoreCollision();
        TemporaryTunnelDrillMod.Unregister(this);
    }

    private sealed class CollisionPair
    {
        internal readonly Collider PlayerCollider;
        internal readonly Collider Blocker;
        internal CollisionPair(Collider playerCollider, Collider blocker)
        {
            PlayerCollider = playerCollider;
            Blocker = blocker;
        }
    }
}

internal sealed class TunnelAddressableCleanup : MonoBehaviour
{
    private float releaseAt;
    internal void Configure(float duration) => releaseAt = Time.time + duration;
    private void Update()
    {
        if (Time.time >= releaseAt) Addressables.ReleaseInstance(gameObject);
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>An equippable staff that opens a temporary dry, walkable path through water.</summary>
public sealed class MosesStaffMod : BaseMod
{
    private static readonly Vector3 HandOffset = new(0.06f, -0.65f, 0.12f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Moses Staff is unequipped.");
    private static readonly List<PartedWaterPath> ActivePaths = new();
    private static GameObject staffRoot;
    private static Transform staffGlow;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextUseTime;

    public override string Name => "Moses Staff";
    public override string Description =>
        "Raise the staff to part the water and create a temporary dry, walkable path with towering waves.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 12f, Max = 120f, Label = "Path length")]
    public static Ref<float> PathLength = new(55f);
    [ModSetting(Order = 20, Min = 3f, Max = 14f, Label = "Path width")]
    public static Ref<float> PathWidth = new(6f);
    [ModSetting(Order = 30, Min = 2f, Max = 14f, Label = "Water wall height")]
    public static Ref<float> WallHeight = new(7f);
    [ModSetting(Order = 40, Min = 8f, Max = 180f, Label = "Parting duration")]
    public static Ref<float> Duration = new(45f);
    [ModSetting(Order = 50, Min = 5f, Max = 80f, Label = "Parting force")]
    public static Ref<float> PartingForce = new(32f);
    [ModSetting(Order = 60, Min = 1f, Max = 5f, Label = "Maximum paths")]
    public static Ref<int> MaximumPaths = new(2);
    [ModSetting(Order = 70, Label = "Glowing path lights")]
    public static Ref<bool> ShowLights = new(true);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("MosesStaffHelp",
                "Equip and close F2. Face across the water and left-click to raise the staff. Two animated walls of water " +
                "open around a solid dry path, nearby movable objects are swept away from the middle, and your Wobbly ignores " +
                "water triggers while walking inside it. The water closes after the timer, but waits if you are still on the path. " +
                "Press R or use Close paths to remove them safely."),
            base.BuildPanel(id),
            new HStack("MosesStaffActions",
                ActionMenu(new Button("Equip Moses Staff", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip staff", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Part the water", PartWater), nameof(PartWater)),
                ActionMenu(new Button("Close paths", ClosePaths), nameof(ClosePaths))
            ).WithContentWidth(),
            new TextWrapped("MosesStaffStatus", "").WithText(Status));
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
        PowerSwordMod.Unequip();
        EquippedState.Value = true;
        EnsureStaffModel();
        Status.Value = "Moses Staff equipped. Face across the water and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (staffRoot) Object.Destroy(staffRoot);
        staffRoot = null;
        staffGlow = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Moses Staff unequipped; open paths remain until their timers end.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClosePaths()
    {
        for (var i = ActivePaths.Count - 1; i >= 0; i--)
            if (ActivePaths[i]) ActivePaths[i].CloseSafely();
        ActivePaths.Clear();
        Status.Value = "The water has closed and all temporary paths were removed.";
    }

    public override void Update()
    {
        var character = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!character)
        {
            if (EquippedState.Value) Unequip();
            if (ActivePaths.Count > 0) ClosePaths();
            return;
        }
        if (!EquippedState.Value) return;
        EnsureStaffModel();
        UpdateStaffModel();
        if (Cursor.visible) return;
        if (Input.GetMouseButtonDown(0)) PartWater();
        if (Input.GetKeyDown(KeyCode.R)) ClosePaths();
    }

    [ModAction(ShowInUI = false)]
    public static void PartWater()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextUseTime) return;
        var camera = Camera.main;
        var character = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!camera || !character)
        {
            Status.Value = "Enter a save before using the staff.";
            return;
        }

        var forward = camera.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.05f) forward = character.transform.forward;
        forward.y = 0f;
        forward.Normalize();

        var playerPosition = GetPlayerPosition(character);
        var pathY = FindSurfaceHeight(playerPosition + forward * 3f, playerPosition.y - 1.25f);
        var width = Mathf.Clamp(PathWidth.Value, 3f, 14f);
        var length = Mathf.Clamp(PathLength.Value, 12f, 120f);
        var start = new Vector3(playerPosition.x, pathY, playerPosition.z) + forward * 1.25f;
        var end = start + forward * length;

        CleanupPathSet();
        var limit = Mathf.Clamp(MaximumPaths.Value, 1, 5);
        while (ActivePaths.Count >= limit)
        {
            var oldest = ActivePaths[0];
            ActivePaths.RemoveAt(0);
            if (oldest) oldest.CloseSafely();
        }

        nextUseTime = Time.unscaledTime + 1.1f;
        var pathObject = new GameObject("ExtraMods Parted Water Path");
        var path = pathObject.AddComponent<PartedWaterPath>();
        path.Initialize(start, end, width, Mathf.Clamp(WallHeight.Value, 2f, 14f),
            Mathf.Clamp(Duration.Value, 8f, 180f), Mathf.Clamp(PartingForce.Value, 5f, 80f),
            character, ShowLights.Value);
        ActivePaths.Add(path);
        Status.Value = $"The water is parted for {length:0} metres. Active paths: {ActivePaths.Count}/{limit}.";
    }

    private static float FindSurfaceHeight(Vector3 around, float fallback)
    {
        var hits = Physics.RaycastAll(around + Vector3.up * 35f, Vector3.down, 80f, ~0,
            QueryTriggerInteraction.Collide);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider) continue;
            if (hit.collider.GetComponentInParent<Water>()) return hit.point.y + 0.05f;
        }
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.collider.isTrigger || hit.normal.y < 0.45f) continue;
            if (hit.collider.GetComponentInParent<PlayerCharacter>() ||
                hit.collider.GetComponentInParent<PlayerVehicle>() ||
                hit.collider.GetComponentInParent<DynamicObject>()) continue;
            return hit.point.y + 0.08f;
        }
        return fallback;
    }

    private static Vector3 GetPlayerPosition(PlayerCharacter character)
    {
        var body = character.GetComponentInChildren<PlayerBody>(true);
        var rigidbody = body ? body.GetRigidbody() : null;
        return rigidbody ? rigidbody.position : character.transform.position;
    }

    internal static void Unregister(PartedWaterPath path) => ActivePaths.Remove(path);

    private static void CleanupPathSet()
    {
        for (var i = ActivePaths.Count - 1; i >= 0; i--)
            if (!ActivePaths[i]) ActivePaths.RemoveAt(i);
    }

    private static void EnsureStaffModel()
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
        if (staffRoot)
        {
            if (staffRoot.transform.parent != anchor) staffRoot.transform.SetParent(anchor, true);
            return;
        }

        staffRoot = new GameObject("ExtraMods Moses Staff");
        staffRoot.transform.SetParent(anchor, false);
        CreateStaffPart(staffRoot.transform, "Wooden Staff", PrimitiveType.Cylinder,
            Vector3.zero, Vector3.zero, new Vector3(0.11f, 1.45f, 0.11f), new Color(0.34f, 0.13f, 0.035f), false);
        CreateStaffPart(staffRoot.transform, "Golden Collar", PrimitiveType.Cylinder,
            new Vector3(0f, 1.3f, 0f), Vector3.zero, new Vector3(0.18f, 0.12f, 0.18f), new Color(1f, 0.65f, 0.08f), true);
        CreateStaffPart(staffRoot.transform, "Crook", PrimitiveType.Sphere,
            new Vector3(0.2f, 1.53f, 0f), Vector3.zero, new Vector3(0.52f, 0.38f, 0.18f), new Color(0.37f, 0.15f, 0.04f), false);
        CreateStaffPart(staffRoot.transform, "Crook Cut", PrimitiveType.Sphere,
            new Vector3(0.25f, 1.49f, 0f), Vector3.zero, new Vector3(0.25f, 0.18f, 0.2f), new Color(0.04f, 0.45f, 0.75f), true);
        staffGlow = CreateStaffPart(staffRoot.transform, "Sea Crystal", PrimitiveType.Sphere,
            new Vector3(-0.02f, 1.48f, 0f), Vector3.zero, new Vector3(0.18f, 0.18f, 0.18f),
            new Color(0.05f, 0.8f, 1f), true).transform;
        var light = staffGlow.gameObject.AddComponent<Light>();
        light.color = new Color(0.1f, 0.72f, 1f);
        light.range = 4f;
        light.intensity = 1.8f;
    }

    private static void UpdateStaffModel()
    {
        if (!staffRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var camera = Camera.main.transform;
        var position = anchor.position + camera.right * HandOffset.x + Vector3.up * HandOffset.y + camera.forward * HandOffset.z;
        var flatForward = camera.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.05f) flatForward = Vector3.forward;
        var rotation = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
        staffRoot.transform.position = Vector3.Lerp(staffRoot.transform.position, position, 0.65f);
        staffRoot.transform.rotation = Quaternion.Slerp(staffRoot.transform.rotation, rotation, 0.65f);
        if (staffGlow)
        {
            var pulse = 1f + Mathf.Sin(Time.unscaledTime * 5f) * 0.15f;
            staffGlow.localScale = Vector3.one * 0.18f * pulse;
        }
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!GameInstance.InstanceExists || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static GameObject CreateStaffPart(Transform parent, string name, PrimitiveType primitive,
        Vector3 localPosition, Vector3 localEuler, Vector3 localScale, Color color, bool emissive)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.Destroy(collider);
        part.transform.SetParent(parent, false);
        part.transform.localPosition = localPosition;
        part.transform.localRotation = Quaternion.Euler(localEuler);
        part.transform.localScale = localScale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = color;
            if (emissive && renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", color * 2.2f);
            }
        }
        return part;
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var oldColor = GUI.color;
        GUI.color = new Color(0.15f, 0.82f, 1f, 1f);
        GUI.DrawTexture(new Rect(x - 17f, y - 1.5f, 11f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 6f, y - 1.5f, 11f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1.5f, y - 17f, 3f, 11f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1.5f, y + 6f, 3f, 11f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        GUI.color = oldColor;
    }
}

internal sealed class PartedWaterPath : MonoBehaviour
{
    private readonly List<Collider> waterColliders = new();
    private readonly List<CollisionPair> ignoredPairs = new();
    private readonly List<Transform> waveSegments = new();
    private Vector3 start;
    private Vector3 end;
    private Vector3 direction;
    private float width;
    private float wallHeight;
    private float expiresAt;
    private PlayerCharacter player;
    private bool dryPassageActive;

    internal void Initialize(Vector3 pathStart, Vector3 pathEnd, float pathWidth, float waveHeight,
        float lifetime, float force, PlayerCharacter localPlayer, bool showLights)
    {
        start = pathStart;
        end = pathEnd;
        direction = (end - start).normalized;
        width = pathWidth;
        wallHeight = waveHeight;
        expiresAt = Time.time + lifetime;
        player = localPlayer;
        transform.position = start;
        transform.rotation = Quaternion.LookRotation(direction, Vector3.up);
        FindWaterColliders();
        BuildPath(showLights);
        SweepObjectsAside(force);
    }

    private void Update()
    {
        if (!player)
        {
            Destroy(gameObject);
            return;
        }
        AnimateWaves();
        var position = PlayerPosition();
        var onPath = IsInsidePath(position, 2.2f);
        if (onPath != dryPassageActive) SetDryPassage(onPath);
        if (Time.time < expiresAt) return;
        if (onPath)
        {
            expiresAt = Time.time + 1f;
            return;
        }
        Destroy(gameObject);
    }

    internal void CloseSafely()
    {
        if (player && IsInsidePath(PlayerPosition(), 2.2f))
        {
            var point = PlayerPosition();
            player.SetPlayerPosition((point - start).sqrMagnitude <= (point - end).sqrMagnitude
                ? start - direction * 2f + Vector3.up
                : end + direction * 2f + Vector3.up);
        }
        Destroy(gameObject);
    }

    private bool IsInsidePath(Vector3 point, float padding)
    {
        var local = transform.InverseTransformPoint(point);
        var length = Vector3.Distance(start, end);
        return local.z >= -padding && local.z <= length + padding &&
               Mathf.Abs(local.x) <= width * 0.5f + padding &&
               local.y >= -3f && local.y <= wallHeight + 4f;
    }

    private void FindWaterColliders()
    {
        var length = Vector3.Distance(start, end);
        var center = (start + end) * 0.5f + Vector3.up * wallHeight * 0.35f;
        var overlaps = Physics.OverlapBox(center,
            new Vector3(width * 0.65f, wallHeight, length * 0.5f + 2f), transform.rotation, ~0,
            QueryTriggerInteraction.Collide);
        foreach (var collider in overlaps)
            if (collider && collider.GetComponentInParent<Water>() && !waterColliders.Contains(collider))
                waterColliders.Add(collider);
    }

    private void SetDryPassage(bool active)
    {
        if (active)
        {
            ignoredPairs.Clear();
            foreach (var playerCollider in player.GetComponentsInChildren<Collider>(true))
            foreach (var waterCollider in waterColliders)
            {
                if (!playerCollider || !waterCollider || playerCollider == waterCollider) continue;
                Physics.IgnoreCollision(playerCollider, waterCollider, true);
                ignoredPairs.Add(new CollisionPair(playerCollider, waterCollider));
            }
        }
        else RestoreWaterCollision();
        dryPassageActive = active;
    }

    private void RestoreWaterCollision()
    {
        foreach (var pair in ignoredPairs)
            if (pair.PlayerCollider && pair.WaterCollider)
                Physics.IgnoreCollision(pair.PlayerCollider, pair.WaterCollider, false);
        ignoredPairs.Clear();
        dryPassageActive = false;
    }

    private void BuildPath(bool showLights)
    {
        var length = Vector3.Distance(start, end);
        CreatePart("Dry Sea Floor", new Vector3(0f, -0.16f, length * 0.5f),
            new Vector3(width, 0.3f, length + 3f), new Color(0.78f, 0.64f, 0.34f), true, false);

        var segmentCount = Mathf.Clamp(Mathf.CeilToInt(length / 3f), 5, 40);
        for (var side = -1; side <= 1; side += 2)
        for (var i = 0; i < segmentCount; i++)
        {
            var z = (i + 0.5f) * length / segmentCount;
            var segment = CreatePart("Parted Water Wall", new Vector3(side * (width * 0.5f + 0.65f), wallHeight * 0.48f, z),
                new Vector3(1.25f, wallHeight, length / segmentCount + 0.35f),
                new Color(0.02f, 0.48f, 0.9f, 0.68f), false, true).transform;
            segment.gameObject.AddComponent<WaveMarker>().Configure(side, i);
            waveSegments.Add(segment);
        }

        for (var i = 0; i <= 6; i++)
        {
            var z = length * i / 6f;
            CreatePart("Golden Path Marker", new Vector3(-width * 0.43f, 0.06f, z),
                new Vector3(0.16f, 0.16f, 0.65f), new Color(1f, 0.72f, 0.12f), false, true);
            CreatePart("Golden Path Marker", new Vector3(width * 0.43f, 0.06f, z),
                new Vector3(0.16f, 0.16f, 0.65f), new Color(1f, 0.72f, 0.12f), false, true);
            if (!showLights) continue;
            var lightObject = new GameObject("Parted Water Light");
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localPosition = new Vector3(0f, 1f, z);
            var light = lightObject.AddComponent<Light>();
            light.color = new Color(0.1f, 0.65f, 1f);
            light.range = width * 1.3f;
            light.intensity = 1.1f;
        }
    }

    private GameObject CreatePart(string partName, Vector3 localPosition, Vector3 localScale, Color color,
        bool colliderEnabled, bool transparent)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
        part.name = partName;
        part.transform.SetParent(transform, false);
        part.transform.localPosition = localPosition;
        part.transform.localScale = localScale;
        var collider = part.GetComponent<Collider>();
        if (collider && !colliderEnabled) Destroy(collider);
        var renderer = part.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = color;
            if (transparent)
            {
                renderer.material.SetFloat("_Mode", 3f);
                renderer.material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                renderer.material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                renderer.material.SetInt("_ZWrite", 0);
                renderer.material.DisableKeyword("_ALPHATEST_ON");
                renderer.material.EnableKeyword("_ALPHABLEND_ON");
                renderer.material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                renderer.material.renderQueue = 3000;
            }
            if (renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", color * 0.35f);
            }
        }
        return part;
    }

    private void AnimateWaves()
    {
        foreach (var segment in waveSegments)
        {
            if (!segment) continue;
            var marker = segment.GetComponent<WaveMarker>();
            var wave = Mathf.Sin(Time.time * 2.8f + marker.Index * 0.72f) * 0.28f;
            var position = segment.localPosition;
            position.x = marker.Side * (width * 0.5f + 0.65f + wave);
            position.y = wallHeight * 0.48f + Mathf.Sin(Time.time * 2.1f + marker.Index) * 0.18f;
            segment.localPosition = position;
        }
    }

    private void SweepObjectsAside(float force)
    {
        var length = Vector3.Distance(start, end);
        var center = (start + end) * 0.5f + Vector3.up * 1.5f;
        var overlaps = Physics.OverlapBox(center, new Vector3(width * 0.7f, wallHeight, length * 0.5f),
            transform.rotation, ~0, QueryTriggerInteraction.Ignore);
        var pushed = new HashSet<Rigidbody>();
        foreach (var collider in overlaps)
        {
            var body = collider ? collider.attachedRigidbody : null;
            if (!body || body.isKinematic || pushed.Contains(body) ||
                collider.GetComponentInParent<PlayerCharacter>() == player) continue;
            pushed.Add(body);
            var local = transform.InverseTransformPoint(body.worldCenterOfMass);
            var side = local.x < 0f ? -transform.right : transform.right;
            body.AddForce(side * force + Vector3.up * force * 0.18f, ForceMode.VelocityChange);
        }
    }

    private Vector3 PlayerPosition()
    {
        var body = player ? player.GetComponentInChildren<PlayerBody>(true) : null;
        var rigidbody = body ? body.GetRigidbody() : null;
        return rigidbody ? rigidbody.position : player.transform.position;
    }

    private void OnDestroy()
    {
        RestoreWaterCollision();
        MosesStaffMod.Unregister(this);
    }

    private sealed class CollisionPair
    {
        internal readonly Collider PlayerCollider;
        internal readonly Collider WaterCollider;
        internal CollisionPair(Collider playerCollider, Collider waterCollider)
        {
            PlayerCollider = playerCollider;
            WaterCollider = waterCollider;
        }
    }
}

internal sealed class WaveMarker : MonoBehaviour
{
    internal int Side { get; private set; }
    internal int Index { get; private set; }
    internal void Configure(int side, int index)
    {
        Side = side;
        Index = index;
    }
}

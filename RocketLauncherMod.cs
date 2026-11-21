using System;
using System.Collections.Generic;
using System.Linq;
using HawkNetworking;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using UnityEngine.AI;

namespace WobblyLifeExtraMods;

/// <summary>A rapid-fire launcher for small networked props, with optional impact detonation.</summary>
public sealed class RocketLauncherMod : BaseMod
{
    private const string TntAddress = "TNT";
    private const string TntNetworkId = "a4698caf71b91d242a93797c3d7a016f";
    private const float CrosshairGap = 7f;
    private const float CrosshairLength = 10f;
    private const float CrosshairThickness = 2f;

    private static readonly Ref<string[]> DisplayItems = new(Array.Empty<string>());
    private static readonly Ref<int> SelectedIndex = new();
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<bool> ContactBombMode = new();
    private static readonly Ref<string> Status = new("Rocket prop launcher is unequipped.");

    private static List<AssetDatabase.AssetEntry> entries = new();
    private static AssetDatabase.AssetEntry tntEntry;
    private static float nextFireTime;
    private static readonly List<GameObject> ChristmasTrees = new();
    private static readonly List<LaunchedWobblyProjectile> LaunchedWobblies = new();
    private static int pendingWobblies;
    private const int MaximumLaunchedWobblies = 12;

    public override string Name => "Rocket Prop Launcher";

    public override string Description =>
        "Launch searchable handheld props at high speed, or fire TNT rockets that explode on impact.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 10f, Max = 200f, Label = "Launch velocity")]
    public static Ref<float> LaunchVelocity = new(70f);

    [ModSetting(Order = 20, Min = 0.05f, Max = 1.5f, Label = "Rapid-fire interval")]
    public static Ref<float> RapidFireInterval = new(0.2f);

    [ModSetting(Order = 30, Min = 1.5f, Max = 8f, Label = "Muzzle distance",
        Description = "Distance in front of the camera where each projectile appears.")]
    public static Ref<float> MuzzleDistance = new(3f);

    [ModSetting(Order = 40, Min = -400f, Max = 400f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(100f);

    [ModSetting(Order = 50, Min = -250f, Max = 250f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(35f);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += RefreshCatalog;
        if (AssetDatabase.IsInitialized) RefreshCatalog();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("RocketLauncherHelp",
                "Search for a prop or select a candy cane, snowman, Christmas tree, or Wobbly, equip, close F2, " +
                "then aim with the red crosshair and hold left-click. The Wobbly wanders for 30 seconds, sits for three minutes, and despawns. Trees are local; Wobblies use a network NPC. Contact bomb mode always fires TNT."),
            new SearchableCombo("Projectile prop", Array.Empty<string>())
                .WithItems(DisplayItems)
                .WithSelectedIndex(SelectedIndex),
            new Checkbox("Contact bomb mode", false).WithValue(ContactBombMode),
            base.BuildPanel(id),
            new HStack("RocketLauncherActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire)),
                ActionMenu(new Button("Refresh props", RefreshCatalog), nameof(RefreshCatalog))
            ).WithContentWidth(),
            new HStack("WinterProjectileActions",
                ActionMenu(new Button("Select candy cane", SelectCandyCane), nameof(SelectCandyCane)),
                ActionMenu(new Button("Select snowman", SelectSnowman), nameof(SelectSnowman)),
                ActionMenu(new Button("Select Christmas tree", SelectChristmasTree), nameof(SelectChristmasTree)),
                ActionMenu(new Button("Select Wobbly", SelectWobbly), nameof(SelectWobbly))
            ).WithContentWidth(),
            new TextWrapped("RocketLauncherStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void Equip()
    {
        WindCannonMod.Unequip();
        PropSpawnerGunMod.Unequip();
        VehicleAircraftSpawnerMod.Unequip();
        PaintballGunMod.Unequip();
        MinecraftBuildingMod.Unequip();
        HeavyAutomaticGunMod.Unequip();
        GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip();
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
        MeteorShowerCannonMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Rocket prop launcher equipped. Close F2, aim, and hold left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Rocket prop launcher unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (Time.unscaledTime < nextFireTime) return;

        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can fire networked projectiles.";
            return;
        }

        var customTree = !ContactBombMode.Value && DisplayItems.Value.Length > entries.Count && SelectedIndex.Value == entries.Count;
        var wobbly = !ContactBombMode.Value && DisplayItems.Value.Length > entries.Count + 1 && SelectedIndex.Value == entries.Count + 1;
        var entry = ContactBombMode.Value ? tntEntry : SelectedEntry();
        if (entry == null && !customTree && !wobbly)
        {
            Status.Value = ContactBombMode.Value
                ? "TNT was not found in this game build. Refresh the projectile list."
                : "Select a projectile prop after the asset scan finishes.";
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before firing.";
            return;
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.05f, RapidFireInterval.Value);

        var screenPoint = new Vector3(
            Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value,
            0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        var position = ray.origin + ray.direction * Mathf.Max(1.5f, MuzzleDistance.Value);
        var rotation = Quaternion.LookRotation(ray.direction, camera.transform.up);
        if (customTree)
        {
            LaunchChristmasTree(position, rotation, ray.direction);
            return;
        }
        if (wobbly)
        {
            LaunchWobbly(position, rotation, ray.direction);
            return;
        }
        var label = ContactBombMode.Value ? "contact bomb" : FriendlyName(entry);

        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = entry.LoadKey,
            NetworkAssetId = entry.NetworkAssetId,
            Guid = entry.Guid,
            AssetName = label,
            Networked = true
        };

        var candidates = PropAddressResolver.Fallbacks(identity).ToList();
        Status.Value = $"Launching {label}...";
        TryNetworkSpawn(candidates, 0, position, rotation, ray.direction, identity, label, ContactBombMode.Value,
            ContactBombMode.Value ? 0 : WinterProjectileKind(entry));
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshCatalog()
    {
        var customSelected = DisplayItems.Value.Length > entries.Count && SelectedIndex.Value >= entries.Count
            ? SelectedIndex.Value - entries.Count : -1;
        var previousKey = SelectedIndex.Value >= 0 && SelectedIndex.Value < entries.Count
            ? entries[SelectedIndex.Value].LoadKey
            : null;

        entries = AssetDatabase.Entries
            .Where(IsProjectileProp)
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LoadKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        tntEntry = AssetDatabase.Entries.FirstOrDefault(entry =>
            string.Equals(entry.NetworkAssetId, TntNetworkId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.LoadKey, TntAddress, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.Address, TntAddress, StringComparison.OrdinalIgnoreCase));

        DisplayItems.Value = entries.Select(entry =>
        {
            var name = FriendlyName(entry);
            return string.Equals(name, entry.LoadKey, StringComparison.OrdinalIgnoreCase)
                ? name
                : $"{name}  —  {entry.LoadKey}";
        }).Concat(new[] { "Christmas tree (custom, local)", "Wobbly (30s wandering, 3m sitting)" }).ToArray();

        var restored = previousKey == null
            ? 0
            : entries.FindIndex(entry => string.Equals(entry.LoadKey, previousKey, StringComparison.OrdinalIgnoreCase));
        SelectedIndex.Value = customSelected >= 0 && customSelected < 2 ? entries.Count + customSelected : restored >= 0 ? restored : 0;
        Status.Value = entries.Count == 0
            ? "The asset database is not ready. Wait for its scan and refresh props."
            : $"Ready: {entries.Count:N0} searchable projectile props; contact TNT {(tntEntry == null ? "not found" : "ready")}.";
    }

    public override void Update()
    {
        if (!EquippedState.Value || Cursor.visible) return;
        if (Input.GetMouseButton(0)) Fire();
    }

    private static AssetDatabase.AssetEntry SelectedEntry()
    {
        var index = SelectedIndex.Value;
        return index >= 0 && index < entries.Count ? entries[index] : null;
    }

    private static bool IsProjectileProp(AssetDatabase.AssetEntry entry)
    {
        if (!entry.IsGameObject || string.IsNullOrEmpty(entry.LoadKey) || string.IsNullOrEmpty(entry.NetworkAssetId))
            return false;

        if (WinterProjectileKind(entry) != 0) return true;
        if (!entry.HasComponent("Rigidbody")) return false;

        return entry.HasComponent("GrabStat") || entry.HasComponent("Food") ||
               entry.HasComponent("Bomb") || entry.HasComponent("MoneyBag");
    }

    private static int WinterProjectileKind(AssetDatabase.AssetEntry entry)
    {
        if (entry == null) return 0;
        foreach (var value in new[] { entry.Name, entry.LoadKey, entry.Address })
        {
            if (string.IsNullOrEmpty(value)) continue;
            var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
            var name = slash >= 0 ? value.Substring(slash + 1) : value;
            if (name.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 7);
            var normalized = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (normalized == "candycane") return 1;
            if (normalized == "snowman") return 2;
        }
        return 0;
    }

    [ModAction(ShowInUI = false)] public static void SelectCandyCane() => SelectWinterProjectile(1, "candy cane");
    [ModAction(ShowInUI = false)] public static void SelectSnowman() => SelectWinterProjectile(2, "snowman");
    [ModAction(ShowInUI = false)] public static void SelectChristmasTree()
    {
        if (DisplayItems.Value.Length == 0) RefreshCatalog();
        SelectedIndex.Value = entries.Count;
        ContactBombMode.Value = false;
        Status.Value = "Selected custom Christmas tree (local only). Equip and hold left-click.";
    }

    [ModAction(ShowInUI = false)] public static void SelectWobbly()
    {
        if (DisplayItems.Value.Length <= entries.Count + 1) RefreshCatalog();
        SelectedIndex.Value = entries.Count + 1;
        ContactBombMode.Value = false;
        Status.Value = "Selected Wobbly. It wanders for 30 seconds, then sits for three minutes.";
    }

    private static void LaunchWobbly(Vector3 position, Quaternion rotation, Vector3 direction)
    {
        LaunchedWobblies.RemoveAll(item => !item);
        if (LaunchedWobblies.Count + pendingWobblies >= MaximumLaunchedWobblies)
        {
            Status.Value = "Wobbly limit reached (12). Wait for one to despawn before launching more.";
            return;
        }
        var npc = AssetDatabase.Entries.FirstOrDefault(item => item.IsGameObject &&
            (string.Equals(item.LoadKey, "NPC Granny", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Name, "NPC Granny", StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(item.NetworkAssetId));
        if (npc == null) { Status.Value = "Network Wobbly prefab not ready. Wait for the asset scan and refresh."; return; }
        pendingWobblies++;
        var candidates = new[] { npc.NetworkAssetId, npc.LoadKey, "NPC Granny" }
            .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        SpawnWobbly(candidates, 0, position, rotation, direction, Mathf.Max(1f, LaunchVelocity.Value));
    }

    private static void SpawnWobbly(IReadOnlyList<string> candidates, int index, Vector3 position,
        Quaternion rotation, Vector3 direction, float speed)
    {
        if (index >= candidates.Count) { pendingWobblies = Mathf.Max(0, pendingWobblies - 1); Status.Value = "Failed to spawn the network Wobbly."; return; }
        NetworkPrefab.SpawnNetworkPrefab(candidates[index], behaviour =>
        {
            if (!behaviour) { SpawnWobbly(candidates, index + 1, position, rotation, direction, speed); return; }
            pendingWobblies = Mathf.Max(0, pendingWobblies - 1);
            if (!GameInstance.InstanceExists || !PropSpawnManager.IsServer)
            {
                if (behaviour.networkObject != null) behaviour.networkObject.Destroy(); else UnityEngine.Object.Destroy(behaviour.gameObject);
                return;
            }
            var npcObject = behaviour.gameObject;
            var controller = npcObject.GetComponentInChildren<PlayerNPCController>(true);
            if (!controller)
            {
                Status.Value = "Spawned prefab has no Wobbly controller; removing it.";
                if (behaviour.networkObject != null) behaviour.networkObject.Destroy(); else UnityEngine.Object.Destroy(npcObject);
                return;
            }
            npcObject.name = "Rocket launcher Wobbly";
            StreetNpcPopulationMod.PrepareSpawnedNpc(npcObject, position);
            IgnoreShooterCollisions(npcObject, true);
            var launched = npcObject.AddComponent<LaunchedWobblyProjectile>();
            launched.Initialize(controller, behaviour.networkObject, direction, speed);
            LaunchedWobblies.Add(launched);
            Status.Value = "Launched a Wobbly! It moves 30 seconds, sits 3 minutes, then despawns.";
        }, position: position, rotation: rotation, owner: null, bUseChunkSystem: false, bSendTransform: true, bCheckChunk: false);
    }

    private static void LaunchChristmasTree(Vector3 position, Quaternion rotation, Vector3 direction)
    {
        ChristmasTrees.RemoveAll(tree => !tree);
        while (ChristmasTrees.Count >= 16)
        {
            if (ChristmasTrees[0]) UnityEngine.Object.Destroy(ChristmasTrees[0]);
            ChristmasTrees.RemoveAt(0);
        }
        var root = new GameObject("Christmas tree projectile");
        root.transform.SetPositionAndRotation(position, rotation);
        var resources = root.AddComponent<ChristmasTreeProjectile>();
        resources.Build();
        var collider = root.AddComponent<CapsuleCollider>();
        collider.center = new Vector3(0f, 1.15f, 0f); collider.height = 2.3f; collider.radius = 0.55f;
        var body = root.AddComponent<Rigidbody>();
        body.mass = 2f; body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.velocity = direction.normalized * Mathf.Max(1f, LaunchVelocity.Value);
        IgnoreShooterCollisions(root, true);
        ChristmasTrees.Add(root);
        Status.Value = "Launched a custom Christmas tree (local only; disappears after 30 seconds).";
    }

    private static void SelectWinterProjectile(int kind, string label)
    {
        var index = entries.FindIndex(entry => WinterProjectileKind(entry) == kind);
        if (index < 0)
        {
            RefreshCatalog();
            index = entries.FindIndex(entry => WinterProjectileKind(entry) == kind);
        }
        if (index < 0)
        {
            Status.Value = $"No network-spawnable {label} found. Wait for the updated asset scan, then refresh props.";
            return;
        }
        SelectedIndex.Value = index;
        ContactBombMode.Value = false;
        Status.Value = $"Selected {label}. Equip the launcher and hold left-click.";
    }

    private static void TryNetworkSpawn(
        IReadOnlyList<string> candidates,
        int index,
        Vector3 position,
        Quaternion rotation,
        Vector3 direction,
        PropIdentity identity,
        string label,
        bool contactBomb,
        int winterProjectileKind)
    {
        if (index >= candidates.Count)
        {
            Status.Value = $"Failed to launch {label}: no registered network key worked.";
            return;
        }

        NetworkPrefab.SpawnNetworkPrefab(
            candidates[index],
            behaviour =>
            {
                if (behaviour == null)
                {
                    TryNetworkSpawn(candidates, index + 1, position, rotation, direction, identity, label, contactBomb, winterProjectileKind);
                    return;
                }

                var spawned = behaviour.gameObject;
                PropSpawnManager.Register(spawned, identity, null, null);
                PrepareProjectile(spawned, direction, contactBomb, winterProjectileKind);
                Status.Value = $"Launched {label}.";
                Plugin.Log?.LogInfo(Status.Value);
            },
            position: position,
            rotation: rotation,
            owner: null,
            bUseChunkSystem: true,
            bSendTransform: true,
            bCheckChunk: true);
    }

    private static void PrepareProjectile(GameObject spawned, Vector3 direction, bool contactBomb, int winterProjectileKind)
    {
        var winterProjectile = winterProjectileKind != 0;
        var bodies = spawned.GetComponentsInChildren<Rigidbody>(true);
        if (winterProjectile && bodies.Length == 0)
        {
            if (spawned.GetComponentsInChildren<Collider>(true).Length == 0)
                spawned.AddComponent<BoxCollider>();
            bodies = new[] { spawned.AddComponent<Rigidbody>() };
        }
        foreach (var body in bodies)
        {
            if (!body) continue;
            if (winterProjectile && body.isKinematic) body.isKinematic = false;
            if (body.isKinematic) continue;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.WakeUp();
            body.velocity = direction.normalized * Mathf.Max(1f, LaunchVelocity.Value);
        }

        if (winterProjectileKind == 1)
        {
            foreach (var grab in spawned.GetComponentsInChildren<GrabStat>(true))
                if (grab) grab.SetIsGrabbable(true);
        }
        IgnoreShooterCollisions(spawned, winterProjectile);

        if (contactBomb)
        {
            var impact = spawned.AddComponent<ContactBombProjectile>();
            impact.Arm(spawned.GetComponent<Bomb>() ?? spawned.GetComponentInChildren<Bomb>(true));
        }
    }

    private static void IgnoreShooterCollisions(GameObject projectile, bool restoreAfterLaunch)
    {
        if (!GameInstance.InstanceExists) return;

        var controller = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!character) return;

        var projectileColliders = projectile.GetComponentsInChildren<Collider>(true);
        var shooterColliders = character.GetComponentsInChildren<Collider>(true);
        var ignoredPairs = restoreAfterLaunch ? new List<ProjectileCollisionPair>() : null;
        foreach (var projectileCollider in projectileColliders)
        foreach (var shooterCollider in shooterColliders)
        {
            if (!projectileCollider || !shooterCollider || Physics.GetIgnoreCollision(projectileCollider, shooterCollider)) continue;
            Physics.IgnoreCollision(projectileCollider, shooterCollider, true);
            ignoredPairs?.Add(new ProjectileCollisionPair(projectileCollider, shooterCollider));
        }
        if (ignoredPairs != null && ignoredPairs.Count > 0)
            projectile.AddComponent<RestoreProjectileCollisions>().Initialize(ignoredPairs);
    }

    private static string FriendlyName(AssetDatabase.AssetEntry entry)
    {
        var value = !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : entry.LoadKey;
        if (string.IsNullOrWhiteSpace(value)) return "Unnamed prop";

        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slash >= 0) value = value.Substring(slash + 1);
        if (value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - ".prefab".Length);
        return value.Replace('_', ' ');
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;

        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var previous = GUI.color;
        GUI.color = ContactBombMode.Value
            ? new Color(1f, 0.2f, 0.08f, 0.98f)
            : new Color(1f, 0.48f, 0.12f, 0.95f);

        DrawRect(x - CrosshairGap - CrosshairLength, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x + CrosshairGap, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x - CrosshairThickness * 0.5f, y - CrosshairGap - CrosshairLength, CrosshairThickness, CrosshairLength);
        DrawRect(x - CrosshairThickness * 0.5f, y + CrosshairGap, CrosshairThickness, CrosshairLength);
        DrawRect(x - 2f, y - 2f, 4f, 4f);

        GUI.color = previous;
    }

    private static void DrawRect(float x, float y, float width, float height)
        => GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
}

/// <summary>Small self-cleaning decorated tree; its visuals and physics are local to the shooter.</summary>
internal sealed class ChristmasTreeProjectile : MonoBehaviour
{
    private readonly List<Material> materials = new();
    private Mesh cone;
    private float expiresAt;

    internal void Build()
    {
        expiresAt = Time.unscaledTime + 30f;
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        var green = CreateMaterial(shader, new Color(0.04f, 0.38f, 0.12f));
        var brown = CreateMaterial(shader, new Color(0.34f, 0.16f, 0.06f));
        var red = CreateMaterial(shader, new Color(0.9f, 0.08f, 0.08f));
        var gold = CreateMaterial(shader, new Color(1f, 0.78f, 0.12f));
        var blue = CreateMaterial(shader, new Color(0.15f, 0.45f, 1f));
        cone = CreateCone();
        MakePrimitive("Trunk", PrimitiveType.Cylinder, new Vector3(0f, 0.25f, 0f), new Vector3(0.28f, 0.25f, 0.28f), brown);
        for (var i = 0; i < 3; i++)
        {
            var foliage = new GameObject("Tree foliage");
            foliage.transform.SetParent(transform, false);
            foliage.transform.localPosition = new Vector3(0f, 0.35f + i * 0.48f, 0f);
            foliage.transform.localScale = new Vector3(1.05f - i * 0.2f, 1.05f, 1.05f - i * 0.2f);
            foliage.AddComponent<MeshFilter>().sharedMesh = cone;
            foliage.AddComponent<MeshRenderer>().sharedMaterial = green;
        }
        for (var i = 0; i < 12; i++)
        {
            var angle = i * Mathf.PI * 2f / 6f;
            var upper = i >= 6;
            var radius = upper ? 0.25f : 0.39f;
            MakePrimitive("Ornament", PrimitiveType.Sphere,
                new Vector3(Mathf.Cos(angle) * radius, upper ? 1.32f : 0.84f, Mathf.Sin(angle) * radius),
                Vector3.one * 0.13f, i % 3 == 0 ? red : i % 3 == 1 ? gold : blue);
        }
        MakePrimitive("Tree topper", PrimitiveType.Sphere, new Vector3(0f, 2.38f, 0f), Vector3.one * 0.18f, gold);
    }

    private Material CreateMaterial(Shader shader, Color color)
    {
        if (!shader) return null;
        var material = new Material(shader) { color = color };
        materials.Add(material); return material;
    }

    private void MakePrimitive(string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
    {
        var part = GameObject.CreatePrimitive(type);
        part.name = name;
        var partCollider = part.GetComponent<Collider>();
        if (partCollider) Destroy(partCollider);
        part.transform.SetParent(transform, false);
        part.transform.localPosition = position; part.transform.localScale = scale;
        if (material) part.GetComponent<Renderer>().sharedMaterial = material;
    }

    private static Mesh CreateCone()
    {
        const int sides = 16;
        var vertices = new Vector3[sides + 2];
        var triangles = new int[sides * 6];
        vertices[0] = Vector3.up; vertices[1] = Vector3.zero;
        for (var i = 0; i < sides; i++)
        {
            var angle = i * Mathf.PI * 2f / sides;
            vertices[i + 2] = new Vector3(Mathf.Cos(angle) * 0.5f, 0f, Mathf.Sin(angle) * 0.5f);
            var next = 2 + (i + 1) % sides;
            var offset = i * 6;
            triangles[offset] = 0; triangles[offset + 1] = next; triangles[offset + 2] = i + 2;
            triangles[offset + 3] = 1; triangles[offset + 4] = i + 2; triangles[offset + 5] = next;
        }
        var result = new Mesh { name = "Christmas tree cone", vertices = vertices, triangles = triangles };
        result.RecalculateNormals(); result.RecalculateBounds(); return result;
    }

    private void Update() { if (Time.unscaledTime >= expiresAt) Destroy(gameObject); }
    private void OnDestroy()
    {
        if (cone) Destroy(cone);
        foreach (var material in materials) if (material) Destroy(material);
        materials.Clear();
    }
}

internal readonly struct ProjectileCollisionPair
{
    internal readonly Collider Projectile;
    internal readonly Collider Shooter;
    internal ProjectileCollisionPair(Collider projectile, Collider shooter) { Projectile = projectile; Shooter = shooter; }
}

/// <summary>Server-owned Wobbly: launched, wandering for 30 seconds, then sitting for 180 seconds.</summary>
internal sealed class LaunchedWobblyProjectile : MonoBehaviour
{
    private PlayerNPCController npc;
    private HawkNetworkObject network;
    private RagdollController ragdoll;
    private PlayerBody playerBody;
    private NavMeshAgent agent;
    private float bornAt;
    private float nextMove;
    private float nextAppearance;
    private int appearancePasses;
    private Vector3 destination;
    private bool hasDestination;
    private bool recovered;
    private bool sitting;

    internal void Initialize(PlayerNPCController controller, HawkNetworkObject networkObject, Vector3 direction, float speed)
    {
        npc = controller; network = networkObject; bornAt = Time.unscaledTime;
        ragdoll = GetComponentInChildren<RagdollController>(true);
        playerBody = GetComponentInChildren<PlayerBody>(true);
        ApplyDefaultAppearance();
        nextAppearance = bornAt + 0.8f;
        if (ragdoll) ragdoll.Ragdoll();
        if (playerBody) playerBody.SetRagdollVelocity(direction.normalized * speed);
        else
        {
            foreach (var body in GetComponentsInChildren<Rigidbody>(true))
                if (body && !body.isKinematic) body.velocity = direction.normalized * speed;
        }
        Plugin.Log?.LogInfo("Rocket Wobbly launched; 30 seconds moving, 180 seconds sitting.");
    }

    private void Update()
    {
        var age = Time.unscaledTime - bornAt;
        if (age >= 210f) { Despawn(); return; }
        if (!npc) { Despawn(); return; }
        if (appearancePasses < 2 && Time.unscaledTime >= nextAppearance)
        {
            ApplyDefaultAppearance(); appearancePasses++; nextAppearance = Time.unscaledTime + 1.2f;
        }
        if (age >= 30f)
        {
            if (!sitting) BeginSitting();
            return;
        }
        if (!recovered && age >= 1.2f)
        {
            recovered = true;
            if (ragdoll && ragdoll.IsActiveRagdoll()) ragdoll.Wakeup(true);
            agent = GetComponent<NavMeshAgent>() ?? gameObject.AddComponent<NavMeshAgent>();
            agent.radius = 0.35f; agent.height = 1.75f; agent.speed = 2.1f; agent.acceleration = 10f;
            agent.angularSpeed = 280f; agent.stoppingDistance = 0.25f;
            if (!agent.isOnNavMesh) agent.enabled = false;
            npc.SetAnimation(new NPCAnimation { bWalking = true }, true, true);
        }
        if (recovered && (!ragdoll || !ragdoll.IsActiveRagdoll())) Wander();
    }

    private void ApplyDefaultAppearance()
    {
        var appearance = npc ? npc.GetCharacterCustomize() : null;
        if (!appearance) return;
        appearance.RemoveAllClothes(true);
        appearance.SetCharacterColor(new Color(1f, 0.8f, 0.16f, 1f), false, -1f);
    }

    private void Wander()
    {
        if (Time.unscaledTime >= nextMove)
        {
            nextMove = Time.unscaledTime + UnityEngine.Random.Range(2.5f, 5f);
            hasDestination = StreetNpcPopulationMod.TryFindWanderDestination(transform.position, out destination);
            if (hasDestination && agent && agent.enabled && agent.isOnNavMesh) agent.SetDestination(destination);
        }
        if (!hasDestination || (agent && agent.enabled && agent.isOnNavMesh)) return;
        var offset = destination - transform.position; offset.y = 0f;
        if (offset.sqrMagnitude < 0.3f) { hasDestination = false; return; }
        var direction = offset.normalized;
        if (Physics.SphereCast(transform.position + Vector3.up * 0.75f, 0.28f, direction,
            out var obstacle, 0.75f, ~0, QueryTriggerInteraction.Ignore) && !obstacle.transform.IsChildOf(transform))
        { hasDestination = false; return; }
        var next = Vector3.MoveTowards(transform.position, destination, 2.1f * Time.deltaTime);
        if (StreetNpcPopulationMod.TrySampleWalkableGround(next, 1.5f, out var ground)) next.y = ground.y;
        transform.position = next;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction, Vector3.up), Time.deltaTime * 7f);
    }

    private void BeginSitting()
    {
        sitting = true; hasDestination = false;
        if (agent && agent.enabled) { if (agent.isOnNavMesh) agent.ResetPath(); agent.enabled = false; }
        if (ragdoll && ragdoll.IsActiveRagdoll()) ragdoll.Wakeup(true);
        if (playerBody) playerBody.SetRagdollVelocity(Vector3.zero);
        var feet = playerBody ? playerBody.GetCenterFeetPosition() : transform.position;
        if (StreetNpcPopulationMod.TrySampleWalkableGround(feet, 1f, out var ground) &&
            (ground - feet).sqrMagnitude < 4f)
        {
            transform.position += ground - feet;
            Physics.SyncTransforms();
        }
        npc.SetAnimation(new NPCAnimation { bSitdown = true }, true, true);
        Plugin.Log?.LogInfo("Rocket Wobbly is sitting; it will despawn after three minutes.");
    }

    private void Despawn()
    {
        enabled = false;
        if (network != null && network.IsServer()) network.Destroy();
        else Destroy(gameObject);
    }
}

internal sealed class RestoreProjectileCollisions : MonoBehaviour
{
    private List<ProjectileCollisionPair> pairs;
    private float restoreAt;
    internal void Initialize(List<ProjectileCollisionPair> ignoredPairs)
    {
        pairs = ignoredPairs;
        restoreAt = Time.unscaledTime + 1f;
    }
    private void Update() { if (Time.unscaledTime >= restoreAt) Restore(); }
    private void OnDestroy() => Restore();
    private void Restore()
    {
        if (pairs == null) return;
        foreach (var pair in pairs)
            if (pair.Projectile && pair.Shooter) Physics.IgnoreCollision(pair.Projectile, pair.Shooter, false);
        pairs = null;
        Destroy(this);
    }
}

/// <summary>Host-side collision trigger attached only to contact-mode TNT projectiles.</summary>
internal sealed class ContactBombProjectile : MonoBehaviour
{
    private Bomb bomb;
    private float armedAt;
    private bool exploded;

    internal void Arm(Bomb target)
    {
        bomb = target;
        armedAt = Time.time + 0.02f;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (exploded || Time.time < armedAt || bomb == null) return;
        exploded = true;
        bomb.Explode();
        Destroy(this);
    }
}

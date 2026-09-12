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
                "Search for a small prop (fish, bombs, TNT, food, and other grabbable objects), equip, close F2, " +
                "then aim with the red crosshair and hold left-click. Contact bomb mode always fires TNT that explodes on impact."),
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

        var entry = ContactBombMode.Value ? tntEntry : SelectedEntry();
        if (entry == null)
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
        TryNetworkSpawn(candidates, 0, position, rotation, ray.direction, identity, label, ContactBombMode.Value);
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshCatalog()
    {
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
        }).ToArray();

        var restored = previousKey == null
            ? 0
            : entries.FindIndex(entry => string.Equals(entry.LoadKey, previousKey, StringComparison.OrdinalIgnoreCase));
        SelectedIndex.Value = restored >= 0 ? restored : 0;
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
        if (!entry.IsGameObject || string.IsNullOrEmpty(entry.LoadKey) ||
            string.IsNullOrEmpty(entry.NetworkAssetId) || !entry.HasComponent("Rigidbody"))
            return false;

        return entry.HasComponent("GrabStat") || entry.HasComponent("Food") ||
               entry.HasComponent("Bomb") || entry.HasComponent("MoneyBag");
    }

    private static void TryNetworkSpawn(
        IReadOnlyList<string> candidates,
        int index,
        Vector3 position,
        Quaternion rotation,
        Vector3 direction,
        PropIdentity identity,
        string label,
        bool contactBomb)
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
                    TryNetworkSpawn(candidates, index + 1, position, rotation, direction, identity, label, contactBomb);
                    return;
                }

                var spawned = behaviour.gameObject;
                PropSpawnManager.Register(spawned, identity, null, null);
                PrepareProjectile(spawned, direction, contactBomb);
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

    private static void PrepareProjectile(GameObject spawned, Vector3 direction, bool contactBomb)
    {
        var bodies = spawned.GetComponentsInChildren<Rigidbody>(true);
        foreach (var body in bodies)
        {
            if (!body || body.isKinematic) continue;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.WakeUp();
            body.velocity = direction.normalized * Mathf.Max(1f, LaunchVelocity.Value);
        }

        IgnoreShooterCollisions(spawned);

        if (contactBomb)
        {
            var impact = spawned.AddComponent<ContactBombProjectile>();
            impact.Arm(spawned.GetComponent<Bomb>() ?? spawned.GetComponentInChildren<Bomb>(true));
        }
    }

    private static void IgnoreShooterCollisions(GameObject projectile)
    {
        if (!GameInstance.InstanceExists) return;

        var controller = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!character) return;

        var projectileColliders = projectile.GetComponentsInChildren<Collider>(true);
        var shooterColliders = character.GetComponentsInChildren<Collider>(true);
        foreach (var projectileCollider in projectileColliders)
        foreach (var shooterCollider in shooterColliders)
        {
            if (projectileCollider && shooterCollider)
                Physics.IgnoreCollision(projectileCollider, shooterCollider, true);
        }
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

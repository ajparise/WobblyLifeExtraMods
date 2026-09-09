using System;
using System.Collections;
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
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace WobblyLifeExtraMods;

/// <summary>A searchable, crosshair-aimed prop placement tool.</summary>
public sealed class PropSpawnerGunMod : BaseMod
{
    private const float CrosshairGap = 6f;
    private const float CrosshairLength = 8f;
    private const float CrosshairThickness = 2f;

    private static readonly Ref<string[]> DisplayItems = new(Array.Empty<string>());
    private static readonly Ref<int> SelectedIndex = new();
    private static readonly Ref<bool> Networked = new(true);
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Prop spawner gun is unequipped.");

    private static List<AssetDatabase.AssetEntry> entries = new();
    private static float nextFireTime;

    public override string Name => "Prop Spawner Gun";

    public override string Description =>
        "Equip a searchable prop gun and place the selected prefab at the crosshair's world position.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 250f, Label = "Maximum aim distance")]
    public static Ref<float> MaximumDistance = new(80f);

    [ModSetting(Order = 20, Min = 0f, Max = 5f, Label = "Surface offset",
        Description = "Moves the spawned pivot away from the hit surface to reduce clipping.")]
    public static Ref<float> SurfaceOffset = new(0.5f);

    [ModSetting(Order = 30, Min = 0.03f, Max = 0.5f, Label = "Rapid-fire interval",
        Description = "Seconds between props while holding the left mouse button.")]
    public static Ref<float> Cooldown = new(0.08f);

    [ModSetting(Order = 40, Label = "Face away from player")]
    public static Ref<bool> FaceAwayFromPlayer = new(true);

    [ModSetting(Order = 50, Min = -400f, Max = 400f, Label = "Crosshair horizontal offset",
        Description = "Pixels right of screen center; use a negative value to move left.")]
    public static Ref<float> CrosshairOffsetX = new(100f);

    [ModSetting(Order = 60, Min = -250f, Max = 250f, Label = "Crosshair vertical offset",
        Description = "Pixels below screen center; use a negative value to move up.")]
    public static Ref<float> CrosshairOffsetY = new(35f);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += RefreshCatalog;
        if (AssetDatabase.IsInitialized) RefreshCatalog();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("PropGunHelp",
                "Type in the prefab box to search. Equip the gun, close F2, aim at a surface, and left-click to spawn. " +
                "Open-sky shots use the maximum aim distance."),
            new SearchableCombo("Prop to spawn", Array.Empty<string>())
                .WithItems(DisplayItems)
                .WithSelectedIndex(SelectedIndex),
            new Checkbox("Use networked version when available", true).WithValue(Networked),
            base.BuildPanel(id),
            new HStack("PropGunActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Spawn once", Fire), nameof(Fire)),
                ActionMenu(new Button("Refresh props", RefreshCatalog), nameof(RefreshCatalog))
            ).WithContentWidth(),
            new TextWrapped("PropGunStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void Equip()
    {
        WindCannonMod.Unequip();
        VehicleAircraftSpawnerMod.Unequip();
        RocketLauncherMod.Unequip();
        PaintballGunMod.Unequip();
        MinecraftBuildingMod.Unequip();
        HeavyAutomaticGunMod.Unequip();
        GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Prop spawner gun equipped. Close the menu, aim, and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Prop spawner gun unequipped.";
    }

    [ModAction(ShowInUI = false, Label = "Spawn selected prop")]
    public static void Fire()
    {
        if (Time.unscaledTime < nextFireTime) return;

        if (entries.Count == 0)
        {
            Status.Value = "No prefabs are available. Wait for the asset scan, then refresh props.";
            return;
        }

        var index = SelectedIndex.Value;
        if (index < 0 || index >= entries.Count)
        {
            Status.Value = "The selected prop is no longer in the catalog. Refresh and select it again.";
            return;
        }

        var entry = entries[index];

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before firing.";
            return;
        }

        // Bombs only become functional when their Hawk networking lifecycle runs. Force the
        // registered network prefab path for them even if local-only spawning was requested.
        var requiresNetwork = entry.HasComponent("Bomb");
        var useNetworked = Networked.Value || requiresNetwork;

        if (useNetworked && !PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can spawn networked props.";
            return;
        }

        if (useNetworked && string.IsNullOrEmpty(entry.NetworkAssetId))
        {
            // Ordinary scenery can safely fall back to a local copy. A Bomb cannot: it would
            // appear usable but never detonate, which is worse than reporting why it failed.
            if (requiresNetwork)
            {
                Status.Value = $"{FriendlyName(entry)} needs networking, but has no registered network prefab ID.";
                return;
            }

            useNetworked = false;
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.05f, Cooldown.Value);

        // GUI coordinates count down from the top; ScreenPointToRay counts up from the bottom.
        // Mirroring Y here keeps the placement ray exactly underneath the drawn crosshair.
        var aimPoint = new Vector3(
            Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value,
            0f);
        var ray = camera.ScreenPointToRay(aimPoint);
        var distance = Mathf.Max(1f, MaximumDistance.Value);
        Vector3 position;

        if (Physics.Raycast(ray, out var hit, distance, ~0, QueryTriggerInteraction.Ignore))
            position = hit.point + hit.normal * SurfaceOffset.Value;
        else
            position = ray.origin + ray.direction * distance;

        var rotation = FaceAwayFromPlayer.Value
            ? Quaternion.Euler(0f, camera.transform.eulerAngles.y, 0f)
            : Quaternion.identity;

        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = entry.LoadKey,
            NetworkAssetId = entry.NetworkAssetId,
            Guid = entry.Guid,
            AssetName = entry.Name,
            Networked = useNetworked
        };

        var candidates = PropAddressResolver.Fallbacks(identity).ToList();
        Status.Value = $"Spawning {entry.Name}...";

        if (useNetworked)
            SpawnNetworked(candidates, position, rotation, identity);
        else
            Plugin.RunCoroutine(SpawnLocal(candidates, position, rotation, identity));
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshCatalog()
    {
        var previousKey = SelectedIndex.Value >= 0 && SelectedIndex.Value < entries.Count
            ? entries[SelectedIndex.Value].LoadKey
            : null;

        entries = AssetDatabase.Entries
            .Where(entry => entry.IsGameObject && !string.IsNullOrEmpty(entry.LoadKey))
            .OrderBy(entry => FriendlyName(entry), StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LoadKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            : $"Prop catalog ready: {entries.Count:N0} searchable prefabs.";
    }

    public override void Update()
    {
        if (!EquippedState.Value || Cursor.visible) return;
        if (Input.GetMouseButton(0)) Fire();
    }

    private static IEnumerator SpawnLocal(
        IEnumerable<string> candidates,
        Vector3 position,
        Quaternion rotation,
        PropIdentity identity)
    {
        foreach (var key in candidates)
        {
            var handle = Addressables.InstantiateAsync(key, position, rotation);
            yield return handle;

            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result)
            {
                RegisterSpawn(handle.Result, identity);
                yield break;
            }

            if (handle.IsValid()) Addressables.Release(handle);
        }

        Status.Value = $"Spawn failed: no valid Addressables key for {identity.Describe()}.";
    }

    private static void SpawnNetworked(
        IReadOnlyList<string> candidates,
        Vector3 position,
        Quaternion rotation,
        PropIdentity identity,
        int index = 0)
    {
        if (index >= candidates.Count)
        {
            Status.Value = $"Network spawn failed for {identity.Describe()}.";
            return;
        }

        NetworkPrefab.SpawnNetworkPrefab(
            candidates[index],
            behaviour =>
            {
                if (behaviour == null)
                {
                    SpawnNetworked(candidates, position, rotation, identity, index + 1);
                    return;
                }

                RegisterSpawn(behaviour.gameObject, identity);
            },
            position: position,
            rotation: rotation,
            owner: null,
            bUseChunkSystem: true,
            bSendTransform: true,
            bCheckChunk: true);
    }

    private static void RegisterSpawn(GameObject spawned, PropIdentity identity)
    {
        PropSpawnManager.Register(spawned, identity, null, null);
        Status.Value = $"Spawned {FriendlyName(identity.AssetName, identity.Address)}.";
        Plugin.Log?.LogInfo(Status.Value);
    }

    private static string FriendlyName(AssetDatabase.AssetEntry entry)
        => FriendlyName(entry.Name, entry.LoadKey);

    private static string FriendlyName(string name, string address)
    {
        if (!string.IsNullOrWhiteSpace(name) && name.Length > 1) return name;
        if (string.IsNullOrWhiteSpace(address)) return "Unnamed prefab";

        var slash = Math.Max(address.LastIndexOf('/'), address.LastIndexOf('\\'));
        var value = slash >= 0 ? address.Substring(slash + 1) : address;
        return value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            ? value.Substring(0, value.Length - ".prefab".Length)
            : value;
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;

        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var previous = GUI.color;
        GUI.color = new Color(0.55f, 1f, 0.45f, 0.95f);

        DrawRect(x - CrosshairGap - CrosshairLength, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x + CrosshairGap, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x - CrosshairThickness * 0.5f, y - CrosshairGap - CrosshairLength, CrosshairThickness, CrosshairLength);
        DrawRect(x - CrosshairThickness * 0.5f, y + CrosshairGap, CrosshairThickness, CrosshairLength);
        DrawRect(x - 1.5f, y - 1.5f, 3f, 3f);

        GUI.color = previous;
    }

    private static void DrawRect(float x, float y, float width, float height)
        => GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
}

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
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>Searchable ground-aligned building spawner with functional aircraft and Wobbly Jet spawning.</summary>
public sealed class BuildingJetSpawnerMod : BaseMod
{
    private static readonly string[] BuildingWords =
    {
        "building", "house", "home", "mansion", "apartment", "office", "skyscraper", "tower",
        "warehouse", "factory", "hangar", "airport", "terminal", "hospital", "school", "museum",
        "bank", "hotel", "church", "castle", "barn", "farmhouse", "garage", "shop", "store",
        "restaurant", "cafe", "cinema", "theatre", "theater", "arcade", "supermarket", "mall",
        "station", "lighthouse", "windmill", "town hall", "fire department", "police department"
    };

    private static readonly string[] BuildingPartWords =
    {
        " door", "door_", " window", "window_", " wall", "wall_", " floor", "floor_", " roof",
        "roof_", " ceiling", "ceiling_", " stair", "stair_", " railing", "railing_", " collider",
        "collider_", " lod", "lod_", " occlusion", " navmesh", " trigger", " furniture"
    };

    private static readonly Ref<string[]> BuildingItems = new(Array.Empty<string>());
    private static readonly Ref<int> BuildingIndex = new();
    private static readonly Ref<string[]> AircraftItems = new(Array.Empty<string>());
    private static readonly Ref<int> AircraftIndex = new();
    private static readonly Ref<string> Status = new("Waiting for the building and aircraft catalog...");
    private static readonly List<GameObject> SpawnedBuildings = new();
    private static List<AssetDatabase.AssetEntry> buildings = new();
    private static List<AssetDatabase.AssetEntry> aircraft = new();

    public override string Name => "Building & Wobbly Jet Spawner";

    public override string Description =>
        "Search and place ground-aligned buildings, spawn functional aircraft, or instantly create the Wobbly Jet.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 8f, Max = 100f, Label = "Building distance")]
    public static Ref<float> BuildingDistance = new(28f);

    [ModSetting(Order = 20, Min = -10f, Max = 25f, Label = "Building height offset")]
    public static Ref<float> BuildingHeightOffset = new(0f);

    [ModSetting(Order = 30, Min = -180f, Max = 180f, Label = "Building rotation offset")]
    public static Ref<float> BuildingRotation = new(0f);

    [ModSetting(Order = 40, Label = "Align building bottom to ground")]
    public static Ref<bool> AlignBuildingToGround = new(true);

    [ModSetting(Order = 50, Label = "Freeze building physics")]
    public static Ref<bool> FreezeBuildingPhysics = new(true);

    [ModSetting(Order = 60, Label = "Show full prefab catalog")]
    public static Ref<bool> ShowFullPrefabCatalog = new(false);

    [ModSetting(Order = 70, Min = 6f, Max = 50f, Label = "Aircraft distance")]
    public static Ref<float> AircraftDistance = new(14f);

    [ModSetting(Order = 80, Min = 1f, Max = 30f, Label = "Aircraft height")]
    public static Ref<float> AircraftHeight = new(7f);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += RefreshCatalog;
        if (AssetDatabase.IsInitialized) RefreshCatalog();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("BuildingJetHelp",
                "Type to search for a building, then spawn it on the ground in front of the camera. Buildings are local " +
                "scenery, stay upright, and can be undone or cleared. If a building is missing, enable Show full prefab " +
                "catalog and press Refresh. Aircraft use genuine network vehicle prefabs so they remain enterable. Host or " +
                "offline play is required for aircraft."),
            new SeparatorText("Buildings", "Buildings"),
            new SearchableCombo("Building", Array.Empty<string>())
                .WithItems(BuildingItems)
                .WithSelectedIndex(BuildingIndex),
            new HStack("BuildingSpawnActions",
                ActionMenu(new Button("Spawn building", SpawnBuilding), nameof(SpawnBuilding)),
                ActionMenu(new Button("Rotate left 15°", RotateBuildingLeft), nameof(RotateBuildingLeft)),
                ActionMenu(new Button("Rotate right 15°", RotateBuildingRight), nameof(RotateBuildingRight))
            ).WithContentWidth(),
            new HStack("BuildingCleanupActions",
                ActionMenu(new Button("Undo last building", UndoLastBuilding), nameof(UndoLastBuilding)),
                ActionMenu(new Button("Clear spawned buildings", ClearSpawnedBuildings), nameof(ClearSpawnedBuildings))
            ).WithContentWidth(),
            new SeparatorText("Aircraft and Wobbly Jet", "Aircraft and Wobbly Jet"),
            new SearchableCombo("Aircraft", Array.Empty<string>())
                .WithItems(AircraftItems)
                .WithSelectedIndex(AircraftIndex),
            new HStack("JetSpawnActions",
                ActionMenu(new Button("Spawn selected aircraft", SpawnSelectedAircraft), nameof(SpawnSelectedAircraft)),
                ActionMenu(new Button("Spawn Wobbly Jet", SpawnWobblyJet), nameof(SpawnWobblyJet))
            ).WithContentWidth(),
            base.BuildPanel(id),
            ActionMenu(new Button("Refresh building and aircraft lists", RefreshCatalog).WithContentWidth(),
                nameof(RefreshCatalog)),
            new TextWrapped("BuildingJetStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshCatalog()
    {
        var previousBuilding = SelectedKey(buildings, BuildingIndex.Value);
        var previousAircraft = SelectedKey(aircraft, AircraftIndex.Value);

        var gameObjects = AssetDatabase.Entries
            .Where(entry => entry.IsGameObject && !string.IsNullOrWhiteSpace(entry.LoadKey))
            .GroupBy(entry => entry.LoadKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        buildings = gameObjects
            .Where(entry => ShowFullPrefabCatalog.Value ? !IsVehicle(entry) : IsBuilding(entry))
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LoadKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        aircraft = gameObjects
            .Where(IsAircraft)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.NetworkAssetId))
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        BuildingItems.Value = buildings.Select(DisplayName).ToArray();
        AircraftItems.Value = aircraft.Select(DisplayName).ToArray();
        BuildingIndex.Value = RestoreIndex(buildings, previousBuilding);
        AircraftIndex.Value = RestoreIndex(aircraft, previousAircraft);

        var jet = ResolveWobblyJet();
        var catalogKind = ShowFullPrefabCatalog.Value ? "full prefab" : "building";
        Status.Value = $"Ready: {buildings.Count:N0} {catalogKind} entries and {aircraft.Count:N0} aircraft. " +
                       (jet == null ? "No jet match found yet." : $"Jet match: {FriendlyName(jet)}.");
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnBuilding()
    {
        PruneBuildings();
        if (buildings.Count == 0)
        {
            Status.Value = "No buildings are listed. Wait for the asset scan, then refresh.";
            return;
        }
        var index = BuildingIndex.Value;
        if (index < 0 || index >= buildings.Count)
        {
            Status.Value = "Select a valid building first.";
            return;
        }
        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "Enter a save before spawning a building.";
            return;
        }

        var entry = buildings[index];
        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        var candidate = camera.transform.position + forward * Mathf.Clamp(BuildingDistance.Value, 8f, 100f);
        var ground = FindGround(candidate, camera.transform.position.y - 1.5f);
        var position = ground + Vector3.up * BuildingHeightOffset.Value;
        var rotation = Quaternion.Euler(0f,
            Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg + BuildingRotation.Value, 0f);
        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = entry.LoadKey,
            NetworkAssetId = entry.NetworkAssetId,
            Guid = entry.Guid,
            AssetName = FriendlyName(entry),
            Networked = false
        };

        Status.Value = $"Spawning {FriendlyName(entry)}...";
        Plugin.RunCoroutine(SpawnBuildingLocal(PropAddressResolver.Fallbacks(identity), position, rotation, identity));
    }

    [ModAction(ShowInUI = false)]
    public static void RotateBuildingLeft()
    {
        BuildingRotation.Value = NormalizeAngle(BuildingRotation.Value - 15f);
        Status.Value = $"Building rotation: {BuildingRotation.Value:0}°.";
    }

    [ModAction(ShowInUI = false)]
    public static void RotateBuildingRight()
    {
        BuildingRotation.Value = NormalizeAngle(BuildingRotation.Value + 15f);
        Status.Value = $"Building rotation: {BuildingRotation.Value:0}°.";
    }

    [ModAction(ShowInUI = false)]
    public static void UndoLastBuilding()
    {
        PruneBuildings();
        if (SpawnedBuildings.Count == 0)
        {
            Status.Value = "There are no spawned buildings to undo.";
            return;
        }
        var last = SpawnedBuildings[SpawnedBuildings.Count - 1];
        SpawnedBuildings.RemoveAt(SpawnedBuildings.Count - 1);
        ReleaseBuilding(last);
        Status.Value = "Removed the last spawned building.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearSpawnedBuildings()
    {
        var removed = 0;
        for (var i = SpawnedBuildings.Count - 1; i >= 0; i--)
        {
            if (!SpawnedBuildings[i]) continue;
            ReleaseBuilding(SpawnedBuildings[i]);
            removed++;
        }
        SpawnedBuildings.Clear();
        Status.Value = $"Cleared {removed:N0} spawned buildings.";
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnSelectedAircraft()
    {
        if (aircraft.Count == 0)
        {
            Status.Value = "No networked aircraft are available yet. Refresh after the asset scan finishes.";
            return;
        }
        var index = AircraftIndex.Value;
        if (index < 0 || index >= aircraft.Count)
        {
            Status.Value = "Select a valid aircraft first.";
            return;
        }
        SpawnAircraft(aircraft[index], FriendlyName(aircraft[index]));
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnWobblyJet()
    {
        var jet = ResolveWobblyJet();
        if (jet == null)
        {
            Status.Value = "The Wobbly Jet was not found in this asset scan. Refresh the lists after entering a save.";
            return;
        }
        SpawnAircraft(jet, "Wobbly Jet");
    }

    private static IEnumerator SpawnBuildingLocal(
        IEnumerable<string> candidates,
        Vector3 groundPosition,
        Quaternion rotation,
        PropIdentity identity)
    {
        foreach (var key in candidates)
        {
            var handle = Addressables.InstantiateAsync(key, groundPosition, rotation);
            yield return handle;
            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result)
            {
                var building = handle.Result;
                ConfigureBuilding(building, groundPosition);
                PropSpawnManager.Register(building, identity, null, null);
                SpawnedBuildings.Add(building);
                Status.Value = $"Spawned {identity.AssetName}. {SpawnedBuildings.Count:N0} spawned buildings active.";
                Plugin.Log?.LogInfo(Status.Value);
                yield break;
            }
            if (handle.IsValid()) Addressables.Release(handle);
        }
        Status.Value = $"Could not load {identity.AssetName}. Try another catalog entry.";
    }

    private static void ConfigureBuilding(GameObject building, Vector3 groundPosition)
    {
        building.SetActive(true);
        foreach (var component in building.GetComponentsInChildren<MonoBehaviour>(true))
            if (component && component.GetType().Name == "ToolChunkSorter") component.enabled = false;

        foreach (var renderer in building.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer) continue;
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
        }

        if (FreezeBuildingPhysics.Value)
        {
            foreach (var body in building.GetComponentsInChildren<Rigidbody>(true))
            {
                if (!body) continue;
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.isKinematic = true;
            }
        }

        if (!AlignBuildingToGround.Value) return;
        var renderers = building.GetComponentsInChildren<Renderer>(true).Where(renderer => renderer).ToArray();
        if (renderers.Length == 0) return;
        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        var adjustment = groundPosition.y + BuildingHeightOffset.Value - bounds.min.y;
        if (Mathf.Abs(adjustment) < 250f) building.transform.position += Vector3.up * adjustment;
    }

    private static void SpawnAircraft(AssetDatabase.AssetEntry entry, string label)
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can spawn a functional aircraft.";
            return;
        }
        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "Enter a save before spawning an aircraft.";
            return;
        }
        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        var position = camera.transform.position + forward * Mathf.Clamp(AircraftDistance.Value, 6f, 50f) +
                       Vector3.up * Mathf.Clamp(AircraftHeight.Value, 1f, 30f);
        var rotation = Quaternion.LookRotation(forward, Vector3.up);
        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = entry.LoadKey,
            NetworkAssetId = entry.NetworkAssetId,
            Guid = entry.Guid,
            AssetName = label,
            Networked = true
        };
        Status.Value = $"Spawning {label}...";
        TryNetworkSpawn(PropAddressResolver.Fallbacks(identity).ToList(), 0, position, rotation, identity, label);
    }

    private static void TryNetworkSpawn(
        IReadOnlyList<string> candidates,
        int index,
        Vector3 position,
        Quaternion rotation,
        PropIdentity identity,
        string label)
    {
        if (index >= candidates.Count)
        {
            Status.Value = $"Failed to spawn {label}: no registered network key worked.";
            return;
        }
        NetworkPrefab.SpawnNetworkPrefab(
            candidates[index],
            behaviour =>
            {
                if (behaviour == null)
                {
                    TryNetworkSpawn(candidates, index + 1, position, rotation, identity, label);
                    return;
                }
                var spawned = behaviour.gameObject;
                spawned.SetActive(true);
                spawned.transform.SetPositionAndRotation(position, rotation);
                foreach (var renderer in spawned.GetComponentsInChildren<Renderer>(true))
                {
                    if (!renderer) continue;
                    renderer.enabled = true;
                    renderer.forceRenderingOff = false;
                }
                PropSpawnManager.Register(spawned, identity, null, null);
                Status.Value = $"Spawned {label}. Walk up and press F to enter it.";
                Plugin.Log?.LogInfo(Status.Value);
            },
            position: position,
            rotation: rotation,
            owner: null,
            bUseChunkSystem: false,
            bSendTransform: true,
            bCheckChunk: false);
    }

    private static AssetDatabase.AssetEntry ResolveWobblyJet()
    {
        return aircraft
            .Where(entry => SearchText(entry).Contains("jet"))
            .OrderBy(JetPreference)
            .ThenBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int JetPreference(AssetDatabase.AssetEntry entry)
    {
        var value = SearchText(entry);
        if (value.Contains("wobbly jet") || value.Contains("wobbly_jet")) return 0;
        if (value.Contains("vehicle jet") || value.Contains("vehicle_jet")) return 1;
        if (value.Contains("fighter jet") || value.Contains("fighter_jet")) return 2;
        return 3;
    }

    private static bool IsBuilding(AssetDatabase.AssetEntry entry)
    {
        if (IsVehicle(entry)) return false;
        var value = SearchText(entry);
        if (BuildingPartWords.Any(value.Contains)) return false;
        return value.Contains("/buildings/") || value.Contains("\\buildings\\") ||
               BuildingWords.Any(value.Contains);
    }

    private static bool IsVehicle(AssetDatabase.AssetEntry entry)
    {
        return entry.HasComponent("PlayerVehicle") || entry.HasComponent("PlayerVehicleRoad") ||
               entry.HasComponent("PlayerBoat") || entry.HasComponent("PlayerPlane") ||
               entry.HasComponent("PlayerHelicopter") || entry.HasComponent("PlayerHotAirBalloon") ||
               entry.HasComponent("PlayerUFO") || entry.HasComponent("PlayerTrainCart") ||
               entry.HasComponent("PlayerSpaceShip") || entry.HasComponent("PlayerSpaceHovercraft");
    }

    private static bool IsAircraft(AssetDatabase.AssetEntry entry)
    {
        return entry.HasComponent("PlayerPlane") || entry.HasComponent("PlayerHelicopter") ||
               entry.HasComponent("PlayerHotAirBalloon") || entry.HasComponent("PlayerUFO");
    }

    private static Vector3 FindGround(Vector3 candidate, float fallbackHeight)
    {
        var hits = Physics.RaycastAll(candidate + Vector3.up * 100f, Vector3.down, 220f, ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.normal.y < 0.45f) continue;
            if (hit.collider.GetComponentInParent<PlayerCharacter>() ||
                hit.collider.GetComponentInParent<PlayerVehicle>() ||
                hit.collider.GetComponentInParent<DynamicObject>()) continue;
            return hit.point;
        }
        return new Vector3(candidate.x, fallbackHeight, candidate.z);
    }

    private static void PruneBuildings()
    {
        for (var i = SpawnedBuildings.Count - 1; i >= 0; i--)
            if (!SpawnedBuildings[i]) SpawnedBuildings.RemoveAt(i);
    }

    private static void ReleaseBuilding(GameObject building)
    {
        if (!building) return;
        try
        {
            if (!Addressables.ReleaseInstance(building)) Object.Destroy(building);
        }
        catch { Object.Destroy(building); }
    }

    private static string SelectedKey(IReadOnlyList<AssetDatabase.AssetEntry> source, int index)
        => index >= 0 && index < source.Count ? source[index].LoadKey : null;

    private static int RestoreIndex(IReadOnlyList<AssetDatabase.AssetEntry> source, string key)
    {
        if (source.Count == 0) return 0;
        if (string.IsNullOrWhiteSpace(key)) return 0;
        var index = source.ToList().FindIndex(entry =>
            string.Equals(entry.LoadKey, key, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 0;
    }

    private static string SearchText(AssetDatabase.AssetEntry entry)
        => $"{entry.Name} {entry.Address} {entry.LoadKey}".ToLowerInvariant();

    private static string DisplayName(AssetDatabase.AssetEntry entry)
    {
        var name = FriendlyName(entry);
        return string.Equals(name, entry.LoadKey, StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}  —  {entry.LoadKey}";
    }

    private static string FriendlyName(AssetDatabase.AssetEntry entry)
    {
        var value = !string.IsNullOrWhiteSpace(entry.Address) ? entry.Address : entry.Name;
        if (string.IsNullOrWhiteSpace(value)) value = entry.LoadKey;
        if (string.IsNullOrWhiteSpace(value)) return "Unnamed prefab";
        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slash >= 0) value = value.Substring(slash + 1);
        if (value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - ".prefab".Length);
        if (value.StartsWith("Vehicle_", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("Vehicle_".Length);
        return value.Replace('_', ' ');
    }

    private static float NormalizeAngle(float value)
    {
        while (value > 180f) value -= 360f;
        while (value < -180f) value += 360f;
        return value;
    }
}

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

/// <summary>Searchable host-side spawner for drivable vehicles and aircraft.</summary>
public sealed class VehicleAircraftSpawnerMod : BaseMod
{
    private const string RocketWingCarUnlockKey = "WobblyLifeExtraMods.RocketWingCarUnlocked";
    private enum GunMode
    {
        Vehicle,
        Aircraft,
        Ufo,
        EggUfo
    }

    private const float CrosshairGap = 8f;
    private const float CrosshairLength = 10f;
    private const float CrosshairThickness = 2f;

    private static readonly Ref<string[]> VehicleItems = new(Array.Empty<string>());
    private static readonly Ref<int> VehicleIndex = new();
    private static readonly Ref<string[]> AircraftItems = new(Array.Empty<string>());
    private static readonly Ref<int> AircraftIndex = new();
    private static readonly Ref<string> Status = new("Waiting for the vehicle catalog...");
    private static readonly Ref<string> RocketWingStatus = new("Rocket Wing Car: locked");
    private static readonly Ref<bool> EquippedState = new();

    private static List<AssetDatabase.AssetEntry> vehicles = new();
    private static List<AssetDatabase.AssetEntry> aircraft = new();
    private static GunMode gunMode;
    private static float nextFireTime;

    public override string Name => "Vehicle & Aircraft Spawner";

    public override string Description =>
        "Search and spawn functional networked cars, boats, special vehicles, planes, helicopters, balloons, and UFOs.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 40f, Label = "Spawn distance")]
    public static Ref<float> SpawnDistance = new(12f);

    [ModSetting(Order = 20, Min = 0f, Max = 15f, Label = "Vehicle height")]
    public static Ref<float> VehicleHeight = new(1.5f);

    [ModSetting(Order = 30, Min = 0f, Max = 30f, Label = "Aircraft height")]
    public static Ref<float> AircraftHeight = new(7f);

    [ModSetting(Order = 40, Label = "Face away from player")]
    public static Ref<bool> FaceAwayFromPlayer = new(true);

    [ModSetting(Order = 50, Min = 0.05f, Max = 2f, Label = "Rapid-fire interval",
        Description = "Seconds between networked vehicle spawns while holding left-click. Very low values can cause heavy lag.")]
    public static Ref<float> RapidFireInterval = new(0.25f);

    [ModSetting(Order = 60, Min = -400f, Max = 400f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(100f);

    [ModSetting(Order = 70, Min = -250f, Max = 250f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(35f);

    [ModSetting(Order = 80, Min = 10f, Max = 80f, Label = "Rocket car takeoff speed")]
    public static Ref<float> RocketTakeoffSpeed = new(28f);

    [ModSetting(Order = 90, Min = 5f, Max = 100f, Label = "Rocket booster power")]
    public static Ref<float> RocketBoosterPower = new(42f);

    [ModSetting(Order = 100, Min = 2f, Max = 35f, Label = "Rocket car lift power")]
    public static Ref<float> RocketLiftPower = new(13f);

    [ModSetting(Order = 110, Min = 40f, Max = 250f, Label = "Rocket car maximum speed")]
    public static Ref<float> RocketMaximumSpeed = new(130f);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += RefreshCatalog;
        if (AssetDatabase.IsInitialized) RefreshCatalog();
    }

    public override Container BuildPanel(string id)
    {
        UpdateRocketWingStatus();
        return new Container(id,
            new TextWrapped("VehicleSpawnerHelp",
                "Type inside either list to search. Vehicles are spawned through Wobbly Life's network-prefab system, " +
                "so they remain drivable and synchronized. Host/offline only."),
            new SeparatorText("Ground and water vehicles", "Ground and water vehicles"),
            new SearchableCombo("Vehicle", Array.Empty<string>())
                .WithItems(VehicleItems)
                .WithSelectedIndex(VehicleIndex),
            new HStack("VehicleActions",
                ActionMenu(new Button("Spawn once", SpawnVehicle), nameof(SpawnVehicle)),
                ActionMenu(new Button("Equip vehicle gun", EquipVehicleGun), nameof(EquipVehicleGun))
            ).WithContentWidth(),
            new SeparatorText("Rocket Wing Car", "Rocket Wing Car"),
            new TextWrapped("RocketWingHelp",
                "Unlock this custom car once, then spawn it here whenever you visit the vehicle-spawner panel. Build road " +
                "speed to take off. While driving, K toggles the unlimited rocket boosters; Space climbs, Left Control dives, " +
                "W/S pitch, and A/D turn and roll."),
            new HStack("RocketWingActions",
                ActionMenu(new Button("Unlock Rocket Wing Car", UnlockRocketWingCar), nameof(UnlockRocketWingCar)),
                ActionMenu(new Button("Spawn Rocket Wing Car", SpawnRocketWingCar), nameof(SpawnRocketWingCar))
            ).WithContentWidth(),
            new TextWrapped("RocketWingStatus", "").WithText(RocketWingStatus),
            new SeparatorText("Aircraft", "Aircraft"),
            new SearchableCombo("Plane / aircraft", Array.Empty<string>())
                .WithItems(AircraftItems)
                .WithSelectedIndex(AircraftIndex),
            new HStack("AircraftSpawnActions",
                ActionMenu(new Button("Spawn aircraft once", SpawnAircraft), nameof(SpawnAircraft)),
                ActionMenu(new Button("Equip aircraft gun", EquipAircraftGun), nameof(EquipAircraftGun)),
                ActionMenu(new Button("Spawn UFO", SpawnUfo), nameof(SpawnUfo)),
                ActionMenu(new Button("Spawn Egg UFO", SpawnEggUfo), nameof(SpawnEggUfo))
            ).WithContentWidth(),
            new HStack("UfoGunActions",
                ActionMenu(new Button("Equip UFO gun", EquipUfoGun), nameof(EquipUfoGun)),
                ActionMenu(new Button("Equip Egg UFO gun", EquipEggUfoGun), nameof(EquipEggUfoGun)),
                ActionMenu(new Button("Unequip gun", Unequip), nameof(Unequip))
            ).WithContentWidth(),
            base.BuildPanel(id),
            ActionMenu(new Button("Refresh vehicle lists", RefreshCatalog).WithContentWidth(), nameof(RefreshCatalog)),
            new TextWrapped("VehicleSpawnerStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnVehicle()
    {
        SpawnSelected(vehicles, VehicleIndex.Value, VehicleHeight.Value, "vehicle");
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnAircraft()
    {
        SpawnSelected(aircraft, AircraftIndex.Value, AircraftHeight.Value, "aircraft");
    }

    [ModAction(ShowInUI = false)]
    public static void UnlockRocketWingCar()
    {
        PlayerPrefs.SetInt(RocketWingCarUnlockKey, 1);
        PlayerPrefs.Save();
        UpdateRocketWingStatus("Unlocked permanently. You can now spawn the Rocket Wing Car.");
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnRocketWingCar()
    {
        if (!IsRocketWingCarUnlocked())
        {
            UpdateRocketWingStatus("Locked. Press Unlock Rocket Wing Car first.");
            return;
        }
        if (vehicles.Count == 0)
        {
            UpdateRocketWingStatus("Vehicle catalog is not ready yet. Refresh the vehicle lists.");
            return;
        }

        var roadCars = vehicles.Where(entry => entry.HasComponent("PlayerVehicleRoad")).ToList();
        var preferred = (roadCars.Count > 0 ? roadCars : vehicles)
            .OrderBy(entry => RocketCarPreference(FriendlyName(entry)))
            .FirstOrDefault();
        if (preferred == null)
        {
            UpdateRocketWingStatus("No road-car prefab was found in this game build.");
            return;
        }
        SpawnRocketWingCar(preferred);
    }

    [ModAction(ShowInUI = false, Label = "Spawn UFO")]
    public static void SpawnUfo()
    {
        SpawnByAddress("Vehicle_UFO", AircraftHeight.Value, "UFO");
    }

    [ModAction(ShowInUI = false, Label = "Spawn Egg UFO")]
    public static void SpawnEggUfo()
    {
        SpawnByAddress("Vehicle_Egg UFO", AircraftHeight.Value, "Egg UFO");
    }

    [ModAction(ShowInUI = false)]
    public static void EquipVehicleGun()
    {
        Equip(GunMode.Vehicle, "vehicle");
    }

    [ModAction(ShowInUI = false)]
    public static void EquipAircraftGun()
    {
        Equip(GunMode.Aircraft, "aircraft");
    }

    [ModAction(ShowInUI = false, Label = "Equip UFO gun")]
    public static void EquipUfoGun()
    {
        Equip(GunMode.Ufo, "UFO");
    }

    [ModAction(ShowInUI = false, Label = "Equip Egg UFO gun")]
    public static void EquipEggUfoGun()
    {
        Equip(GunMode.EggUfo, "Egg UFO");
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Vehicle spawner gun unequipped.";
    }

    private static void Equip(GunMode mode, string label)
    {
        WindCannonMod.Unequip();
        PropSpawnerGunMod.Unequip();
        RocketLauncherMod.Unequip();
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
        gunMode = mode;
        EquippedState.Value = true;
        Status.Value = $"{label} gun equipped. Close F2, aim, and hold left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshCatalog()
    {
        vehicles = AssetDatabase.Entries
            .Where(IsGroundOrWaterVehicle)
            .Where(HasNetworkIdentity)
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        aircraft = AssetDatabase.Entries
            .Where(IsAircraft)
            .Where(HasNetworkIdentity)
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        VehicleItems.Value = vehicles.Select(FriendlyName).ToArray();
        AircraftItems.Value = aircraft.Select(FriendlyName).ToArray();
        VehicleIndex.Value = Mathf.Clamp(VehicleIndex.Value, 0, Math.Max(0, vehicles.Count - 1));
        AircraftIndex.Value = Mathf.Clamp(AircraftIndex.Value, 0, Math.Max(0, aircraft.Count - 1));

        Status.Value = $"Ready: {vehicles.Count} vehicles and {aircraft.Count} aircraft.";
        UpdateRocketWingStatus();
    }

    private static bool IsRocketWingCarUnlocked() => PlayerPrefs.GetInt(RocketWingCarUnlockKey, 0) == 1;

    private static void UpdateRocketWingStatus(string detail = null)
    {
        var access = IsRocketWingCarUnlocked() ? "unlocked" : "locked";
        RocketWingStatus.Value = detail == null ? $"Rocket Wing Car: {access}." : $"Rocket Wing Car: {access}. {detail}";
    }

    internal static void NotifyRocketWingCar(string detail) => UpdateRocketWingStatus(detail);

    private static int RocketCarPreference(string name)
    {
        var value = name.ToLowerInvariant();
        if (value.Contains("super") || value.Contains("sport")) return 0;
        if (value.Contains("race") || value.Contains("formula")) return 1;
        if (value.Contains("car") || value.Contains("road")) return 2;
        return 3;
    }

    private static void SpawnRocketWingCar(AssetDatabase.AssetEntry entry)
    {
        if (!PropSpawnManager.IsServer)
        {
            UpdateRocketWingStatus("Only the lobby host can spawn the custom car.");
            return;
        }
        var camera = Camera.main;
        if (!camera)
        {
            UpdateRocketWingStatus("Enter a save before spawning the custom car.");
            return;
        }
        var flatForward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (flatForward.sqrMagnitude < 0.01f) flatForward = Vector3.forward;
        var position = camera.transform.position + flatForward * SpawnDistance.Value + Vector3.up * VehicleHeight.Value;
        var rotation = Quaternion.LookRotation(flatForward, Vector3.up);
        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = entry.LoadKey,
            NetworkAssetId = entry.NetworkAssetId,
            Guid = entry.Guid,
            AssetName = "Rocket Wing Car",
            Networked = true
        };
        UpdateRocketWingStatus("Spawning...");
        TryNetworkSpawn(PropAddressResolver.Fallbacks(identity).ToList(), 0, position, rotation, identity,
            "Rocket Wing Car", spawned => RocketWingCarController.Attach(spawned));
    }

    private static void SpawnByAddress(string address, float height, string label)
    {
        var entry = AssetDatabase.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.LoadKey, address, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            Status.Value = $"{label} was not found in this game build. Refresh the vehicle lists.";
            return;
        }

        Spawn(entry, height, label);
    }

    private static void SpawnSelected(
        IReadOnlyList<AssetDatabase.AssetEntry> source,
        int index,
        float height,
        string kind)
    {
        if (source.Count == 0)
        {
            Status.Value = $"No {kind} entries are available. Wait for the asset scan and refresh.";
            return;
        }

        if (index < 0 || index >= source.Count)
        {
            Status.Value = $"Select a valid {kind}.";
            return;
        }

        Spawn(source[index], height, FriendlyName(source[index]));
    }

    private static void Spawn(AssetDatabase.AssetEntry entry, float height, string label)
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can spawn functional vehicles.";
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before spawning a vehicle.";
            return;
        }

        var flatForward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (flatForward.sqrMagnitude < 0.01f) flatForward = camera.transform.forward;

        var position = camera.transform.position + flatForward * SpawnDistance.Value + Vector3.up * height;
        var rotation = FaceAwayFromPlayer.Value
            ? Quaternion.LookRotation(flatForward, Vector3.up)
            : Quaternion.identity;

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
        Status.Value = $"Spawning {label}...";
        TryNetworkSpawn(candidates, 0, position, rotation, identity, label);
    }

    private static void FireGun()
    {
        if (Time.unscaledTime < nextFireTime) return;
        nextFireTime = Time.unscaledTime + Mathf.Max(0.05f, RapidFireInterval.Value);

        switch (gunMode)
        {
            case GunMode.Vehicle:
                SpawnSelectedAtAim(vehicles, VehicleIndex.Value, VehicleHeight.Value, "vehicle");
                break;
            case GunMode.Aircraft:
                SpawnSelectedAtAim(aircraft, AircraftIndex.Value, AircraftHeight.Value, "aircraft");
                break;
            case GunMode.Ufo:
                SpawnByAddressAtAim("Vehicle_UFO", AircraftHeight.Value, "UFO");
                break;
            case GunMode.EggUfo:
                SpawnByAddressAtAim("Vehicle_Egg UFO", AircraftHeight.Value, "Egg UFO");
                break;
        }
    }

    private static void SpawnSelectedAtAim(
        IReadOnlyList<AssetDatabase.AssetEntry> source,
        int index,
        float height,
        string kind)
    {
        if (source.Count == 0 || index < 0 || index >= source.Count)
        {
            Status.Value = $"Select a valid {kind} before firing.";
            return;
        }

        SpawnAtAim(source[index], height, FriendlyName(source[index]));
    }

    private static void SpawnByAddressAtAim(string address, float height, string label)
    {
        var entry = AssetDatabase.Entries.FirstOrDefault(candidate =>
            string.Equals(candidate.LoadKey, address, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Address, address, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            Status.Value = $"{label} was not found in this game build.";
            return;
        }

        SpawnAtAim(entry, height, label);
    }

    private static void SpawnAtAim(AssetDatabase.AssetEntry entry, float height, string label)
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can fire the vehicle spawner gun.";
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found.";
            return;
        }

        var screenPoint = new Vector3(
            Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value,
            0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        var maxDistance = Mathf.Max(5f, SpawnDistance.Value * 3f);

        Vector3 position;
        if (Physics.Raycast(ray, out var hit, maxDistance, ~0, QueryTriggerInteraction.Ignore))
            position = hit.point + Vector3.up * height;
        else
            position = ray.origin + ray.direction * maxDistance + Vector3.up * height;

        var flatForward = Vector3.ProjectOnPlane(ray.direction, Vector3.up).normalized;
        if (flatForward.sqrMagnitude < 0.01f) flatForward = Vector3.forward;
        var rotation = FaceAwayFromPlayer.Value
            ? Quaternion.LookRotation(flatForward, Vector3.up)
            : Quaternion.identity;

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
        TryNetworkSpawn(candidates, 0, position, rotation, identity, label);
    }

    public override void Update()
    {
        if (!EquippedState.Value || Cursor.visible) return;
        if (Input.GetMouseButton(0)) FireGun();
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;

        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var previous = GUI.color;
        GUI.color = new Color(1f, 0.75f, 0.15f, 0.95f);

        DrawRect(x - CrosshairGap - CrosshairLength, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x + CrosshairGap, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x - CrosshairThickness * 0.5f, y - CrosshairGap - CrosshairLength, CrosshairThickness, CrosshairLength);
        DrawRect(x - CrosshairThickness * 0.5f, y + CrosshairGap, CrosshairThickness, CrosshairLength);
        DrawRect(x - 2f, y - 2f, 4f, 4f);

        GUI.color = previous;
    }

    private static void DrawRect(float x, float y, float width, float height)
        => GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);

    private static void TryNetworkSpawn(
        IReadOnlyList<string> candidates,
        int index,
        Vector3 position,
        Quaternion rotation,
        PropIdentity identity,
        string label,
        Action<GameObject> configure = null)
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
                    TryNetworkSpawn(candidates, index + 1, position, rotation, identity, label, configure);
                    return;
                }

                PropSpawnManager.Register(behaviour.gameObject, identity, null, null);
                configure?.Invoke(behaviour.gameObject);
                Status.Value = $"Spawned {label}.";
                if (label == "Rocket Wing Car")
                    UpdateRocketWingStatus("Spawned. Build speed to take off; K toggles the boosters.");
                Plugin.Log?.LogInfo(Status.Value);
            },
            position: position,
            rotation: rotation,
            owner: null,
            bUseChunkSystem: true,
            bSendTransform: true,
            bCheckChunk: true);
    }

    private static bool IsGroundOrWaterVehicle(AssetDatabase.AssetEntry entry)
        => entry.IsGameObject &&
           (entry.HasComponent("PlayerVehicleRoad") ||
            entry.HasComponent("PlayerBoat") ||
            entry.HasComponent("PlayerTrainCart") ||
            entry.HasComponent("PlayerSpaceShip") ||
            entry.HasComponent("PlayerSpaceHovercraft"));

    private static bool IsAircraft(AssetDatabase.AssetEntry entry)
        => entry.IsGameObject &&
           (entry.HasComponent("PlayerPlane") ||
            entry.HasComponent("PlayerHelicopter") ||
            entry.HasComponent("PlayerHotAirBalloon") ||
            entry.HasComponent("PlayerUFO"));

    private static bool HasNetworkIdentity(AssetDatabase.AssetEntry entry)
        => !string.IsNullOrEmpty(entry.NetworkAssetId) && !string.IsNullOrEmpty(entry.LoadKey);

    private static string FriendlyName(AssetDatabase.AssetEntry entry)
    {
        var value = !string.IsNullOrWhiteSpace(entry.Address) ? entry.Address : entry.Name;
        if (string.IsNullOrWhiteSpace(value)) return "Unnamed vehicle";

        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slash >= 0) value = value.Substring(slash + 1);
        if (value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - ".prefab".Length);
        if (value.StartsWith("Vehicle_", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("Vehicle_".Length);

        return value.Replace('_', ' ');
    }
}

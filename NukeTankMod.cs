using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FMODUnity;
using HarmonyLib;
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

public sealed class NukeTankMod : BaseMod
{
    private static readonly Ref<string> Status = new("Enter a world and spawn the tank.");
    private static NukeTankController tank;
    private static bool spawning;
    internal static bool BlocksFreeCamera => tank && tank.IsLocalDriver;
    public override string Name => "Nuke & Minigun Tank";
    public override string Description => "Drive a tank with nukes, a minigun and a guided missile camera.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("NukeTankHelp", "Host/offline: spawn the custom armored tank and enter the driver seat. Press 5 for a nuke, hold O for minigun fire, or press H for a mouse-guided nuke and follow-camera. The minigun has 1,000 rounds and reloads in 3.5 seconds; both nuke modes share 100 rounds and reload in 5.3 seconds. Reload starts automatically when empty. Tank body, weapon visuals and guided camera are local to the host; the drivable base is networked."),
        new HStack("NukeTankActions", ActionMenu(new Button("Spawn tank", Spawn), nameof(Spawn))).WithContentWidth(),
        new TextWrapped("NukeTankStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)] public static void Spawn()
    {
        if (!PropSpawnManager.IsServer || !GameInstance.InstanceExists) { Status.Value = "Enter a world as host or offline player first."; return; }
        if (tank || spawning) { Status.Value = "Your spawned tank is already in the world or is spawning."; return; }
        var camera = Camera.main;
        if (!camera) { Status.Value = "No gameplay camera found."; return; }
        var entry = AssetDatabase.Entries.FirstOrDefault(e => e.IsGameObject && !string.IsNullOrEmpty(e.NetworkAssetId) &&
            (string.Equals(e.LoadKey, "Vehicle_WaterBalloonTank", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(e.Name, "Vehicle_WaterBalloonTank", StringComparison.OrdinalIgnoreCase)));
        if (entry == null) { Status.Value = "The network tank was not found; wait for the asset scan and try again."; return; }
        var identity = new PropIdentity { Kind = PropSourceKind.Addressable, Address = entry.LoadKey, NetworkAssetId = entry.NetworkAssetId,
            Guid = entry.Guid, AssetName = "Nuke Tank", Networked = true };
        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        var position = camera.transform.position + forward * 12f;
        if (Physics.Raycast(position + Vector3.up * 30f, Vector3.down, out var hit, 70f, ~0, QueryTriggerInteraction.Ignore)) position = hit.point + Vector3.up * 1.5f;
        spawning = true; Status.Value = "Spawning tank...";
        SpawnCandidate(PropAddressResolver.Fallbacks(identity).ToList(), 0, position, Quaternion.LookRotation(forward), identity);
    }
    private static void SpawnCandidate(IReadOnlyList<string> choices, int index, Vector3 position, Quaternion rotation, PropIdentity identity)
    {
        if (index >= choices.Count) { spawning = false; Status.Value = "The game could not spawn the tank prefab."; return; }
        NetworkPrefab.SpawnNetworkPrefab(choices[index], behaviour =>
        {
            if (!behaviour) { SpawnCandidate(choices, index + 1, position, rotation, identity); return; }
            spawning = false;
            if (!GameInstance.InstanceExists || !PropSpawnManager.IsServer)
            { if (behaviour.networkObject != null && behaviour.networkObject.IsServer()) behaviour.networkObject.Destroy(); return; }
            var vehicle = behaviour.GetComponentInChildren<PlayerVehicle>(true);
            if (!vehicle)
            {
                Status.Value = "The tank prefab has no usable vehicle; it was removed.";
                if (behaviour.networkObject != null && behaviour.networkObject.IsServer()) behaviour.networkObject.Destroy();
                return;
            }
            PropSpawnManager.Register(behaviour.gameObject, identity, null, null);
            tank = vehicle.gameObject.AddComponent<NukeTankController>();
            tank.Initialize(vehicle);
            Status.Value = "Tank ready. Enter the driver seat: 5 nuke, O minigun, H guided nuke.";
        }, position: position, rotation: rotation, owner: null, bUseChunkSystem: true, bSendTransform: true, bCheckChunk: true);
    }
    internal static void Report(string message) { Status.Value = message; Plugin.Log?.LogInfo("Nuke tank: " + message); }
    internal static void DrawOverlay()
    {
        if (!tank || !tank.IsLocalDriver || Event.current.type != EventType.Repaint) return;
        var box = new Rect(18f, Screen.height - 112f, 430f, 90f);
        var old = GUI.color; GUI.color = new Color(0f, 0f, 0f, 0.8f); GUI.DrawTexture(box, Texture2D.whiteTexture); GUI.color = old;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        GUI.Label(new Rect(box.x + 10, box.y + 5, 410, 25), tank.AmmoText, style);
        GUI.Label(new Rect(box.x + 10, box.y + 30, 410, 25), "5: NUKE   O: MINIGUN   H: GUIDED NUKE", style);
        GUI.Label(new Rect(box.x + 10, box.y + 55, 410, 25), tank.IsGuiding ? "Mouse steers the nuke • camera returns on impact" : "Aim with your camera", style);
        if (!tank.IsGuiding && !Cursor.visible)
        {
            var previous = GUI.color; GUI.color = Color.black;
            GUI.DrawTexture(new Rect(Screen.width * 0.5f - 1.5f, Screen.height * 0.5f - 1.5f, 3f, 3f), Texture2D.whiteTexture);
            GUI.color = previous;
        }
    }
    internal static void Shutdown() { if (tank) tank.EndGuidance(); tank = null; spawning = false; TankNativeExplosion.Shutdown(); TankBulletVisual.Shutdown(); }
}

internal sealed class NukeTankController : MonoBehaviour
{
    private const int NukeCapacity = 100;
    private const int GunCapacity = 1000;
    private static readonly object InputHandle = new();
    private PlayerVehicle vehicle;
    private PlayerControllerInputManager input;
    private TankNukeProjectile guided;
    private TankGuidanceCamera cameraDriver;
    private int nukes = NukeCapacity;
    private int rounds = GunCapacity;
    private float nukeReloadUntil, gunReloadUntil, nextGun, nextNuke, nextSound;
    private bool cursorWasVisible;
    private CursorLockMode previousLock;
    public bool IsGuiding => guided;
    public bool IsLocalDriver => vehicle && GameInstance.InstanceExists &&
        vehicle.GetDriverPlayerController() == GameInstance.Instance.GetFirstLocalPlayerController();
    public string AmmoText => "NUKES: " + (nukeReloadUntil > 0 ? "reloading " + Mathf.Max(0, nukeReloadUntil - Time.unscaledTime).ToString("0.0") + "s" : nukes + "/100") +
        "     MINIGUN: " + (gunReloadUntil > 0 ? "reloading " + Mathf.Max(0, gunReloadUntil - Time.unscaledTime).ToString("0.0") + "s" : rounds + "/1000");
    internal void Initialize(PlayerVehicle target)
    {
        vehicle = target;
        vehicle.AddNotAllowedOptimizationHandle(this);
        vehicle.SetInteractable(true);
        var seat = vehicle.GetActionEnterExitInteract(); if (seat) seat.SetInteractable(true, true);
        CustomTankVisual.Create(vehicle);
        TankNativeExplosion.Preload();
    }
    private void Update()
    {
        if (!vehicle || !GameInstance.InstanceExists || !IsLocalDriver) { EndGuidance(); return; }
        if (nukeReloadUntil > 0 && Time.unscaledTime >= nukeReloadUntil) { nukes = NukeCapacity; nukeReloadUntil = 0; }
        if (gunReloadUntil > 0 && Time.unscaledTime >= gunReloadUntil) { rounds = GunCapacity; gunReloadUntil = 0; }
        if (Cursor.visible) return;
        if (!guided && Input.GetKeyDown(KeyCode.Alpha5)) FireNuke(false);
        if (!guided && Input.GetKeyDown(KeyCode.H)) FireNuke(true);
        if (!guided && Input.GetKey(KeyCode.O)) FireMinigun();
    }
    private void FireNuke(bool isGuided)
    {
        if (nukes <= 0 || nukeReloadUntil > 0 || Time.unscaledTime < nextNuke) return;
        var camera = Camera.main; if (!camera) return;
        var direction = camera.transform.forward;
        var origin = vehicle.transform.position + Vector3.up * 2.5f + vehicle.transform.forward * 4f;
        var root = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        root.name = isGuided ? "Guided tank nuke" : "Tank nuke";
        var collider = root.GetComponent<Collider>(); if (collider) Destroy(collider);
        root.transform.position = origin; root.transform.rotation = Quaternion.LookRotation(direction) * Quaternion.Euler(90f, 0f, 0f);
        root.transform.localScale = new Vector3(0.55f, 0.85f, 0.55f);
        var projectile = root.AddComponent<TankNukeProjectile>();
        projectile.Initialize(this, vehicle, direction, isGuided);
        nukes--; nextNuke = Time.unscaledTime + 0.4f;
        if (nukes == 0) nukeReloadUntil = Time.unscaledTime + 5.3f;
        PlayShotSound(origin);
        if (isGuided) BeginGuidance(projectile, camera);
    }
    private void FireMinigun()
    {
        if (rounds <= 0 || gunReloadUntil > 0 || Time.unscaledTime < nextGun) return;
        var camera = Camera.main; if (!camera) return;
        nextGun = Time.unscaledTime + 0.1f; rounds--;
        if (rounds == 0) gunReloadUntil = Time.unscaledTime + 3.5f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
        var hits = Physics.RaycastAll(ray, 300f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        var shooter = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = shooter ? shooter.GetPlayerCharacter() : null;
        var endpoint = ray.GetPoint(300f);
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(vehicle.transform) || (character && hit.transform.IsChildOf(character.transform))) continue;
            endpoint = hit.point;
            TankExplosion.Blast(hit.point, 2.5f, 500f, vehicle, shooter);
            break;
        }
        TankBulletVisual.Create(vehicle.transform.position + Vector3.up * 2.5f + vehicle.transform.forward * 3.5f, endpoint);
        if (Time.unscaledTime >= nextSound) { nextSound = Time.unscaledTime + 0.25f; PlayShotSound(vehicle.transform.position); }
    }
    private static void PlayShotSound(Vector3 point)
    {
        try { var sound = RuntimeManager.CreateInstance("event:/Objects/Objects_PaperCannon"); sound.set3DAttributes(RuntimeUtils.To3DAttributes(point)); sound.start(); sound.release(); }
        catch (Exception exception) { Plugin.Log?.LogWarning("Tank sound failed: " + exception.Message); }
    }
    private void BeginGuidance(TankNukeProjectile projectile, Camera camera)
    {
        FreeCameraMod.StopCamera();
        guided = projectile;
        var player = GameInstance.Instance.GetFirstLocalPlayerController();
        input = player ? player.GetPlayerControllerInputManager() : null;
        if (input) { input.DisablePlayerTransformInput(InputHandle); input.DisableInteratorInput(InputHandle); }
        cursorWasVisible = Cursor.visible; previousLock = Cursor.lockState; Cursor.visible = false; Cursor.lockState = CursorLockMode.Locked;
        cameraDriver = camera.gameObject.AddComponent<TankGuidanceCamera>(); cameraDriver.Initialize(projectile);
    }
    internal void EndGuidance()
    {
        if (!guided && !cameraDriver && !input) return;
        if (guided) guided.StopGuidance();
        guided = null;
        if (cameraDriver) { cameraDriver.enabled = false; Destroy(cameraDriver); }
        cameraDriver = null;
        if (input) { input.EnablePlayerTransformInput(InputHandle); input.EnableInteratorInput(InputHandle); }
        input = null; Cursor.visible = cursorWasVisible; Cursor.lockState = previousLock;
    }
    private void OnDestroy() { EndGuidance(); if (vehicle) vehicle.RemoveNotAllowedOptimizationHandle(this); }
}

internal sealed class TankNukeProjectile : MonoBehaviour
{
    private NukeTankController owner;
    private PlayerVehicle tank;
    private Vector3 direction;
    private bool guided;
    private float yaw, pitch, expires;
    internal Vector3 Direction => direction;
    internal void StopGuidance() { guided = false; owner = null; }
    internal void Initialize(NukeTankController controller, PlayerVehicle vehicle, Vector3 heading, bool isGuided)
    {
        owner = controller; tank = vehicle; direction = heading.normalized; guided = isGuided;
        var angles = Quaternion.LookRotation(direction).eulerAngles; yaw = angles.y; pitch = angles.x > 180f ? angles.x - 360f : angles.x;
        expires = Time.unscaledTime + 20f;
    }
    private void Update()
    {
        if (!GameInstance.InstanceExists || Time.unscaledTime >= expires) { Destroy(gameObject); return; }
        if (guided && !Cursor.visible)
        {
            yaw += Input.GetAxis("Mouse X") * 2.2f;
            pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 2.2f, -85f, 85f);
            direction = Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
        }
    }
    private void FixedUpdate()
    {
        var step = 85f * Time.fixedDeltaTime;
        var hits = Physics.RaycastAll(transform.position, direction, step + 0.4f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        var shooter = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var character = shooter ? shooter.GetPlayerCharacter() : null;
        foreach (var hit in hits)
        {
            if (!hit.collider || (tank && hit.transform.IsChildOf(tank.transform)) || (character && hit.transform.IsChildOf(character.transform))) continue;
            TankExplosion.Blast(hit.point, 25f, 5000f, tank, shooter);
            Destroy(gameObject); return;
        }
        transform.position += direction * step;
        transform.rotation = Quaternion.LookRotation(direction) * Quaternion.Euler(90f, 0f, 0f);
    }
    private void OnDestroy() { if (guided && owner) owner.EndGuidance(); }
}

[DefaultExecutionOrder(10000)]
internal sealed class TankGuidanceCamera : MonoBehaviour
{
    private TankNukeProjectile projectile;
    internal void Initialize(TankNukeProjectile target) => projectile = target;
    private void LateUpdate()
    {
        if (!projectile) return;
        var forward = projectile.Direction;
        transform.SetPositionAndRotation(projectile.transform.position - forward * 5f + Vector3.up * 1.2f,
            Quaternion.LookRotation(forward, Vector3.up));
    }
}

internal static class TankExplosion
{
    internal static void Blast(Vector3 point, float radius, float impulse, PlayerVehicle firingTank, PlayerController shooter)
    {
        var bodies = new HashSet<Rigidbody>(); var cars = new HashSet<PlayerVehicle>();
        var props = new HashSet<DynamicObject>(); var breakables = new HashSet<DestructableDynamicObject>();
        var players = new HashSet<PlayerController>();
        foreach (var collider in Physics.OverlapSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!collider || (firingTank && collider.transform.IsChildOf(firingTank.transform))) continue;
            var character = collider.GetComponentInParent<PlayerCharacter>();
            var controller = character ? character.GetPlayerController() : null;
            if (controller && controller != shooter) players.Add(controller);
            var vehicle = collider.GetComponentInParent<PlayerVehicle>(); if (vehicle && vehicle != firingTank) cars.Add(vehicle);
            var destructible = collider.GetComponentInParent<DestructableDynamicObject>(); if (destructible) breakables.Add(destructible);
            var dynamicProp = collider.GetComponentInParent<DynamicObject>(); if (dynamicProp && !vehicle && !character) props.Add(dynamicProp);
            var body = collider.attachedRigidbody; if (body && !body.isKinematic) bodies.Add(body);
        }
        foreach (var body in bodies) if (body) body.AddExplosionForce(impulse, point, radius, 1f, ForceMode.Impulse);
        foreach (var vehicle in cars) if (vehicle) vehicle.DestroyGameObject();
        foreach (var item in breakables) if (item) item.Break();
        foreach (var item in props) if (item) item.DestroyGameObject();
        foreach (var player in players) if (player) { player.ServerDestoryPlayerCharacter(true, false); Plugin.RunCoroutine(Respawn(player)); }
        if (TankNativeExplosion.Show(point, radius)) return;
        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere); flash.name = "Tank explosion fallback"; flash.transform.position = point;
        var shape = flash.GetComponent<Collider>(); if (shape) UnityEngine.Object.Destroy(shape);
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        Material material = null;
        if (shader) { material = new Material(shader) { color = new Color(1f, 0.45f, 0.05f, 0.7f) }; flash.GetComponent<Renderer>().sharedMaterial = material; }
        Plugin.RunCoroutine(Animate(flash, material, radius));
    }
    private static IEnumerator Respawn(PlayerController player) { yield return new WaitForSeconds(0.5f); if (player) player.ClientRequestRespawn(-1); }
    private static IEnumerator Animate(GameObject flash, Material material, float radius)
    {
        var start = Time.unscaledTime;
        while (flash && Time.unscaledTime - start < 0.45f)
        {
            var fraction = (Time.unscaledTime - start) / 0.45f;
            flash.transform.localScale = Vector3.one * Mathf.Lerp(0.5f, radius * 2f, fraction);
            if (material) material.color = new Color(1f, 0.45f, 0.05f, 0.65f * (1f - fraction));
            yield return null;
        }
        if (flash) UnityEngine.Object.Destroy(flash); if (material) UnityEngine.Object.Destroy(material);
    }
}

/// <summary>Visual shell only: original network vehicle, colliders and driver seat remain intact.</summary>
internal sealed class CustomTankVisual : MonoBehaviour
{
    private readonly List<Material> materials = new();
    private readonly Dictionary<Renderer, bool> originalRenderers = new();

    internal static void Create(PlayerVehicle vehicle)
    {
        if (!vehicle || vehicle.GetComponentInChildren<CustomTankVisual>()) return;
        var root = new GameObject("Custom armored tank visuals");
        root.transform.SetParent(vehicle.transform, false);
        root.AddComponent<CustomTankVisual>().Build(vehicle.transform);
    }
    private void Build(Transform vehicle)
    {
        var bounds = new Bounds(Vector3.zero, new Vector3(2.5f, 1.5f, 4.5f));
        var initialized = false;
        foreach (var renderer in vehicle.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer || renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer) continue;
            var b = renderer.bounds;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var p = vehicle.InverseTransformPoint(b.center + Vector3.Scale(b.extents, new Vector3(x, y, z)));
                if (!initialized) { bounds = new Bounds(p, Vector3.zero); initialized = true; } else bounds.Encapsulate(p);
            }
            if (IsWheelOrTrack(renderer.transform, vehicle)) continue;
            originalRenderers[renderer] = renderer.enabled;
            renderer.enabled = false;
        }
        var width = Mathf.Clamp(bounds.size.x, 2f, 4f);
        var length = Mathf.Clamp(bounds.size.z, 3.5f, 7f);
        var bottom = Mathf.Clamp(bounds.min.y, -2f, 1f);
        var center = new Vector3(bounds.center.x, bottom, bounds.center.z);
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        var green = MakeMaterial(shader, new Color(0.23f, 0.31f, 0.14f));
        var dark = MakeMaterial(shader, new Color(0.10f, 0.13f, 0.08f));
        var black = MakeMaterial(shader, new Color(0.025f, 0.025f, 0.025f));
        var steel = MakeMaterial(shader, new Color(0.35f, 0.38f, 0.28f));
        Part("Armored hull", PrimitiveType.Cube, center + new Vector3(0f, 0.75f, 0f), new Vector3(width * 0.8f, 1.1f, length * 0.82f), Quaternion.identity, green);
        Part("Front armor", PrimitiveType.Cube, center + new Vector3(0f, 1.12f, length * 0.36f), new Vector3(width * 0.8f, 0.5f, 0.42f), Quaternion.Euler(-20f, 0f, 0f), steel);
        Part("Turret", PrimitiveType.Cylinder, center + new Vector3(0f, 1.48f, -length * 0.08f), new Vector3(width * 0.64f, 0.30f, width * 0.64f), Quaternion.identity, green);
        Part("Main cannon", PrimitiveType.Cylinder, center + new Vector3(0f, 1.53f, length * 0.35f), new Vector3(0.32f, length * 0.25f, 0.32f), Quaternion.Euler(90f, 0f, 0f), dark);
        Part("Cannon muzzle", PrimitiveType.Cylinder, center + new Vector3(0f, 1.53f, length * 0.83f), new Vector3(0.42f, 0.16f, 0.42f), Quaternion.Euler(90f, 0f, 0f), black);
        Part("Hatch", PrimitiveType.Cylinder, center + new Vector3(0f, 1.85f, -length * 0.12f), new Vector3(0.58f, 0.08f, 0.58f), Quaternion.identity, dark);
        Part("Minigun mount", PrimitiveType.Cube, center + new Vector3(width * 0.23f, 1.94f, 0f), new Vector3(0.2f, 0.22f, 0.36f), Quaternion.identity, black);
        Part("Minigun barrel", PrimitiveType.Cylinder, center + new Vector3(width * 0.23f, 1.94f, 0.37f), new Vector3(0.09f, 0.35f, 0.09f), Quaternion.Euler(90f, 0f, 0f), black);
        for (var side = -1; side <= 1; side += 2)
        {
            Part("Tank track", PrimitiveType.Cube, center + new Vector3(side * width * 0.47f, 0.43f, 0f), new Vector3(width * 0.24f, 0.7f, length * 0.9f), Quaternion.identity, black);
            for (var i = 0; i < 5; i++)
                Part("Track wheel", PrimitiveType.Cylinder, center + new Vector3(side * width * 0.6f, 0.43f, -length * 0.32f + i * length * 0.16f), new Vector3(0.52f, 0.05f, 0.52f), Quaternion.Euler(0f, 0f, 90f), steel);
        }
    }
    private Material MakeMaterial(Shader shader, Color color)
    {
        if (!shader) return null;
        var material = new Material(shader) { color = color }; materials.Add(material); return material;
    }
    private static bool IsWheelOrTrack(Transform part, Transform vehicle)
    {
        while (part && part != vehicle)
        {
            var name = part.name.ToLowerInvariant();
            if (name.Contains("wheel") || name.Contains("tire") || name.Contains("tyre") || name.Contains("track") || name.Contains("tread")) return true;
            part = part.parent;
        }
        return false;
    }
    private void Part(string name, PrimitiveType type, Vector3 position, Vector3 scale, Quaternion rotation, Material material)
    {
        var part = GameObject.CreatePrimitive(type); part.name = name;
        var collider = part.GetComponent<Collider>(); if (collider) { collider.enabled = false; Destroy(collider); }
        part.transform.SetParent(transform, false); part.transform.localPosition = position;
        part.transform.localRotation = rotation; part.transform.localScale = scale;
        if (material) part.GetComponent<Renderer>().sharedMaterial = material;
    }
    private void OnDestroy()
    {
        foreach (var item in originalRenderers) if (item.Key) item.Key.enabled = item.Value;
        foreach (var material in materials) if (material) Destroy(material);
        originalRenderers.Clear(); materials.Clear();
    }
}

/// <summary>Plays the game's own explosion particles and FMOD event, without invoking its extra damage logic.</summary>
internal static class TankNativeExplosion
{
    private const string Path = "Assets/Content/Game/Prefabs/Particles/Explosions/Explosion.prefab";
    private static readonly FieldInfo SoundField = AccessTools.Field(typeof(BaseParticle), "playEvent");
    private static AsyncOperationHandle<GameObject> handle;
    private static bool loading, ready, failed;
    private static int generation;
    private static int activeEffects;
    internal static void Preload()
    {
        if (loading || ready || failed) return;
        loading = true; var token = generation; Plugin.RunCoroutine(Load(token));
    }
    private static IEnumerator Load(int token)
    {
        var request = Addressables.LoadAssetAsync<GameObject>(Path);
        yield return request;
        if (token != generation) { if (request.IsValid()) Addressables.Release(request); yield break; }
        loading = false;
        if (request.Status == AsyncOperationStatus.Succeeded && request.Result)
        { handle = request; ready = true; Plugin.Log?.LogInfo("Tank loaded Wobbly Life's original explosion prefab."); }
        else
        {
            if (request.IsValid()) Addressables.Release(request);
            failed = true; Plugin.Log?.LogWarning("Tank could not load the original explosion; using the fallback effect.");
        }
    }
    internal static bool Show(Vector3 point, float radius)
    {
        if (!ready || !handle.IsValid() || !handle.Result) { Preload(); return false; }
        if (activeEffects >= 12) return true;
        GameObject root = null;
        try
        {
            root = new GameObject("Native Wobbly explosion (tank)"); root.SetActive(false);
            root.transform.position = point;
            root.transform.localScale = Vector3.one * (radius > 5f ? 3f : 0.5f);
            var instance = UnityEngine.Object.Instantiate(handle.Result, root.transform, false);
            var native = instance.GetComponentsInChildren<BaseParticle>(true);
            string sound = null;
            foreach (var particle in native)
            {
                if (!particle) continue;
                if (string.IsNullOrEmpty(sound) && SoundField != null) sound = SoundField.GetValue(particle) as string;
                particle.enabled = false;
            }
            foreach (var collider in instance.GetComponentsInChildren<Collider>(true)) if (collider) collider.enabled = false;
            instance.SetActive(true); root.SetActive(true);
            foreach (var particle in instance.GetComponentsInChildren<ParticleSystem>(true)) if (particle) particle.Play(true);
            if (!string.IsNullOrEmpty(sound))
            {
                try { var audio = RuntimeManager.CreateInstance(sound); audio.set3DAttributes(RuntimeUtils.To3DAttributes(point)); audio.start(); audio.release(); }
                catch (Exception exception) { Plugin.Log?.LogWarning("Native tank explosion sound failed: " + exception.Message); }
            }
            activeEffects++; Plugin.RunCoroutine(RemoveEffect(root, radius > 5f ? 6f : 1.2f));
            return true;
        }
        catch (Exception exception)
        {
            if (root) UnityEngine.Object.Destroy(root);
            Plugin.Log?.LogWarning("Native tank explosion effect failed: " + exception.Message); return false;
        }
    }
    private static IEnumerator RemoveEffect(GameObject root, float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (root) UnityEngine.Object.Destroy(root);
        activeEffects = Mathf.Max(0, activeEffects - 1);
    }
    internal static void Shutdown()
    {
        generation++; loading = false; failed = false; ready = false;
        if (handle.IsValid()) Addressables.Release(handle);
        handle = default;
    }
}

internal sealed class TankBulletVisual : MonoBehaviour
{
    private static Material black;
    private Vector3 start, end;
    private float born;
    internal static void Create(Vector3 origin, Vector3 destination)
    {
        var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere); dot.name = "Tank minigun black dot";
        var collider = dot.GetComponent<Collider>(); if (collider) { collider.enabled = false; Destroy(collider); }
        if (!black)
        {
            var shader = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Standard");
            if (shader) black = new Material(shader) { color = Color.black };
        }
        if (black) dot.GetComponent<Renderer>().sharedMaterial = black;
        dot.transform.localScale = Vector3.one * 0.07f;
        var visual = dot.AddComponent<TankBulletVisual>(); visual.start = origin; visual.end = destination; visual.born = Time.unscaledTime;
    }
    private void Update()
    {
        var progress = (Time.unscaledTime - born) / 0.1f;
        if (progress >= 1f) { Destroy(gameObject); return; }
        transform.position = Vector3.Lerp(start, end, progress);
    }
    internal static void Shutdown() { if (black) Destroy(black); black = null; }
}

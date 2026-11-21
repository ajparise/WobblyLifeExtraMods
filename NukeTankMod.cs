using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FMODUnity;
using HawkNetworking;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

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
        new TextWrapped("NukeTankHelp", "Host/offline: spawn the tank and enter the driver seat. Press 5 for a nuke, hold O for minigun fire, or press H for a mouse-guided nuke and follow-camera. The minigun has 1,000 rounds and reloads in 3.5 seconds; both nuke modes share 100 rounds and reload in 5.3 seconds. Reload starts automatically when empty. Explosions damage supported cars, breakable props and players. The tank base is networked; weapon visuals and guided camera are local to the host."),
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
    }
    internal static void Shutdown() { if (tank) tank.EndGuidance(); tank = null; spawning = false; }
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
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(vehicle.transform) || (character && hit.transform.IsChildOf(character.transform))) continue;
            TankExplosion.Blast(hit.point, 2.5f, 500f, vehicle, shooter);
            break;
        }
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
        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere); flash.name = "Tank explosion"; flash.transform.position = point;
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

using System;
using System.Collections.Generic;
using System.Linq;
using HawkNetworking;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class PowerClothesCrazyCarsMod : BaseMod
{
    private const string TntNetworkId = "a4698caf71b91d242a93797c3d7a016f";
    private static readonly string[] PowerNames = { "Speed Boots", "Jetpack Jacket", "Shockwave Helmet" };
    private static readonly Ref<string[]> PowerItems = new(PowerNames);
    private static readonly Ref<int> SelectedPower = new();
    private static readonly Ref<bool> ClothesEnabled = new();
    private static readonly Ref<bool> CarsEnabled = new();
    private static readonly Ref<string> Status = new("Choose power clothes or enter a car to apply crazy upgrades.");
    private static GameObject clothingVisual;
    private static Transform clothingOwner;
    private static Material clothingMaterial;
    private static float nextShockwave;
    private static float nextMissile;
    private static PlayerVehicle activeVehicle;
    private static Rigidbody activeVehicleBody;
    private static CrazyCarCollisionSensor carSensor;
    private static bool savedVehicleCollision;
    private static bool vehiclePhasing;
    private static float vehiclePhaseUntil;
    private static readonly Dictionary<Renderer, bool> VehicleRendererStates = new();

    public override string Name => "Power Clothes & Crazy Cars";
    public override string Description => "Wear custom power gear and turn your current car into an extreme invisible missile-equipped jet car.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 80f, Label = "Clothes power strength")]
    public static Ref<float> ClothesPower = new(30f);

    [ModSetting(Order = 20, Min = 30f, Max = 300f, Label = "Crazy car maximum speed")]
    public static Ref<float> CarMaximumSpeed = new(125f);

    [ModSetting(Order = 30, Min = 10f, Max = 150f, Label = "Car acceleration")]
    public static Ref<float> CarAcceleration = new(65f);

    [ModSetting(Order = 40, Label = "Car phases through walls")]
    public static Ref<bool> CarWallPhase = new(true);

    [ModSetting(Order = 50, Label = "Invisible car")]
    public static Ref<bool> InvisibleCar = new(false);

    [ModSetting(Order = 60, Label = "Car jetpack")]
    public static Ref<bool> CarJetpack = new(true);

    [ModSetting(Order = 70, Label = "Heat-seeking missiles")]
    public static Ref<bool> HomingMissiles = new(true);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("PowerCrazyHelp",
                "Power clothes: select gear and apply it. Speed Boots boost movement, Jetpack Jacket flies with Space, " +
                "and Shockwave Helmet blasts nearby physics objects with Q. Crazy cars upgrade the vehicle you drive: " +
                "W accelerates, Space uses car jets, and X launches a missile at the closest other vehicle."),
            new SearchableCombo("Power clothing", PowerNames).WithItems(PowerItems).WithSelectedIndex(SelectedPower),
            base.BuildPanel(id),
            new HStack("PowerClothesActions",
                ActionMenu(new Button("Wear selected power clothes", WearSelected), nameof(WearSelected)),
                ActionMenu(new Button("Remove power clothes", RemoveClothes), nameof(RemoveClothes))
            ).WithContentWidth(),
            new HStack("CrazyCarActions",
                ActionMenu(new Button("Enable crazy cars", EnableCars), nameof(EnableCars)),
                ActionMenu(new Button("Disable car upgrades", DisableCars), nameof(DisableCars)),
                ActionMenu(new Button("Fire missile now", FireMissile), nameof(FireMissile))
            ).WithContentWidth(),
            new TextWrapped("PowerCrazyStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void WearSelected()
    {
        ClothesEnabled.Value = true;
        DestroyClothingVisual();
        Status.Value = $"{CurrentPowerName()} equipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void RemoveClothes()
    {
        ClothesEnabled.Value = false;
        DestroyClothingVisual();
        Status.Value = "Power clothes removed.";
    }

    [ModAction(ShowInUI = false)]
    public static void EnableCars()
    {
        CarsEnabled.Value = true;
        Status.Value = "Crazy car upgrades enabled. Enter a vehicle to apply them.";
    }

    [ModAction(ShowInUI = false)]
    public static void DisableCars()
    {
        CarsEnabled.Value = false;
        ClearVehicle();
        Status.Value = "Crazy car upgrades disabled and vehicle appearance restored.";
    }

    public override void Update()
    {
        var controller = LocalPlayer();
        if (!controller) { DestroyClothingVisual(); ClearVehicle(); return; }
        var character = controller.GetPlayerCharacter();
        if (ClothesEnabled.Value && character) UpdatePowerClothes(character);
        else DestroyClothingVisual();

        if (!CarsEnabled.Value) return;
        var vehicle = CurrentVehicle(controller);
        if (vehicle != activeVehicle) SetVehicle(vehicle);
        if (activeVehicle && activeVehicleBody) UpdateCrazyCar();
    }

    private static void UpdatePowerClothes(PlayerCharacter character)
    {
        var playerBody = character.GetComponentInChildren<PlayerBody>(true);
        var body = playerBody ? playerBody.GetRigidbody() : null;
        if (!body) return;
        EnsureClothingVisual(body.transform);

        var camera = Camera.main;
        var forward = camera ? Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized : body.transform.forward;
        var right = camera ? Vector3.ProjectOnPlane(camera.transform.right, Vector3.up).normalized : body.transform.right;
        var strength = Mathf.Max(5f, ClothesPower.Value);

        switch (Mathf.Clamp(SelectedPower.Value, 0, PowerNames.Length - 1))
        {
            case 0:
                var movement = Vector3.zero;
                if (Input.GetKey(KeyCode.W)) movement += forward;
                if (Input.GetKey(KeyCode.S)) movement -= forward;
                if (Input.GetKey(KeyCode.D)) movement += right;
                if (Input.GetKey(KeyCode.A)) movement -= right;
                if (movement.sqrMagnitude > 0.01f) body.AddForce(movement.normalized * strength, ForceMode.Acceleration);
                break;
            case 1:
                if (Input.GetKey(KeyCode.Space))
                    body.AddForce((Vector3.up + forward * 0.18f).normalized * strength, ForceMode.Acceleration);
                break;
            case 2:
                if (Input.GetKeyDown(KeyCode.Q) && Time.unscaledTime >= nextShockwave)
                {
                    nextShockwave = Time.unscaledTime + 0.8f;
                    foreach (var hit in Physics.OverlapSphere(body.worldCenterOfMass, 12f, ~0, QueryTriggerInteraction.Ignore))
                    {
                        var target = hit.attachedRigidbody;
                        if (!target || target.isKinematic || target.transform.IsChildOf(character.transform)) continue;
                        var direction = (target.worldCenterOfMass - body.worldCenterOfMass).normalized;
                        target.AddForce((direction + Vector3.up * 0.25f).normalized * strength, ForceMode.VelocityChange);
                    }
                }
                break;
        }
    }

    private static void UpdateCrazyCar()
    {
        if (vehiclePhasing && Time.unscaledTime >= vehiclePhaseUntil)
        {
            activeVehicleBody.detectCollisions = savedVehicleCollision;
            vehiclePhasing = false;
        }

        SetVehicleInvisible(InvisibleCar.Value);
        if (Cursor.visible) return;
        if (Input.GetKey(KeyCode.W))
            activeVehicleBody.AddForce(activeVehicle.transform.forward * CarAcceleration.Value, ForceMode.Acceleration);
        if (CarJetpack.Value && Input.GetKey(KeyCode.Space))
            activeVehicleBody.AddForce(Vector3.up * CarAcceleration.Value * 0.75f, ForceMode.Acceleration);

        var max = Mathf.Max(30f, CarMaximumSpeed.Value);
        if (activeVehicleBody.velocity.sqrMagnitude > max * max)
            activeVehicleBody.velocity = activeVehicleBody.velocity.normalized * max;
        if (HomingMissiles.Value && Input.GetKeyDown(KeyCode.X)) FireMissile();
    }

    [ModAction(ShowInUI = false)]
    public static void FireMissile()
    {
        if (!CarsEnabled.Value || !activeVehicle || !activeVehicleBody)
        { Status.Value = "Enter a car with crazy car upgrades enabled first."; return; }
        if (!HomingMissiles.Value) { Status.Value = "Heat-seeking missiles are disabled."; return; }
        if (!PropSpawnManager.IsServer) { Status.Value = "Only the offline player or lobby host can launch missiles."; return; }
        if (Time.unscaledTime < nextMissile) return;
        nextMissile = Time.unscaledTime + 0.8f;

        var target = UnityEngine.Object.FindObjectsOfType<PlayerVehicle>()
            .Where(vehicle => vehicle && vehicle != activeVehicle)
            .OrderBy(vehicle => (vehicle.transform.position - activeVehicle.transform.position).sqrMagnitude)
            .FirstOrDefault();
        if (!target) { Status.Value = "No other vehicle found for the missile to track."; return; }

        var position = activeVehicleBody.worldCenterOfMass + activeVehicle.transform.forward * 3f + Vector3.up;
        NetworkPrefab.SpawnNetworkPrefab(TntNetworkId, behaviour =>
        {
            if (behaviour == null) { Status.Value = "Missile prefab failed to spawn."; return; }
            var missile = behaviour.gameObject;
            IgnoreVehicleCollision(missile, activeVehicle);
            var homing = missile.AddComponent<CrazyCarHomingMissile>();
            homing.Configure(target.transform, missile.GetComponentInChildren<Bomb>(true));
            Status.Value = $"Heat-seeking missile launched at {target.name}.";
        }, position: position, rotation: activeVehicle.transform.rotation, owner: null,
           bUseChunkSystem: true, bSendTransform: true, bCheckChunk: true);
    }

    private static void SetVehicle(PlayerVehicle vehicle)
    {
        ClearVehicle();
        if (!vehicle) return;
        activeVehicle = vehicle;
        var movement = vehicle.GetVehicleMovementBase();
        activeVehicleBody = movement ? movement.GetRigidbody() : vehicle.GetComponentInChildren<Rigidbody>();
        if (!activeVehicleBody) { activeVehicle = null; return; }
        carSensor = activeVehicleBody.gameObject.AddComponent<CrazyCarCollisionSensor>();
        carSensor.Configure(activeVehicleBody);
        Status.Value = $"Crazy upgrades applied to {vehicle.name}.";
    }

    private static void ClearVehicle()
    {
        SetVehicleInvisible(false);
        if (vehiclePhasing && activeVehicleBody) activeVehicleBody.detectCollisions = savedVehicleCollision;
        if (carSensor) UnityEngine.Object.Destroy(carSensor);
        activeVehicle = null;
        activeVehicleBody = null;
        carSensor = null;
        vehiclePhasing = false;
    }

    internal static void PhaseVehicleThroughWall(Rigidbody body, Collision collision)
    {
        if (!CarsEnabled.Value || !CarWallPhase.Value || body != activeVehicleBody || collision.contactCount == 0) return;
        var other = collision.collider ? collision.collider.attachedRigidbody : null;
        if (other && !other.isKinematic) return;
        var steep = Enumerable.Range(0, collision.contactCount)
            .Any(index => Mathf.Abs(collision.GetContact(index).normal.y) < 0.7f);
        if (!steep) return;
        if (!vehiclePhasing) savedVehicleCollision = body.detectCollisions;
        body.detectCollisions = false;
        vehiclePhasing = true;
        vehiclePhaseUntil = Time.unscaledTime + 0.75f;
        Status.Value = "Wall detected: car phasing briefly while road collision stays enabled normally.";
    }

    private static PlayerController LocalPlayer() =>
        GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;

    private static PlayerVehicle CurrentVehicle(PlayerController controller)
    {
        var entered = controller.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
        var vehicle = entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
        return vehicle && vehicle.GetDriverPlayerController() == controller ? vehicle : null;
    }

    private static void SetVehicleInvisible(bool invisible)
    {
        if (!activeVehicle) return;
        if (invisible && VehicleRendererStates.Count == 0)
        {
            foreach (var renderer in activeVehicle.GetComponentsInChildren<Renderer>(true))
            { if (!renderer) continue; VehicleRendererStates[renderer] = renderer.enabled; renderer.enabled = false; }
        }
        else if (!invisible && VehicleRendererStates.Count > 0)
        {
            foreach (var saved in VehicleRendererStates) if (saved.Key) saved.Key.enabled = saved.Value;
            VehicleRendererStates.Clear();
        }
    }

    private static void IgnoreVehicleCollision(GameObject missile, PlayerVehicle vehicle)
    {
        foreach (var a in missile.GetComponentsInChildren<Collider>(true))
        foreach (var b in vehicle.GetComponentsInChildren<Collider>(true))
            if (a && b) Physics.IgnoreCollision(a, b, true);
    }

    private static string CurrentPowerName() => PowerNames[Mathf.Clamp(SelectedPower.Value, 0, PowerNames.Length - 1)];

    private static void EnsureClothingVisual(Transform owner)
    {
        if (clothingVisual && clothingOwner == owner) return;
        DestroyClothingVisual();
        clothingOwner = owner;
        clothingVisual = new GameObject($"ExtraMods {CurrentPowerName()}");
        clothingVisual.transform.SetParent(owner, false);
        var color = SelectedPower.Value == 0 ? new Color(1f, 0.12f, 0.05f) :
                    SelectedPower.Value == 1 ? new Color(0.05f, 0.55f, 1f) : new Color(0.7f, 0.1f, 1f);
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        if (shader) { clothingMaterial = new Material(shader); clothingMaterial.color = color; }

        if (SelectedPower.Value == 0)
        {
            CreateGearPart("Left Power Boot", PrimitiveType.Cube, new Vector3(-0.2f, -0.55f, 0f), new Vector3(0.22f, 0.22f, 0.35f));
            CreateGearPart("Right Power Boot", PrimitiveType.Cube, new Vector3(0.2f, -0.55f, 0f), new Vector3(0.22f, 0.22f, 0.35f));
        }
        else if (SelectedPower.Value == 1)
        {
            CreateGearPart("Jet Tank Left", PrimitiveType.Cylinder, new Vector3(-0.2f, 0.05f, -0.3f), new Vector3(0.16f, 0.45f, 0.16f));
            CreateGearPart("Jet Tank Right", PrimitiveType.Cylinder, new Vector3(0.2f, 0.05f, -0.3f), new Vector3(0.16f, 0.45f, 0.16f));
        }
        else
        {
            CreateGearPart("Shockwave Helmet", PrimitiveType.Sphere, new Vector3(0f, 0.65f, 0f), new Vector3(0.52f, 0.3f, 0.52f));
        }
    }

    private static void CreateGearPart(string name, PrimitiveType primitive, Vector3 position, Vector3 scale)
    {
        var part = GameObject.CreatePrimitive(primitive); part.name = name;
        var collider = part.GetComponent<Collider>(); if (collider) UnityEngine.Object.Destroy(collider);
        part.transform.SetParent(clothingVisual.transform, false); part.transform.localPosition = position; part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>(); if (renderer && clothingMaterial) renderer.sharedMaterial = clothingMaterial;
    }

    private static void DestroyClothingVisual()
    {
        if (clothingVisual) UnityEngine.Object.Destroy(clothingVisual);
        if (clothingMaterial) UnityEngine.Object.Destroy(clothingMaterial);
        clothingVisual = null; clothingOwner = null; clothingMaterial = null;
    }
}

internal sealed class CrazyCarCollisionSensor : MonoBehaviour
{
    private Rigidbody body;
    internal void Configure(Rigidbody target) => body = target;
    private void OnCollisionEnter(Collision collision) => PowerClothesCrazyCarsMod.PhaseVehicleThroughWall(body, collision);
}

internal sealed class CrazyCarHomingMissile : MonoBehaviour
{
    private Transform target;
    private Rigidbody body;
    private Bomb bomb;
    private float armedAt;
    internal void Configure(Transform targetTransform, Bomb targetBomb)
    {
        target = targetTransform; bomb = targetBomb; body = GetComponentInChildren<Rigidbody>(); armedAt = Time.time + 0.2f;
        if (body) { body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; body.velocity = transform.forward * 35f; }
        UnityEngine.Object.Destroy(gameObject, 15f);
    }
    private void FixedUpdate()
    {
        if (!target || !body) return;
        var desired = (target.position - body.worldCenterOfMass).normalized * 48f;
        body.velocity = Vector3.RotateTowards(body.velocity, desired, 3.5f * Time.fixedDeltaTime, 18f * Time.fixedDeltaTime);
        if (body.velocity.sqrMagnitude > 0.1f) transform.rotation = Quaternion.LookRotation(body.velocity.normalized);
    }
    private void OnCollisionEnter(Collision collision)
    {
        if (Time.time < armedAt || !bomb) return;
        bomb.Explode(); enabled = false;
    }
}

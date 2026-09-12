using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>Adds wings, toggleable boosters, and speed-gated flight to a spawned road vehicle.</summary>
internal sealed class RocketWingCarController : MonoBehaviour
{
    private readonly List<Transform> flames = new();
    private PlayerVehicle vehicle;
    private Rigidbody body;
    private bool boostersActive;
    private bool flightActive;
    private float flightStartedAt;

    internal static void Attach(GameObject spawned)
    {
        if (!spawned || spawned.GetComponentInChildren<RocketWingCarController>(true)) return;
        var controller = spawned.AddComponent<RocketWingCarController>();
        controller.Initialize();
    }

    private void Initialize()
    {
        vehicle = GetComponent<PlayerVehicle>() ?? GetComponentInChildren<PlayerVehicle>(true);
        var movement = vehicle ? vehicle.GetVehicleMovementBase() : null;
        body = movement ? movement.GetRigidbody() : GetComponentInChildren<Rigidbody>();
        BuildRocketCarVisuals();
    }

    private void Update()
    {
        if (!vehicle || !body)
        {
            vehicle = GetComponent<PlayerVehicle>() ?? GetComponentInChildren<PlayerVehicle>(true);
            var movement = vehicle ? vehicle.GetVehicleMovementBase() : null;
            body = movement ? movement.GetRigidbody() : GetComponentInChildren<Rigidbody>();
            if (!vehicle || !body) return;
        }

        var localDriver = HasLocalDriver();
        if (localDriver && !Cursor.visible && Input.GetKeyDown(KeyCode.K))
        {
            boostersActive = !boostersActive;
            VehicleAircraftSpawnerMod.NotifyRocketWingCar(
                boostersActive ? "Boosters ON. Press K again to turn them off." : "Boosters OFF.");
        }
        UpdateFlames(boostersActive && localDriver);
    }

    private void FixedUpdate()
    {
        if (!vehicle || !body || !HasLocalDriver()) return;
        var forward = vehicle.transform.forward.normalized;
        var speed = body.velocity.magnitude;
        var forwardSpeed = Vector3.Dot(body.velocity, forward);
        var takeoffSpeed = Mathf.Clamp(VehicleAircraftSpawnerMod.RocketTakeoffSpeed.Value, 10f, 80f);

        if (boostersActive)
            body.AddForce(forward * Mathf.Clamp(VehicleAircraftSpawnerMod.RocketBoosterPower.Value, 5f, 100f),
                ForceMode.Acceleration);

        if (!flightActive && forwardSpeed >= takeoffSpeed)
        {
            flightActive = true;
            flightStartedAt = Time.time;
            body.AddForce(Vector3.up * Mathf.Clamp(takeoffSpeed * 0.28f, 5f, 15f), ForceMode.VelocityChange);
            VehicleAircraftSpawnerMod.NotifyRocketWingCar("Takeoff speed reached — wings are generating lift!");
        }

        if (!flightActive)
        {
            ClampSpeed();
            return;
        }

        var speedLift = Mathf.Clamp01(speed / takeoffSpeed);
        var lift = Physics.gravity.magnitude +
                   Mathf.Clamp(VehicleAircraftSpawnerMod.RocketLiftPower.Value, 2f, 35f) * speedLift;
        body.AddForce(Vector3.up * lift, ForceMode.Acceleration);

        if (!Cursor.visible)
        {
            var pitch = 0f;
            var yaw = 0f;
            var roll = 0f;
            if (Input.GetKey(KeyCode.W)) pitch += 1f;
            if (Input.GetKey(KeyCode.S)) pitch -= 1f;
            if (Input.GetKey(KeyCode.A)) { yaw -= 1f; roll += 1f; }
            if (Input.GetKey(KeyCode.D)) { yaw += 1f; roll -= 1f; }

            body.AddRelativeTorque(new Vector3(pitch * 8f, yaw * 5f, roll * 11f), ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.Space)) body.AddForce(Vector3.up * 18f, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.LeftControl)) body.AddForce(Vector3.down * 18f, ForceMode.Acceleration);
        }

        // Mild aerodynamic stability keeps a car-shaped body controllable without removing its playful physics.
        var localVelocity = vehicle.transform.InverseTransformDirection(body.velocity);
        var sideways = vehicle.transform.right * localVelocity.x;
        body.AddForce(-sideways * 1.5f, ForceMode.Acceleration);
        ClampSpeed();

        if (Time.time - flightStartedAt > 1.5f && IsGrounded() && speed < takeoffSpeed * 0.55f)
        {
            flightActive = false;
            VehicleAircraftSpawnerMod.NotifyRocketWingCar("Landed. Build speed again for another takeoff.");
        }
    }

    private bool HasLocalDriver()
    {
        if (!vehicle || !GameInstance.InstanceExists) return false;
        var local = GameInstance.Instance.GetFirstLocalPlayerController();
        return local && vehicle.GetDriverPlayerController() == local;
    }

    private bool IsGrounded()
    {
        var hits = Physics.RaycastAll(body.worldCenterOfMass + Vector3.up * 0.3f, Vector3.down, 2.5f, ~0,
            QueryTriggerInteraction.Ignore);
        foreach (var hit in hits)
            if (hit.collider && !hit.collider.transform.IsChildOf(vehicle.transform)) return true;
        return false;
    }

    private void ClampSpeed()
    {
        var maximum = Mathf.Clamp(VehicleAircraftSpawnerMod.RocketMaximumSpeed.Value, 40f, 250f);
        if (body.velocity.sqrMagnitude > maximum * maximum)
            body.velocity = body.velocity.normalized * maximum;
    }

    private void BuildRocketCarVisuals()
    {
        var root = vehicle ? vehicle.transform : transform;
        var bounds = CalculateBounds(root);
        var localCenter = root.InverseTransformPoint(bounds.center);
        var halfWidth = Mathf.Clamp(bounds.extents.x, 0.9f, 3.2f);
        var halfLength = Mathf.Clamp(bounds.extents.z, 1.4f, 5f);
        var wingY = localCenter.y + Mathf.Clamp(bounds.extents.y * 0.15f, 0.15f, 0.7f);

        CreatePart(root, "Rocket Wing Left", PrimitiveType.Cube,
            new Vector3(localCenter.x - halfWidth * 1.28f, wingY, localCenter.z),
            new Vector3(halfWidth * 1.15f, 0.12f, halfLength * 0.7f), new Vector3(0f, -8f, -5f),
            new Color(0.12f, 0.18f, 0.28f), true);
        CreatePart(root, "Rocket Wing Right", PrimitiveType.Cube,
            new Vector3(localCenter.x + halfWidth * 1.28f, wingY, localCenter.z),
            new Vector3(halfWidth * 1.15f, 0.12f, halfLength * 0.7f), new Vector3(0f, 8f, 5f),
            new Color(0.12f, 0.18f, 0.28f), true);

        CreatePart(root, "Left Wing Tip", PrimitiveType.Cube,
            new Vector3(localCenter.x - halfWidth * 1.9f, wingY + 0.16f, localCenter.z),
            new Vector3(0.12f, 0.65f, halfLength * 0.52f), new Vector3(0f, -8f, -10f),
            new Color(0.95f, 0.24f, 0.05f), true);
        CreatePart(root, "Right Wing Tip", PrimitiveType.Cube,
            new Vector3(localCenter.x + halfWidth * 1.9f, wingY + 0.16f, localCenter.z),
            new Vector3(0.12f, 0.65f, halfLength * 0.52f), new Vector3(0f, 8f, 10f),
            new Color(0.95f, 0.24f, 0.05f), true);

        for (var side = -1; side <= 1; side += 2)
        {
            var x = localCenter.x + side * halfWidth * 0.62f;
            var z = localCenter.z - halfLength * 0.9f;
            CreatePart(root, "Rocket Booster", PrimitiveType.Cylinder,
                new Vector3(x, wingY + 0.05f, z), new Vector3(0.38f, 0.85f, 0.38f),
                new Vector3(90f, 0f, 0f), new Color(0.22f, 0.23f, 0.27f), true);
            var flame = CreatePart(root, "Unlimited Rocket Flame", PrimitiveType.Capsule,
                new Vector3(x, wingY + 0.05f, z - 1.05f), new Vector3(0.22f, 0.72f, 0.22f),
                new Vector3(90f, 0f, 0f), new Color(0.05f, 0.65f, 1f), true).transform;
            flame.gameObject.SetActive(false);
            flames.Add(flame);
            var light = flame.gameObject.AddComponent<Light>();
            light.color = new Color(0.1f, 0.55f, 1f);
            light.range = 7f;
            light.intensity = 2.2f;
        }
    }

    private static Bounds CalculateBounds(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(root.position, new Vector3(2f, 1.5f, 4f));
        var bounds = renderers[0].bounds;
        for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return bounds;
    }

    private static GameObject CreatePart(Transform parent, string name, PrimitiveType primitive,
        Vector3 localPosition, Vector3 localScale, Vector3 localEuler, Color color, bool emissive)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.Destroy(collider);
        part.transform.SetParent(parent, false);
        part.transform.localPosition = localPosition;
        part.transform.localScale = localScale;
        part.transform.localRotation = Quaternion.Euler(localEuler);
        var renderer = part.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = color;
            if (emissive && renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", color * 1.4f);
            }
        }
        return part;
    }

    private void UpdateFlames(bool visible)
    {
        for (var i = 0; i < flames.Count; i++)
        {
            var flame = flames[i];
            if (!flame) continue;
            if (flame.gameObject.activeSelf != visible) flame.gameObject.SetActive(visible);
            if (!visible) continue;
            var pulse = 0.78f + Mathf.Abs(Mathf.Sin(Time.unscaledTime * 18f + i)) * 0.55f;
            flame.localScale = new Vector3(0.22f * pulse, 0.72f * pulse, 0.22f * pulse);
        }
    }
}

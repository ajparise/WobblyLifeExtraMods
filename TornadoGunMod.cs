using System;
using System.Collections.Generic;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>An equippable launcher that creates moving, physics-enabled tornadoes.</summary>
public sealed class TornadoGunMod : BaseMod
{
    private static readonly Vector3 HandOffset = new(0f, 0.18f, 0.13f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Tornado Gun is unequipped.");
    private static readonly List<TornadoEffect> ActiveTornadoes = new();
    private static GameObject gunRoot;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextFireTime;
    private static float recoilUntil;

    public override string Name => "Tornado Gun";
    public override string Description =>
        "Fire moving tornadoes that pull in, lift, spin, and finally throw Wobblies, vehicles, and physics props.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 2f, Max = 18f, Label = "Tornado radius")]
    public static Ref<float> Radius = new(7f);
    [ModSetting(Order = 20, Min = 3f, Max = 30f, Label = "Tornado height")]
    public static Ref<float> Height = new(14f);
    [ModSetting(Order = 30, Min = 2f, Max = 80f, Label = "Inward pull")]
    public static Ref<float> PullStrength = new(32f);
    [ModSetting(Order = 40, Min = 2f, Max = 80f, Label = "Spin strength")]
    public static Ref<float> SpinStrength = new(38f);
    [ModSetting(Order = 50, Min = 1f, Max = 60f, Label = "Lift strength")]
    public static Ref<float> LiftStrength = new(24f);
    [ModSetting(Order = 60, Min = 0f, Max = 60f, Label = "Final throw strength")]
    public static Ref<float> ThrowStrength = new(22f);
    [ModSetting(Order = 70, Min = 2f, Max = 18f, Label = "Tornado lifetime")]
    public static Ref<float> Lifetime = new(8f);
    [ModSetting(Order = 80, Min = 0f, Max = 25f, Label = "Travel speed")]
    public static Ref<float> TravelSpeed = new(8f);
    [ModSetting(Order = 90, Min = 20f, Max = 300f, Label = "Maximum travel distance")]
    public static Ref<float> TravelDistance = new(120f);
    [ModSetting(Order = 100, Min = 0.1f, Max = 2f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.65f);
    [ModSetting(Order = 110, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();
    [ModSetting(Order = 120, Min = 1f, Max = 8f, Label = "Maximum active tornadoes")]
    public static Ref<float> MaximumActive = new(4f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("TornadoGunHelp",
                "Equip and close F2, then left-click to launch a moving tornado along the crosshair. It pulls nearby " +
                "objects inward, lifts and spins them, and throws them outward when it expires. Your own Wobbly is ignored. " +
                "Enable Rapid fire to hold the trigger. Physics effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("TornadoGunActions",
                ActionMenu(new Button("Equip Tornado Gun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip Tornado Gun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire)),
                ActionMenu(new Button("Clear tornadoes", ClearTornadoes), nameof(ClearTornadoes))
            ).WithContentWidth(),
            new TextWrapped("TornadoGunStatus", "").WithText(Status));
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
        FireworkMinigunMod.Unequip();
        JellyGunMod.Unequip();
        EquippedState.Value = true;
        EnsureGunModel();
        Status.Value = "Tornado Gun equipped. Close F2 and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (gunRoot) Object.Destroy(gunRoot);
        gunRoot = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Tornado Gun unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearTornadoes()
    {
        for (var i = ActiveTornadoes.Count - 1; i >= 0; i--)
            if (ActiveTornadoes[i]) Object.Destroy(ActiveTornadoes[i].gameObject);
        ActiveTornadoes.Clear();
        Status.Value = "All tornadoes cleared.";
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;
        if (!HasLocalPlayer())
        {
            Unequip();
            ClearTornadoes();
            return;
        }
        EnsureGunModel();
        UpdateGunModel();
        if (Cursor.visible) return;
        if (RapidFire.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can create tornadoes.";
            return;
        }
        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var shooter = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the Tornado Gun.";
            return;
        }

        PruneTornadoes();
        var limit = Mathf.Clamp(Mathf.RoundToInt(MaximumActive.Value), 1, 8);
        while (ActiveTornadoes.Count >= limit)
        {
            var oldest = ActiveTornadoes[0];
            ActiveTornadoes.RemoveAt(0);
            if (oldest) Object.Destroy(oldest.gameObject);
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.1f, FireInterval.Value);
        recoilUntil = Time.unscaledTime + 0.1f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var horizontal = Vector3.ProjectOnPlane(ray.direction, Vector3.up);
        if (horizontal.sqrMagnitude < 0.01f) horizontal = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up);
        if (horizontal.sqrMagnitude < 0.01f) horizontal = shooter.transform.forward;
        horizontal.Normalize();

        var root = new GameObject("ExtraMods Moving Tornado");
        root.transform.position = FindGround(ray.origin + horizontal * 5f, shooter);
        var effect = root.AddComponent<TornadoEffect>();
        effect.Initialize(shooter, horizontal);
        ActiveTornadoes.Add(effect);
        Status.Value = $"Tornado launched. Active: {ActiveTornadoes.Count}/{limit}.";
    }

    private static Vector3 FindGround(Vector3 position, PlayerCharacter shooter)
    {
        var hits = Physics.RaycastAll(position + Vector3.up * 25f, Vector3.down, 70f, ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(shooter.transform)) continue;
            if (hit.collider.attachedRigidbody && !hit.collider.attachedRigidbody.isKinematic) continue;
            return hit.point + Vector3.up * 0.15f;
        }
        return position;
    }

    internal static Vector3 FollowGround(Vector3 position, PlayerCharacter shooter)
    {
        var grounded = FindGround(position, shooter);
        if (Mathf.Abs(grounded.y - position.y) <= 8f) position.y = grounded.y;
        return position;
    }

    internal static void Unregister(TornadoEffect effect) => ActiveTornadoes.Remove(effect);

    private static void PruneTornadoes()
    {
        for (var i = ActiveTornadoes.Count - 1; i >= 0; i--)
            if (!ActiveTornadoes[i]) ActiveTornadoes.RemoveAt(i);
    }

    private static void EnsureGunModel()
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
        if (gunRoot)
        {
            if (gunRoot.transform.parent != anchor) gunRoot.transform.SetParent(anchor, true);
            return;
        }

        gunRoot = new GameObject("ExtraMods Tornado Gun");
        gunRoot.transform.SetParent(anchor, false);
        CreateGunPart("Storm Receiver", PrimitiveType.Cube, new Vector3(0f, 0f, 0.18f), Vector3.zero,
            new Vector3(0.32f, 0.28f, 0.85f), new Color(0.12f, 0.2f, 0.24f));
        CreateGunPart("Cyclone Barrel", PrimitiveType.Cylinder, new Vector3(0f, 0.01f, 0.72f),
            new Vector3(90f, 0f, 0f), new Vector3(0.2f, 0.5f, 0.2f), new Color(0.5f, 0.85f, 0.92f));
        CreateGunPart("Storm Grip", PrimitiveType.Cube, new Vector3(0f, -0.27f, 0.02f),
            new Vector3(-15f, 0f, 0f), new Vector3(0.17f, 0.4f, 0.22f), new Color(0.08f, 0.12f, 0.15f));
        for (var i = 0; i < 3; i++)
            CreateGunPart("Muzzle Wind Disc", PrimitiveType.Cylinder, new Vector3(0f, 0.01f, 1.02f + i * 0.12f),
                new Vector3(90f, 0f, 0f), new Vector3(0.22f + i * 0.04f, 0.025f, 0.22f + i * 0.04f),
                new Color(0.55f, 0.95f, 1f));
    }

    private static void UpdateGunModel()
    {
        if (!gunRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var position = anchor.position + rotation * HandOffset;
        if (Time.unscaledTime < recoilUntil) position -= ray.direction * 0.12f;
        gunRoot.transform.position = Vector3.Lerp(gunRoot.transform.position, position, 0.65f);
        gunRoot.transform.rotation = Quaternion.Slerp(gunRoot.transform.rotation, rotation, 0.65f);
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!HasLocalPlayer() || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController().GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static bool HasLocalPlayer() => GameInstance.InstanceExists &&
        GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();

    private static void CreateGunPart(string name, PrimitiveType primitive, Vector3 position,
        Vector3 rotation, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.Destroy(collider);
        part.transform.SetParent(gunRoot.transform, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        var oldMatrix = GUI.matrix;
        GUI.color = Color.Lerp(new Color(0.35f, 0.9f, 1f), Color.white,
            0.25f + Mathf.PingPong(Time.unscaledTime * 1.5f, 0.5f));
        GUIUtility.RotateAroundPivot(Time.unscaledTime * 120f, new Vector2(x, y));
        GUI.DrawTexture(new Rect(x - 22f, y - 2f, 14f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 8f, y - 2f, 14f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 22f, 3f, 14f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 8f, 3f, 14f), Texture2D.whiteTexture);
        GUI.matrix = oldMatrix;
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class TornadoEffect : MonoBehaviour
{
    private readonly HashSet<int> processed = new();
    private readonly List<LineRenderer> spirals = new();
    private PlayerCharacter owner;
    private Vector3 direction;
    private Vector3 start;
    private float bornAt;
    private float phase;
    private bool threwTargets;

    internal void Initialize(PlayerCharacter shooter, Vector3 travelDirection)
    {
        owner = shooter;
        direction = Vector3.ProjectOnPlane(travelDirection, Vector3.up).normalized;
        start = transform.position;
        bornAt = Time.time;
        BuildVisual();
    }

    private void Update()
    {
        var age = Time.time - bornAt;
        var lifetime = Mathf.Clamp(TornadoGunMod.Lifetime.Value, 2f, 18f);
        if (age >= lifetime || Vector3.Distance(start, transform.position) >= TornadoGunMod.TravelDistance.Value)
        {
            ThrowAndExpire();
            return;
        }
        var next = transform.position + direction * Mathf.Max(0f, TornadoGunMod.TravelSpeed.Value) * Time.deltaTime;
        transform.position = TornadoGunMod.FollowGround(next, owner);
        phase += Time.deltaTime * 4.5f;
        UpdateVisual(age / lifetime);
    }

    private void FixedUpdate() => ApplyVortex(false);

    private void ApplyVortex(bool finalThrow)
    {
        if (!owner) return;
        var radius = Mathf.Clamp(TornadoGunMod.Radius.Value, 2f, 18f);
        var height = Mathf.Clamp(TornadoGunMod.Height.Value, 3f, 30f);
        var scanRadius = Mathf.Sqrt(radius * radius + height * height * 0.25f);
        var center = transform.position + Vector3.up * height * 0.5f;
        var colliders = Physics.OverlapSphere(center, scanRadius, ~0, QueryTriggerInteraction.Ignore);
        processed.Clear();

        foreach (var collider in colliders)
        {
            if (!collider || collider.transform.IsChildOf(owner.transform)) continue;
            var vehicle = collider.GetComponentInParent<PlayerVehicle>();
            var character = vehicle ? null : collider.GetComponentInParent<PlayerCharacter>();
            var playerBody = character ? character.GetComponentInChildren<PlayerBody>(true) : null;
            var body = vehicle
                ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true) ?? collider.attachedRigidbody
                : playerBody ? playerBody.GetRigidbody() : collider.attachedRigidbody;
            if (!body || body.isKinematic) continue;
            var id = vehicle ? vehicle.GetInstanceID() : character ? character.GetInstanceID() : body.GetInstanceID();
            if (!processed.Add(id)) continue;

            var offset = body.worldCenterOfMass - transform.position;
            var flat = Vector3.ProjectOnPlane(offset, Vector3.up);
            var distance = flat.magnitude;
            if (offset.y < -1.5f || offset.y > height || distance > radius) continue;
            var outward = distance > 0.1f ? flat / distance : Vector3.right;
            var tangent = Vector3.Cross(Vector3.up, outward).normalized;
            var closeness = Mathf.Clamp01(1f - distance / radius);

            if (finalThrow)
            {
                var throwVelocity = (outward * 0.8f + tangent * 0.35f + Vector3.up * 0.65f).normalized *
                                    Mathf.Max(0f, TornadoGunMod.ThrowStrength.Value);
                ApplyVelocity(character, body, throwVelocity, true);
                continue;
            }
            var acceleration = -outward * TornadoGunMod.PullStrength.Value * (0.35f + closeness * 0.65f) +
                               tangent * TornadoGunMod.SpinStrength.Value +
                               Vector3.up * TornadoGunMod.LiftStrength.Value * (0.45f + closeness * 0.55f);
            ApplyVelocity(character, body, acceleration, false);
            if (character && character.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("tornado hit");
        }
    }

    private static void ApplyVelocity(PlayerCharacter character, Rigidbody body, Vector3 force, bool instant)
    {
        if (character)
        {
            var ragdoll = character.GetRagdollController();
            var playerBody = character.GetComponentInChildren<PlayerBody>(true);
            if (ragdoll) ragdoll.Ragdoll();
            if (playerBody)
            {
                var velocity = instant ? force : body.velocity + force * Time.fixedDeltaTime;
                playerBody.SetRagdollVelocity(Vector3.ClampMagnitude(velocity, instant ? 80f : 45f));
                return;
            }
        }
        body.WakeUp();
        body.AddForce(force, instant ? ForceMode.VelocityChange : ForceMode.Acceleration);
        if (!character)
            body.AddTorque(Vector3.up * TornadoGunMod.SpinStrength.Value * (instant ? 0.3f : 0.08f),
                instant ? ForceMode.VelocityChange : ForceMode.Acceleration);
    }

    private void ThrowAndExpire()
    {
        if (threwTargets) return;
        threwTargets = true;
        ApplyVortex(true);
        Destroy(gameObject, 0.08f);
    }

    private void BuildVisual()
    {
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        for (var i = 0; i < 5; i++)
        {
            var child = new GameObject($"Tornado Spiral {i + 1}");
            child.transform.SetParent(transform, false);
            var line = child.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.positionCount = 28;
            line.startWidth = 0.22f;
            line.endWidth = 0.06f;
            if (shader) line.material = new Material(shader);
            spirals.Add(line);
        }
        UpdateVisual(0f);
    }

    private void UpdateVisual(float normalizedAge)
    {
        var radius = Mathf.Clamp(TornadoGunMod.Radius.Value, 2f, 18f);
        var height = Mathf.Clamp(TornadoGunMod.Height.Value, 3f, 30f);
        for (var strand = 0; strand < spirals.Count; strand++)
        {
            var line = spirals[strand];
            if (!line) continue;
            for (var i = 0; i < line.positionCount; i++)
            {
                var t = i / (line.positionCount - 1f);
                var taper = Mathf.Lerp(0.15f, radius, Mathf.Pow(t, 0.72f));
                var angle = phase + strand * Mathf.PI * 0.4f + t * Mathf.PI * 5.5f;
                var wobble = 0.88f + Mathf.Sin(t * 18f + phase * 1.7f + strand) * 0.12f;
                line.SetPosition(i, new Vector3(Mathf.Cos(angle) * taper * wobble, t * height,
                    Mathf.Sin(angle) * taper * wobble));
            }
            var fade = 1f - Mathf.Clamp01((normalizedAge - 0.82f) / 0.18f);
            line.startColor = new Color(0.72f, 0.9f, 0.95f, 0.82f * fade);
            line.endColor = new Color(0.35f, 0.62f, 0.72f, 0.22f * fade);
        }
    }

    private void OnDestroy() => TornadoGunMod.Unregister(this);
}

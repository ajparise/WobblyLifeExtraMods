using System;
using System.Collections;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>A randomized physics toy that applies one of several colorful effects to the aimed target.</summary>
public sealed class ChaosWandMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Chaos Wand is unequipped.");
    private static float nextFireTime;

    public override string Name => "Chaos Wand";
    public override string Description =>
        "Zap a Wobbly, car, or physics prop to randomly launch it, spin it, float it, or blast it with confetti.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 10f, Max = 500f, Label = "Wand range")]
    public static Ref<float> Range = new(160f);

    [ModSetting(Order = 20, Min = 5f, Max = 100f, Label = "Chaos strength")]
    public static Ref<float> Strength = new(32f);

    [ModSetting(Order = 30, Min = 1f, Max = 15f, Label = "Float duration")]
    public static Ref<float> FloatDuration = new(5f);

    [ModSetting(Order = 40, Min = 0.1f, Max = 2f, Label = "Cooldown")]
    public static Ref<float> Cooldown = new(0.45f);

    [ModSetting(Order = 50, Label = "Rapid chaos")]
    public static Ref<bool> RapidFire = new();

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("ChaosWandHelp",
                "Equip the wand, close F2, aim with the color-changing crosshair, and left-click. Each zap randomly " +
                "launches, tornado-spins, floats, or confetti-blasts the aimed Wobbly, vehicle, or movable prop. " +
                "Physics effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("ChaosWandActions",
                ActionMenu(new Button("Equip Chaos Wand", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip Chaos Wand", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Cast once", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("ChaosWandStatus", "").WithText(Status));
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
        PortalGunMod.Unequip();
        LightningGunMod.Unequip();
        WobblyHeadHomingGunMod.Unequip();
        BananaPeelLauncherMod.Unequip();
        TornadoGunMod.Unequip();
        FireworkMinigunMod.Unequip();
        JellyGunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip();
        MosesStaffMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Chaos Wand equipped. Close F2, aim, and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Chaos Wand unequipped.";
    }

    public override void Update()
    {
        if (!EquippedState.Value || Cursor.visible) return;
        if (!GameInstance.InstanceExists || !GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter())
        {
            Unequip();
            return;
        }

        if (RapidFire.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can cast physics chaos.";
            return;
        }

        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var shooter = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before using the Chaos Wand.";
            return;
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.1f, Cooldown.Value);
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        if (!TryFindTarget(ray, Mathf.Max(10f, Range.Value), shooter, out var hit))
        {
            CreateBeam(ray.origin, ray.origin + ray.direction * Range.Value);
            Status.Value = "The chaos zap missed.";
            return;
        }

        CreateBeam(ray.origin, hit.point);
        ApplyRandomEffect(hit, shooter);
    }

    private static bool TryFindTarget(Ray ray, float range, PlayerCharacter shooter, out RaycastHit selected)
    {
        selected = default;
        var hits = Physics.RaycastAll(ray, range, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(shooter.transform)) continue;
            selected = hit;
            return true;
        }
        return false;
    }

    private static void ApplyRandomEffect(RaycastHit hit, PlayerCharacter shooter)
    {
        var character = hit.collider.GetComponentInParent<PlayerCharacter>();
        var vehicle = hit.collider.GetComponentInParent<PlayerVehicle>();
        var body = vehicle ? vehicle.GetComponent<Rigidbody>() : hit.collider.attachedRigidbody;
        var mode = UnityEngine.Random.Range(0, 4);
        var direction = (hit.point - shooter.transform.position).normalized;
        if (direction.sqrMagnitude < 0.01f) direction = Vector3.forward;

        if (character && character != shooter)
        {
            if (character.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("chaos wand hit");
            ApplyToWobbly(character, mode, direction, hit.point);
            return;
        }

        switch (mode)
        {
            case 0:
                if (body && !body.isKinematic)
                    body.AddForce((direction * 0.55f + Vector3.up * 0.8f).normalized * Strength.Value,
                        ForceMode.VelocityChange);
                CreateConfetti(hit.point, 8);
                Status.Value = body ? "CHAOS: Super launch!" : "CHAOS: The wall refuses to fly, but it sparkles.";
                break;
            case 1:
                if (body && !body.isKinematic)
                {
                    body.AddTorque(UnityEngine.Random.onUnitSphere * Strength.Value * 2.5f,
                        ForceMode.VelocityChange);
                    body.AddForce(Vector3.up * Strength.Value * 0.25f, ForceMode.VelocityChange);
                }
                CreateSpiral(hit.point);
                Status.Value = body ? "CHAOS: Tornado spin!" : "CHAOS: A tiny tornado appeared.";
                break;
            case 2:
                if (body && !body.isKinematic)
                {
                    var floating = body.GetComponent<ChaosFloatEffect>() ?? body.gameObject.AddComponent<ChaosFloatEffect>();
                    floating.Configure(Mathf.Clamp(FloatDuration.Value, 1f, 15f));
                }
                CreateConfetti(hit.point, 12);
                Status.Value = body ? "CHAOS: Zero gravity!" : "CHAOS: Gravity ignored your request.";
                break;
            default:
                CreateConfetti(hit.point, 32);
                if (body && !body.isKinematic)
                    body.AddForce(Vector3.up * Strength.Value * 0.18f, ForceMode.VelocityChange);
                Status.Value = "CHAOS: CONFETTI BLAST!";
                break;
        }
    }

    private static void ApplyToWobbly(PlayerCharacter character, int mode, Vector3 direction, Vector3 point)
    {
        var ragdoll = character.GetRagdollController();
        var playerBody = character.GetComponentInChildren<PlayerBody>(true);
        if (ragdoll) ragdoll.Ragdoll();

        var strength = Mathf.Max(5f, Strength.Value);
        switch (mode)
        {
            case 0:
                if (playerBody)
                    playerBody.SetRagdollVelocity(direction * strength * 0.55f + Vector3.up * strength);
                CreateConfetti(point, 10);
                Status.Value = "CHAOS: Wobbly super launch!";
                break;
            case 1:
                foreach (var limb in character.GetComponentsInChildren<Rigidbody>(true))
                    if (limb && !limb.isKinematic)
                        limb.AddTorque(UnityEngine.Random.onUnitSphere * strength * 1.8f, ForceMode.VelocityChange);
                if (playerBody) playerBody.SetRagdollVelocity(Vector3.up * strength * 0.35f);
                CreateSpiral(point);
                Status.Value = "CHAOS: Wobbly tornado!";
                break;
            case 2:
                if (playerBody) playerBody.SetRagdollVelocity(Vector3.up * strength * 0.55f);
                foreach (var limb in character.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (!limb || limb.isKinematic) continue;
                    var floating = limb.GetComponent<ChaosFloatEffect>() ?? limb.gameObject.AddComponent<ChaosFloatEffect>();
                    floating.Configure(Mathf.Clamp(FloatDuration.Value, 1f, 15f));
                }
                CreateConfetti(point, 14);
                Status.Value = "CHAOS: Floating Wobbly!";
                break;
            default:
                if (playerBody) playerBody.SetRagdollVelocity(Vector3.up * strength * 0.22f);
                CreateConfetti(point, 36);
                Status.Value = "CHAOS: Wobbly confetti party!";
                break;
        }
    }

    private static void CreateBeam(Vector3 start, Vector3 end)
    {
        var root = new GameObject("ExtraMods Chaos Beam");
        var beam = root.AddComponent<LineRenderer>();
        beam.positionCount = 2;
        beam.useWorldSpace = true;
        beam.SetPosition(0, start);
        beam.SetPosition(1, end);
        beam.startWidth = 0.075f;
        beam.endWidth = 0.018f;
        beam.startColor = Color.HSVToRGB(Mathf.Repeat(Time.time * 0.4f, 1f), 0.9f, 1f);
        beam.endColor = Color.HSVToRGB(Mathf.Repeat(Time.time * 0.4f + 0.45f, 1f), 0.9f, 1f);
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader) beam.material = new Material(shader);
        UnityEngine.Object.Destroy(root, 0.18f);
    }

    private static void CreateConfetti(Vector3 point, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var piece = GameObject.CreatePrimitive(i % 2 == 0 ? PrimitiveType.Cube : PrimitiveType.Sphere);
            piece.name = "Chaos Confetti";
            piece.transform.position = point + UnityEngine.Random.insideUnitSphere * 0.35f;
            piece.transform.localScale = i % 2 == 0
                ? new Vector3(0.06f, 0.025f, 0.12f)
                : Vector3.one * 0.065f;
            var collider = piece.GetComponent<Collider>();
            if (collider) collider.enabled = false;
            var renderer = piece.GetComponent<Renderer>();
            if (renderer)
                renderer.material.color = Color.HSVToRGB(UnityEngine.Random.value, 0.9f, 1f);
            var body = piece.AddComponent<Rigidbody>();
            body.mass = 0.01f;
            body.velocity = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(2f, 7f) + Vector3.up * 5f;
            body.angularVelocity = UnityEngine.Random.onUnitSphere * 15f;
            UnityEngine.Object.Destroy(piece, UnityEngine.Random.Range(1.5f, 3.5f));
        }
    }

    private static void CreateSpiral(Vector3 point)
    {
        for (var i = 0; i < 14; i++)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Chaos Spiral";
            var angle = i / 14f * Mathf.PI * 4f;
            marker.transform.position = point + new Vector3(Mathf.Cos(angle), i * 0.09f, Mathf.Sin(angle)) * 0.65f;
            marker.transform.localScale = Vector3.one * 0.09f;
            var collider = marker.GetComponent<Collider>();
            if (collider) collider.enabled = false;
            marker.GetComponent<Renderer>().material.color = Color.HSVToRGB(i / 14f, 0.9f, 1f);
            UnityEngine.Object.Destroy(marker, 0.7f);
        }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = Color.HSVToRGB(Mathf.Repeat(Time.unscaledTime * 0.35f, 1f), 0.9f, 1f);
        GUI.DrawTexture(new Rect(x - 18f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 6f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 18f, 4f, 12f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 6f, 4f, 12f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class ChaosFloatEffect : MonoBehaviour
{
    private Rigidbody body;
    private float expiresAt;

    internal void Configure(float duration)
    {
        body = GetComponent<Rigidbody>();
        expiresAt = Mathf.Max(expiresAt, Time.time + duration);
        if (body) body.WakeUp();
    }

    private void FixedUpdate()
    {
        if (!body || Time.time >= expiresAt)
        {
            Destroy(this);
            return;
        }

        body.AddForce(-Physics.gravity * 1.08f, ForceMode.Acceleration);
        body.AddForce(Vector3.up * 0.7f, ForceMode.Acceleration);
    }
}

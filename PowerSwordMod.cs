using System;
using System.Collections;
using System.Collections.Generic;
using FMODUnity;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>A handheld animated sword with knockback strikes and instant vehicle destruction.</summary>
public sealed class PowerSwordMod : BaseMod
{
    private const string NativeSwingSound = "event:/Jobs/WoodCutterJob/AxeSwing";
    private static readonly Vector3 HandOffset = new(0.05f, 0.02f, 0.18f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Power Sword is unequipped.");
    private static GameObject swordRoot;
    private static Transform bladeGlow;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextSwingTime;
    private static float swingStartedAt = -10f;

    public override string Name => "Power Sword";
    public override string Description =>
        "Swing a glowing sword that knocks back Wobblies and physics objects and instantly explodes cars.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 2f, Max = 10f, Label = "Sword reach")]
    public static Ref<float> Reach = new(4.5f);
    [ModSetting(Order = 20, Min = 0.5f, Max = 4f, Label = "Swing width")]
    public static Ref<float> SwingWidth = new(1.5f);
    [ModSetting(Order = 30, Min = 5f, Max = 100f, Label = "Knockback power")]
    public static Ref<float> KnockbackPower = new(35f);
    [ModSetting(Order = 40, Min = 0.15f, Max = 2f, Label = "Swing cooldown")]
    public static Ref<float> SwingCooldown = new(0.45f);
    [ModSetting(Order = 50, Min = 3f, Max = 15f, Label = "Car explosion radius")]
    public static Ref<float> ExplosionRadius = new(6f);
    [ModSetting(Order = 60, Min = 0f, Max = 1f, Label = "Blade red")]
    public static Ref<float> BladeRed = new(0.08f);
    [ModSetting(Order = 70, Min = 0f, Max = 1f, Label = "Blade green")]
    public static Ref<float> BladeGreen = new(0.7f);
    [ModSetting(Order = 80, Min = 0f, Max = 1f, Label = "Blade blue")]
    public static Ref<float> BladeBlue = new(1f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("PowerSwordHelp",
                "Equip and close F2, then left-click to swing. Every Wobbly or movable physics object caught by the short " +
                "sword arc is knocked away from you. A struck car explodes instantly and is removed directly, so it does not " +
                "leave a rusty wreck. Destructive effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("PowerSwordActions",
                ActionMenu(new Button("Equip Power Sword", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip sword", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Swing once", Swing), nameof(Swing))
            ).WithContentWidth(),
            new TextWrapped("PowerSwordStatus", "").WithText(Status));
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
        TornadoGunMod.Unequip();
        FireworkMinigunMod.Unequip();
        JellyGunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip();
        MosesStaffMod.Unequip();
        EquippedState.Value = true;
        EnsureSwordModel();
        Status.Value = "Power Sword equipped. Close F2 and left-click to swing.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (swordRoot) Object.Destroy(swordRoot);
        swordRoot = null;
        bladeGlow = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Power Sword unequipped.";
    }

    public override void Update()
    {
        var character = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!character)
        {
            if (EquippedState.Value) Unequip();
            return;
        }
        if (!EquippedState.Value) return;
        EnsureSwordModel();
        UpdateSwordModel();
        if (!Cursor.visible && Input.GetMouseButtonDown(0)) Swing();
    }

    [ModAction(ShowInUI = false)]
    public static void Swing()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextSwingTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can use the Power Sword.";
            return;
        }
        var camera = Camera.main;
        var shooter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before swinging the sword.";
            return;
        }

        nextSwingTime = Time.unscaledTime + Mathf.Clamp(SwingCooldown.Value, 0.15f, 2f);
        swingStartedAt = Time.unscaledTime;
        var forward = camera.transform.forward.normalized;
        var origin = camera.transform.position + forward * 0.35f - Vector3.up * 0.25f;
        var end = origin + forward * Mathf.Clamp(Reach.Value, 2f, 10f);
        var hits = Physics.OverlapCapsule(origin, end, Mathf.Clamp(SwingWidth.Value, 0.5f, 4f), ~0,
            QueryTriggerInteraction.Ignore);

        var vehicles = new HashSet<PlayerVehicle>();
        var characters = new HashSet<PlayerCharacter>();
        var bodies = new HashSet<Rigidbody>();
        var carHits = 0;
        var knockbackHits = 0;
        foreach (var collider in hits)
        {
            if (!collider || collider.transform.IsChildOf(shooter.transform)) continue;
            var vehicle = collider.GetComponentInParent<PlayerVehicle>();
            if (vehicle)
            {
                if (!vehicles.Add(vehicle)) continue;
                var point = collider.ClosestPoint(origin + forward * 2f);
                ExplodeVehicle(vehicle, point);
                carHits++;
                continue;
            }

            var character = collider.GetComponentInParent<PlayerCharacter>();
            if (character)
            {
                if (!characters.Add(character)) continue;
                KnockbackWobbly(character, origin, forward);
                if (collider.GetComponentInParent<PlayerNPCController>())
                    PoliceChaseMod.ReportNpcHarassment("power sword hit");
                knockbackHits++;
                continue;
            }

            var body = collider.attachedRigidbody;
            if (!body || body.isKinematic || !bodies.Add(body)) continue;
            var away = (forward * 0.75f + (body.worldCenterOfMass - origin).normalized * 0.25f + Vector3.up * 0.18f).normalized;
            body.WakeUp();
            body.AddForceAtPosition(away * Mathf.Clamp(KnockbackPower.Value, 5f, 100f),
                collider.ClosestPoint(origin), ForceMode.VelocityChange);
            body.AddTorque(UnityEngine.Random.onUnitSphere * KnockbackPower.Value * 0.25f, ForceMode.VelocityChange);
            knockbackHits++;
        }

        PlaySwingSound(origin);
        Status.Value = carHits > 0
            ? $"Sword strike exploded {carHits} car(s) and knocked back {knockbackHits} other target(s)."
            : knockbackHits > 0
                ? $"Sword strike knocked back {knockbackHits} target(s)."
                : "Sword swung, but nothing was in range.";
    }

    private static void KnockbackWobbly(PlayerCharacter character, Vector3 origin, Vector3 forward)
    {
        var ragdoll = character.GetRagdollController();
        var playerBody = character.GetComponentInChildren<PlayerBody>(true);
        if (!ragdoll || !playerBody) return;
        ragdoll.Ragdoll();
        var body = playerBody.GetRigidbody();
        var radial = body ? Vector3.ProjectOnPlane(body.worldCenterOfMass - origin, Vector3.up).normalized : forward;
        if (radial.sqrMagnitude < 0.01f) radial = forward;
        var power = Mathf.Clamp(KnockbackPower.Value, 5f, 100f);
        playerBody.SetRagdollVelocity((forward * 0.72f + radial * 0.28f + Vector3.up * 0.3f).normalized * power);
    }

    private static void ExplodeVehicle(PlayerVehicle vehicle, Vector3 point)
    {
        CreateExplosion(point, vehicle);
        vehicle.DestroyGameObject();
    }

    private static void CreateExplosion(Vector3 point, PlayerVehicle destroyedVehicle)
    {
        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "Power Sword Car Explosion";
        flash.transform.position = point;
        flash.transform.localScale = Vector3.one * 0.3f;
        var collider = flash.GetComponent<Collider>();
        if (collider) Object.DestroyImmediate(collider);
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        var material = new Material(shader) { color = new Color(1f, 0.24f, 0.01f, 0.96f) };
        flash.GetComponent<Renderer>().material = material;
        Plugin.RunCoroutine(AnimateExplosion(flash, material));

        var radius = Mathf.Clamp(ExplosionRadius.Value, 3f, 15f);
        var affected = new HashSet<Rigidbody>();
        foreach (var nearby in Physics.OverlapSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            var body = nearby.attachedRigidbody;
            if (!body || body.isKinematic || !affected.Add(body) ||
                body.GetComponentInParent<PlayerVehicle>() == destroyedVehicle) continue;
            body.AddExplosionForce(KnockbackPower.Value * 35f, point, radius, 2.5f, ForceMode.Impulse);
        }
    }

    private static IEnumerator AnimateExplosion(GameObject flash, Material material)
    {
        var elapsed = 0f;
        while (flash && elapsed < 0.4f)
        {
            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / 0.4f);
            flash.transform.localScale = Vector3.one * Mathf.Lerp(0.3f, ExplosionRadius.Value * 1.4f, progress);
            material.color = new Color(1f, Mathf.Lerp(0.55f, 0.02f, progress), 0.01f, 1f - progress);
            yield return null;
        }
        if (flash) Object.Destroy(flash);
        if (material) Object.Destroy(material);
    }

    private static void PlaySwingSound(Vector3 position)
    {
        try
        {
            var sound = RuntimeManager.CreateInstance(NativeSwingSound);
            sound.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            sound.setVolume(1.1f);
            sound.start();
            sound.release();
        }
        catch (Exception exception) { Plugin.Log?.LogWarning($"Power Sword swing sound failed: {exception.Message}"); }
    }

    private static void EnsureSwordModel()
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
        if (swordRoot)
        {
            if (swordRoot.transform.parent != anchor) swordRoot.transform.SetParent(anchor, true);
            UpdateBladeColor();
            return;
        }

        swordRoot = new GameObject("ExtraMods Power Sword");
        swordRoot.transform.SetParent(anchor, true);
        CreatePart(swordRoot.transform, "Leather Grip", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.08f),
            new Vector3(90f, 0f, 0f), new Vector3(0.11f, 0.32f, 0.11f), new Color(0.16f, 0.055f, 0.025f), false);
        CreatePart(swordRoot.transform, "Golden Guard", PrimitiveType.Cube, new Vector3(0f, 0f, 0.42f),
            Vector3.zero, new Vector3(0.85f, 0.1f, 0.12f), new Color(1f, 0.63f, 0.07f), true);
        CreatePart(swordRoot.transform, "Guard Gem", PrimitiveType.Sphere, new Vector3(0f, 0f, 0.43f),
            Vector3.zero, Vector3.one * 0.19f, new Color(1f, 0.12f, 0.03f), true);
        bladeGlow = CreatePart(swordRoot.transform, "Glowing Blade", PrimitiveType.Cube, new Vector3(0f, 0f, 1.32f),
            Vector3.zero, new Vector3(0.19f, 0.055f, 1.72f), BladeColor(), true).transform;
        CreatePart(swordRoot.transform, "Blade Tip", PrimitiveType.Cube, new Vector3(0f, 0f, 2.2f),
            new Vector3(0f, 45f, 0f), new Vector3(0.16f, 0.055f, 0.16f), BladeColor(), true);
        var light = bladeGlow.gameObject.AddComponent<Light>();
        light.color = BladeColor();
        light.range = 4f;
        light.intensity = 1.5f;
    }

    private static void UpdateSwordModel()
    {
        if (!swordRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var aimRotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var elapsed = Time.unscaledTime - swingStartedAt;
        var swingProgress = Mathf.Clamp01(elapsed / 0.3f);
        var swingAngle = elapsed >= 0f && elapsed <= 0.3f ? Mathf.Lerp(-75f, 85f, SmoothStep(swingProgress)) : -8f;
        var rotation = aimRotation * Quaternion.AngleAxis(swingAngle, Vector3.up) * Quaternion.AngleAxis(-12f, Vector3.forward);
        swordRoot.transform.position = Vector3.Lerp(swordRoot.transform.position, anchor.position + aimRotation * HandOffset, 0.72f);
        swordRoot.transform.rotation = Quaternion.Slerp(swordRoot.transform.rotation, rotation, 0.78f);
        UpdateBladeColor();
    }

    private static float SmoothStep(float value) => value * value * (3f - 2f * value);

    private static void UpdateBladeColor()
    {
        if (!bladeGlow) return;
        var color = BladeColor();
        var renderer = bladeGlow.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = color;
            if (renderer.material.HasProperty("_EmissionColor")) renderer.material.SetColor("_EmissionColor", color * 2.5f);
        }
        var light = bladeGlow.GetComponent<Light>();
        if (light) light.color = color;
    }

    private static Color BladeColor() => new(Mathf.Clamp01(BladeRed.Value), Mathf.Clamp01(BladeGreen.Value),
        Mathf.Clamp01(BladeBlue.Value), 1f);

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!GameInstance.InstanceExists || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static GameObject CreatePart(Transform parent, string name, PrimitiveType primitive, Vector3 position,
        Vector3 rotation, Vector3 scale, Color color, bool emissive)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.DestroyImmediate(collider);
        part.transform.SetParent(parent, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = color;
            if (emissive && renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", color * 2.5f);
            }
        }
        return part;
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = BladeColor();
        GUI.DrawTexture(new Rect(x - 11f, y - 1.5f, 8f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 3f, y - 1.5f, 8f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1.5f, y - 11f, 3f, 8f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1.5f, y + 3f, 3f, 8f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

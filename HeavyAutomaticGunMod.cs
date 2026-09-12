using System;
using FMODUnity;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>Three-hundred-round, infinite-reserve automatic weapon with selectable reload behavior.</summary>
public sealed class HeavyAutomaticGunMod : BaseMod
{
    private const int MagazineCapacity = 300;
    private const string NativeGunshotEvent = "event:/Objects/Objects_PaperCannon";
    private const float CrosshairGap = 7f;
    private const float CrosshairLength = 11f;
    private const float CrosshairThickness = 2f;
    private static readonly Vector3 HandGripOffset = new(0f, 0.24f, 0.08f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");

    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Heavy automatic gun is unequipped.");
    private static readonly Ref<string> AmmoStatus = new("Ammo: 300 / ∞");

    private static GameObject gunRoot;
    private static Transform magazineModel;
    private static Transform muzzle;
    private static AudioSource audioSource;
    private static AudioClip shotSound;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static int rounds = MagazineCapacity;
    private static bool reloading;
    private static float reloadStarted;
    private static float reloadFinishes;
    private static float nextShotTime;
    private static float recoilUntil;

    public override string Name => "Heavy Automatic Gun";

    public override string Description =>
        "A visible full-auto gun with a 300-round magazine, infinite reserve ammunition, effects, and two reload modes.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 0.04f, Max = 0.5f, Label = "Automatic fire interval")]
    public static Ref<float> FireInterval = new(0.09f);

    [ModSetting(Order = 20, Label = "Animated reload",
        Description = "On: the gun lowers and its magazine moves during reload. Off: the magazine refills instantly.")]
    public static Ref<bool> AnimatedReload = new(true);

    [ModSetting(Order = 30, Min = 0.4f, Max = 4f, Label = "Animated reload duration")]
    public static Ref<float> ReloadDuration = new(1.35f);

    [ModSetting(Order = 40, Min = 5f, Max = 300f, Label = "Weapon range")]
    public static Ref<float> WeaponRange = new(120f);

    [ModSetting(Order = 50, Min = 0f, Max = 40f, Label = "Impact knockback")]
    public static Ref<float> ImpactKnockback = new(10f);

    [ModSetting(Order = 60, Min = 0f, Max = 1f, Label = "Gunshot volume")]
    public static Ref<float> GunshotVolume = new(0.7f);

    [ModSetting(Order = 70, Label = "Muzzle flash and tracers")]
    public static Ref<bool> ShowShotEffects = new(true);

    [ModSetting(Order = 80, Min = -400f, Max = 400f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(100f);

    [ModSetting(Order = 90, Min = -250f, Max = 250f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(35f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("HeavyGunHelp",
                "Equip, close F2, and hold left-click for fully automatic fire. The magazine holds 300 rounds with " +
                "infinite reserve ammo. Press R to reload early. Disable Animated reload for instant reloading."),
            base.BuildPanel(id),
            new HStack("HeavyGunActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire)),
                ActionMenu(new Button("Reload", Reload), nameof(Reload))
            ).WithContentWidth(),
            new TextWrapped("HeavyGunAmmo", "").WithText(AmmoStatus),
            new TextWrapped("HeavyGunStatus", "").WithText(Status)
        );
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
        EquippedState.Value = true;
        rounds = MagazineCapacity;
        reloading = false;
        UpdateAmmoText();
        EnsureGunModel();
        Status.Value = "Heavy automatic gun equipped. Hold left-click; press R to reload.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        reloading = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (gunRoot) Object.Destroy(gunRoot);
        gunRoot = null;
        magazineModel = null;
        muzzle = null;
        audioSource = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Heavy automatic gun unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || reloading || Time.unscaledTime < nextShotTime) return;
        if (rounds <= 0)
        {
            Reload();
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before firing.";
            return;
        }

        EnsureGunModel();
        nextShotTime = Time.unscaledTime + Mathf.Max(0.04f, FireInterval.Value);
        rounds--;
        UpdateAmmoText();

        var screenPoint = new Vector3(
            Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value,
            0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        var end = ray.origin + ray.direction * Mathf.Max(5f, WeaponRange.Value);

        if (TryRaycastIgnoringShooter(ray, Mathf.Max(5f, WeaponRange.Value), out var hit))
        {
            end = hit.point;
            if (hit.collider.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("weapon hit");
            if (hit.rigidbody && !hit.rigidbody.isKinematic)
                hit.rigidbody.AddForceAtPosition(ray.direction * ImpactKnockback.Value, hit.point, ForceMode.VelocityChange);
            if (ShowShotEffects.Value) CreateImpactSpark(hit.point, hit.normal);
        }

        PlayGunshot();
        if (ShowShotEffects.Value) CreateShotEffects(muzzle ? muzzle.position : ray.origin, end);
        recoilUntil = Time.unscaledTime + 0.07f;
        Status.Value = $"Fired. {rounds} rounds remain.";

        if (rounds == 0) Reload();
    }

    [ModAction(ShowInUI = false)]
    public static void Reload()
    {
        if (!EquippedState.Value || reloading || rounds == MagazineCapacity) return;

        if (!AnimatedReload.Value)
        {
            rounds = MagazineCapacity;
            UpdateAmmoText();
            Status.Value = "Instant reload complete: 300 / ∞.";
            return;
        }

        reloading = true;
        reloadStarted = Time.unscaledTime;
        reloadFinishes = reloadStarted + Mathf.Max(0.4f, ReloadDuration.Value);
        UpdateAmmoText();
        Status.Value = "Reloading...";
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;

        // The plugin survives scene changes. The equipped model must not survive the player
        // character disappearing as a save is closed, or it remains visible over the title screen.
        if (!HasLocalPlayerCharacter())
        {
            UnequipForWorldExit();
            return;
        }

        EnsureGunModel();
        AnimateGun();

        if (Cursor.visible) return;
        if (Input.GetKeyDown(KeyCode.R)) Reload();
        if (Input.GetMouseButton(0)) Fire();
    }

    private static bool HasLocalPlayerCharacter()
    {
        if (!GameInstance.InstanceExists) return false;
        var controller = GameInstance.Instance.GetFirstLocalPlayerController();
        return controller && controller.GetPlayerCharacter();
    }

    private static void UnequipForWorldExit()
    {
        EquippedState.Value = false;
        reloading = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (gunRoot) Object.Destroy(gunRoot);
        gunRoot = null;
        magazineModel = null;
        muzzle = null;
        audioSource = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Heavy automatic gun unequipped because the world was closed.";
    }

    private static void EnsureGunModel()
    {
        var camera = Camera.main;
        if (!camera) return;

        var hand = ResolveRightHand();
        if (!hand) return;

        if (activeHand != hand)
        {
            if (activeHand && handPoseRequested) activeHand.ResetPointing();
            activeHand = hand;
            handPoseRequested = false;
        }

        // Request the pose once. Re-requesting whenever vanilla clears it can emit an RPC every
        // frame, producing simulation/input lag even while the rendering FPS remains high.
        if (!handPoseRequested)
        {
            activeHand.SetPointing(true, true);
            handPoseRequested = true;
        }
        var handAnchor = activeHand.GetAnchorTransform();
        if (!handAnchor) return;

        if (gunRoot)
        {
            if (gunRoot.transform.parent != handAnchor)
                gunRoot.transform.SetParent(handAnchor, true);
            return;
        }

        gunRoot = new GameObject("ExtraMods Heavy Automatic Gun");
        gunRoot.transform.SetParent(handAnchor, true);

        CreateGunPart("Receiver", PrimitiveType.Cube, new Vector3(0f, 0f, 0.15f), Vector3.zero,
            new Vector3(0.28f, 0.25f, 0.9f), new Color(0.11f, 0.12f, 0.14f));
        CreateGunPart("Upper Rail", PrimitiveType.Cube, new Vector3(0f, 0.15f, 0.08f), Vector3.zero,
            new Vector3(0.18f, 0.06f, 0.72f), new Color(0.05f, 0.055f, 0.065f));
        CreateGunPart("Barrel", PrimitiveType.Cylinder, new Vector3(0f, 0.02f, 0.82f), new Vector3(90f, 0f, 0f),
            new Vector3(0.1f, 0.55f, 0.1f), new Color(0.07f, 0.075f, 0.085f));
        CreateGunPart("Stock", PrimitiveType.Cube, new Vector3(0f, -0.02f, -0.55f), new Vector3(-8f, 0f, 0f),
            new Vector3(0.24f, 0.3f, 0.42f), new Color(0.14f, 0.15f, 0.17f));
        CreateGunPart("Grip", PrimitiveType.Cube, new Vector3(0f, -0.25f, -0.05f), new Vector3(-18f, 0f, 0f),
            new Vector3(0.16f, 0.38f, 0.2f), new Color(0.08f, 0.085f, 0.095f));
        magazineModel = CreateGunPart("300 Round Magazine", PrimitiveType.Cube,
            new Vector3(0f, -0.28f, 0.25f), new Vector3(8f, 0f, 0f),
            new Vector3(0.2f, 0.42f, 0.28f), new Color(0.18f, 0.19f, 0.21f));

        muzzle = new GameObject("Muzzle").transform;
        muzzle.SetParent(gunRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, 0.02f, 1.4f);

        audioSource = gunRoot.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 0f;
        audioSource.ignoreListenerPause = true;
        audioSource.priority = 0;
        if (!shotSound) shotSound = CreateGunshotClip();
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!GameInstance.InstanceExists || RightHandField == null) return null;

        var controller = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = controller ? controller.GetPlayerCharacter() : null;
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static Transform CreateGunPart(
        string name, PrimitiveType primitive, Vector3 position, Vector3 rotation, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        Object.Destroy(collider);
        part.transform.SetParent(gunRoot.transform, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
        return part.transform;
    }

    private static void AnimateGun()
    {
        if (!gunRoot || !activeHand) return;

        var camera = Camera.main;
        var handAnchor = activeHand.GetAnchorTransform();
        if (!camera || !handAnchor) return;

        var screenPoint = new Vector3(
            Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value,
            0f);
        var aimRay = camera.ScreenPointToRay(screenPoint);
        var aimRotation = Quaternion.LookRotation(aimRay.direction, camera.transform.up);

        var position = handAnchor.position + aimRotation * HandGripOffset;
        var rotationOffset = Vector3.zero;
        var magazineY = -0.28f;

        if (reloading)
        {
            var duration = Mathf.Max(0.4f, reloadFinishes - reloadStarted);
            var t = Mathf.Clamp01((Time.unscaledTime - reloadStarted) / duration);
            var dip = Mathf.Sin(t * Mathf.PI);
            position += aimRotation * new Vector3(0.08f, -0.42f * dip, -0.12f * dip);
            rotationOffset += new Vector3(28f * dip, 0f, 14f * dip);
            magazineY -= Mathf.Sin(t * Mathf.PI) * 0.55f;

            if (Time.unscaledTime >= reloadFinishes)
            {
                reloading = false;
                rounds = MagazineCapacity;
                position = handAnchor.position + aimRotation * HandGripOffset;
                rotationOffset = Vector3.zero;
                magazineY = -0.28f;
                UpdateAmmoText();
                Status.Value = "Animated reload complete: 300 / ∞.";
            }
        }
        else if (Time.unscaledTime < recoilUntil)
        {
            position -= aimRay.direction * 0.13f;
            rotationOffset.x -= 7f;
        }

        var targetRotation = aimRotation * Quaternion.Euler(rotationOffset);
        gunRoot.transform.position = Vector3.Lerp(gunRoot.transform.position, position, 0.62f);
        gunRoot.transform.rotation = Quaternion.Slerp(gunRoot.transform.rotation, targetRotation, 0.62f);
        if (magazineModel)
        {
            var magazinePosition = magazineModel.localPosition;
            magazinePosition.y = magazineY;
            magazineModel.localPosition = magazinePosition;
        }
    }

    private static bool TryRaycastIgnoringShooter(Ray ray, float range, out RaycastHit selected)
    {
        selected = default;
        var shooter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        var hits = Physics.RaycastAll(ray, range, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider) continue;
            if (shooter && hit.collider.GetComponentInParent<PlayerCharacter>() == shooter) continue;
            selected = hit;
            return true;
        }
        return false;
    }

    private static void PlayGunshot()
    {
        var volume = Mathf.Clamp01(GunshotVolume.Value);
        if (volume <= 0f) return;

        // Wobbly Life mixes sound through FMOD, so use a native one-shot that is guaranteed to
        // reach the game's active audio buses. Keep the generated crack underneath as fallback
        // for installations where Unity audio is also routed to the listener.
        try
        {
            var instance = RuntimeManager.CreateInstance(NativeGunshotEvent);
            instance.set3DAttributes(RuntimeUtils.To3DAttributes(muzzle ? muzzle.position : Camera.main.transform.position));
            instance.setVolume(volume);
            instance.start();
            instance.release();
        }
        catch (Exception exception)
        {
            Plugin.Log?.LogWarning($"Native gunshot event failed: {exception.Message}");
        }

        if (audioSource && shotSound)
        {
            audioSource.pitch = UnityEngine.Random.Range(0.94f, 1.06f);
            audioSource.PlayOneShot(shotSound, volume);
        }
    }

    private static AudioClip CreateGunshotClip()
    {
        const int sampleRate = 44100;
        const float duration = 0.16f;
        var sampleCount = Mathf.CeilToInt(sampleRate * duration);
        var samples = new float[sampleCount];
        var random = new System.Random(8031);
        for (var i = 0; i < sampleCount; i++)
        {
            var time = i / (float)sampleRate;
            var envelope = Mathf.Exp(-time * 34f);
            var crack = (float)(random.NextDouble() * 2.0 - 1.0) * envelope;
            var boom = Mathf.Sin(2f * Mathf.PI * 92f * time) * Mathf.Exp(-time * 18f);
            samples[i] = Mathf.Clamp(crack * 0.72f + boom * 0.52f, -1f, 1f);
        }

        var clip = AudioClip.Create("ExtraMods Heavy Gunshot", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static void CreateShotEffects(Vector3 start, Vector3 end)
    {
        var direction = end - start;
        var distance = direction.magnitude;
        if (distance > 0.01f)
        {
            var tracer = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            tracer.name = "Heavy Gun Tracer";
            var tracerCollider = tracer.GetComponent<Collider>();
            if (tracerCollider) tracerCollider.enabled = false;
            Object.Destroy(tracerCollider);
            tracer.transform.position = start + direction * 0.5f;
            tracer.transform.rotation = Quaternion.FromToRotation(Vector3.up, direction.normalized);
            tracer.transform.localScale = new Vector3(0.012f, distance * 0.5f, 0.012f);
            var renderer = tracer.GetComponent<Renderer>();
            if (renderer) renderer.material.color = new Color(1f, 0.72f, 0.12f);
            Object.Destroy(tracer, 0.045f);
        }

        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "Heavy Gun Muzzle Flash";
        var flashCollider = flash.GetComponent<Collider>();
        if (flashCollider) flashCollider.enabled = false;
        Object.Destroy(flashCollider);
        flash.transform.position = start;
        flash.transform.localScale = Vector3.one * 0.18f;
        var flashRenderer = flash.GetComponent<Renderer>();
        if (flashRenderer) flashRenderer.material.color = new Color(1f, 0.55f, 0.08f);
        Object.Destroy(flash, 0.04f);
    }

    private static void CreateImpactSpark(Vector3 point, Vector3 normal)
    {
        // One non-physics flash replaces four temporary rigidbodies per bullet. At full auto this
        // avoids constant physics registration and garbage collection without losing hit feedback.
        var spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        spark.name = "Heavy Gun Impact Flash";
        var collider = spark.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        Object.Destroy(collider);
        spark.transform.position = point + normal * 0.025f;
        spark.transform.localScale = Vector3.one * 0.075f;
        var renderer = spark.GetComponent<Renderer>();
        if (renderer) renderer.material.color = new Color(1f, 0.5f, 0.05f);
        Object.Destroy(spark, 0.065f);
    }

    private static void UpdateAmmoText()
    {
        AmmoStatus.Value = reloading ? $"Ammo: {rounds} / ∞ — RELOADING" : $"Ammo: {rounds} / ∞";
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;

        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var previous = GUI.color;
        GUI.color = reloading ? new Color(1f, 0.5f, 0.12f, 0.95f) : new Color(1f, 0.95f, 0.72f, 0.98f);
        DrawRect(x - CrosshairGap - CrosshairLength, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x + CrosshairGap, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x - CrosshairThickness * 0.5f, y - CrosshairGap - CrosshairLength, CrosshairThickness, CrosshairLength);
        DrawRect(x - CrosshairThickness * 0.5f, y + CrosshairGap, CrosshairThickness, CrosshairLength);
        DrawRect(x - 2f, y - 2f, 4f, 4f);
        GUI.Label(new Rect(x + 16f, y + 11f, 120f, 24f), reloading ? "RELOADING" : $"{rounds} / ∞");
        GUI.color = previous;
    }

    private static void DrawRect(float x, float y, float width, float height)
        => GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
}

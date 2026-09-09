using System.Collections;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>A host-side lava projectile weapon with player, vehicle, and persistent burn effects.</summary>
public sealed class LavaGunMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Lava gun is unequipped.");
    private static float nextFireTime;

    public override string Name => "Lava Gun";
    public override string Description =>
        "Shoot molten lava that instantly respawns Wobblies, destroys cars without rusty wrecks, and sets hit objects on fire.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Label = "Instant player respawn")]
    public static Ref<bool> InstantRespawn = new(true);

    [ModSetting(Order = 20, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();

    [ModSetting(Order = 30, Min = 0.05f, Max = 1f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.16f);

    [ModSetting(Order = 40, Min = 20f, Max = 250f, Label = "Lava velocity")]
    public static Ref<float> ProjectileVelocity = new(85f);

    [ModSetting(Order = 50, Min = 0.12f, Max = 0.8f, Label = "Lava ball size")]
    public static Ref<float> ProjectileSize = new(0.3f);

    [ModSetting(Order = 60, Min = 2f, Max = 30f, Label = "Fire duration")]
    public static Ref<float> BurnDuration = new(12f);

    [ModSetting(Order = 70, Min = 1f, Max = 12f, Label = "Explosion radius")]
    public static Ref<float> ExplosionRadius = new(5f);

    [ModSetting(Order = 80, Min = -300f, Max = 300f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new();

    [ModSetting(Order = 90, Min = -250f, Max = 250f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new();

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("LavaGunHelp",
                "Equip, close F2, aim with the orange crosshair, and left-click. Lava visibly burns every surface it hits. " +
                "Wobblies are destroyed and optionally respawn immediately. Cars explode and are removed directly, so no rusty wreck remains. " +
                "Destructive effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("LavaGunActions",
                ActionMenu(new Button("Equip lava gun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip lava gun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("LavaGunStatus", "").WithText(Status));
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
        EquippedState.Value = true;
        Status.Value = "Lava gun equipped. Close F2, aim, and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Lava gun unequipped.";
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
            Status.Value = "Only the offline player or lobby host can fire the lava gun.";
            return;
        }

        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !character)
        {
            Status.Value = "Enter a save before firing the lava gun.";
            return;
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.05f, FireInterval.Value);
        var screenPoint = new Vector3(Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value, 0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        SpawnProjectile(ray.origin + ray.direction * 1.5f, ray.direction, controller);
        Status.Value = "Lava fired.";
    }

    private static void SpawnProjectile(Vector3 position, Vector3 direction, PlayerController shooter)
    {
        var projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        projectile.name = "ExtraMods Lava Projectile";
        projectile.transform.position = position;
        projectile.transform.localScale = Vector3.one * Mathf.Clamp(ProjectileSize.Value, 0.12f, 0.8f);

        var renderer = projectile.GetComponent<Renderer>();
        if (renderer)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            renderer.material = new Material(shader) { color = new Color(1f, 0.12f, 0.005f, 1f) };
        }

        var light = projectile.AddComponent<Light>();
        light.color = new Color(1f, 0.18f, 0.01f);
        light.range = 4f;
        light.intensity = 2.4f;

        var body = projectile.AddComponent<Rigidbody>();
        body.mass = 0.18f;
        body.useGravity = true;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.velocity = direction.normalized * Mathf.Max(20f, ProjectileVelocity.Value);

        var impact = projectile.AddComponent<LavaProjectile>();
        impact.Initialize(shooter);
        IgnoreShooter(projectile, shooter);
        Object.Destroy(projectile, 15f);
    }

    private static void IgnoreShooter(GameObject projectile, PlayerController shooter)
    {
        var character = shooter ? shooter.GetPlayerCharacter() : null;
        var projectileCollider = projectile.GetComponent<Collider>();
        if (!character || !projectileCollider) return;
        foreach (var collider in character.GetComponentsInChildren<Collider>(true))
            if (collider) Physics.IgnoreCollision(projectileCollider, collider, true);
    }

    internal static void HandleImpact(Collision collision, PlayerController shooter)
    {
        if (!collision.collider) return;
        var point = collision.contactCount > 0 ? collision.GetContact(0).point : collision.collider.bounds.center;
        var normal = collision.contactCount > 0 ? collision.GetContact(0).normal : Vector3.up;
        var character = collision.collider.GetComponentInParent<PlayerCharacter>();
        var vehicle = collision.collider.GetComponentInParent<PlayerVehicle>();

        CreateBurnEffect(collision.collider.transform, point, normal);

        if (character)
        {
            if (character.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("lava hit");

            var targetController = character.GetPlayerController();
            if (targetController && targetController != shooter)
            {
                targetController.ServerDestoryPlayerCharacter(true, false);
                if (InstantRespawn.Value) Plugin.RunCoroutine(RespawnImmediately(targetController));
                Status.Value = InstantRespawn.Value
                    ? "Wobbly burned: instant respawn requested."
                    : "Wobbly burned by lava.";
            }
            return;
        }

        if (vehicle)
        {
            CreateExplosion(point);
            vehicle.DestroyGameObject();
            Status.Value = "Vehicle destroyed instantly; rusty wreck skipped.";
            return;
        }

        var destructable = collision.collider.GetComponentInParent<DestructableDynamicObject>();
        if (destructable)
        {
            destructable.Break();
            Status.Value = "Breakable object ignited and shattered.";
            return;
        }

        Status.Value = $"{CleanName(collision.collider.name)} is on fire.";
    }

    private static IEnumerator RespawnImmediately(PlayerController controller)
    {
        yield return null;
        if (controller) controller.ClientRequestRespawn(-1);
    }

    private static void CreateBurnEffect(Transform target, Vector3 point, Vector3 normal)
    {
        var root = new GameObject("ExtraMods Lava Fire");
        root.transform.position = point + normal.normalized * 0.05f;
        if (target) root.transform.SetParent(target, true);
        root.AddComponent<LavaBurnEffect>().Initialize(Mathf.Clamp(BurnDuration.Value, 2f, 30f));
    }

    private static void CreateExplosion(Vector3 point)
    {
        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "Lava Vehicle Explosion";
        flash.transform.position = point;
        flash.transform.localScale = Vector3.one * 0.35f;
        var collider = flash.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        var material = new Material(shader) { color = new Color(1f, 0.15f, 0.005f, 0.95f) };
        flash.GetComponent<Renderer>().material = material;
        Plugin.RunCoroutine(AnimateExplosion(flash, material));

        var radius = Mathf.Max(1f, ExplosionRadius.Value);
        foreach (var nearby in Physics.OverlapSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            var body = nearby.attachedRigidbody;
            if (body && !body.isKinematic)
                body.AddExplosionForce(1000f, point, radius, 2.5f, ForceMode.Impulse);
        }
    }

    private static IEnumerator AnimateExplosion(GameObject flash, Material material)
    {
        var elapsed = 0f;
        while (flash && elapsed < 0.35f)
        {
            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / 0.35f);
            flash.transform.localScale = Vector3.one * Mathf.Lerp(0.35f, ExplosionRadius.Value * 1.5f, progress);
            material.color = new Color(1f, Mathf.Lerp(0.4f, 0.04f, progress), 0.005f, 1f - progress);
            yield return null;
        }
        if (flash) Object.Destroy(flash);
        if (material) Object.Destroy(material);
    }

    private static string CleanName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Object" : value.Replace("(Clone)", string.Empty).Trim();

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var old = GUI.color;
        GUI.color = new Color(1f, 0.2f, 0.01f, 1f);
        GUI.DrawTexture(new Rect(x - 17f, y - 2f, 11f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 6f, y - 2f, 11f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 17f, 4f, 11f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 6f, 4f, 11f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class LavaProjectile : MonoBehaviour
{
    private PlayerController shooter;
    private bool hit;

    internal void Initialize(PlayerController owner) => shooter = owner;

    private void OnCollisionEnter(Collision collision)
    {
        if (hit) return;
        hit = true;
        LavaGunMod.HandleImpact(collision, shooter);
        Destroy(gameObject);
    }
}

internal sealed class LavaBurnEffect : MonoBehaviour
{
    private readonly Transform[] flames = new Transform[7];
    private readonly Vector3[] bases = new Vector3[7];
    private readonly Vector3[] baseScales = new Vector3[7];
    private float expiresAt;

    internal void Initialize(float duration)
    {
        expiresAt = Time.time + duration;
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        for (var i = 0; i < flames.Length; i++)
        {
            var flame = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flame.name = "Lava Flame";
            Destroy(flame.GetComponent<Collider>());
            flame.transform.SetParent(transform, false);
            bases[i] = new Vector3(Random.Range(-0.38f, 0.38f), Random.Range(0.05f, 0.35f),
                Random.Range(-0.38f, 0.38f));
            flame.transform.localPosition = bases[i];
            baseScales[i] = new Vector3(Random.Range(0.12f, 0.25f), Random.Range(0.35f, 0.75f),
                Random.Range(0.12f, 0.25f));
            flame.transform.localScale = baseScales[i];
            var renderer = flame.GetComponent<Renderer>();
            if (renderer)
                renderer.material = new Material(shader)
                {
                    color = i % 3 == 0 ? new Color(1f, 0.65f, 0.02f, 0.92f) : new Color(1f, 0.08f, 0.005f, 0.9f)
                };
            flames[i] = flame.transform;
        }

        var glow = gameObject.AddComponent<Light>();
        glow.color = new Color(1f, 0.18f, 0.01f);
        glow.range = 5f;
        glow.intensity = 2f;
    }

    private void Update()
    {
        if (Time.time >= expiresAt)
        {
            Destroy(gameObject);
            return;
        }

        for (var i = 0; i < flames.Length; i++)
        {
            if (!flames[i]) continue;
            var wave = Mathf.Sin(Time.time * (8f + i * 0.4f) + i * 1.7f);
            flames[i].localPosition = bases[i] + Vector3.up * (wave * 0.09f);
            var scale = 0.88f + (wave + 1f) * 0.1f;
            flames[i].localScale = new Vector3(baseScales[i].x * scale,
                baseScales[i].y * (1.05f - wave * 0.08f), baseScales[i].z * scale);
        }
    }
}

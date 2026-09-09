using System.Collections;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Colored projectile weapon with optional host-side target assistance and hit effects.</summary>
public sealed class PaintballGunMod : BaseMod
{
    private const float CrosshairGap = 6f;
    private const float CrosshairLength = 9f;
    private const float CrosshairThickness = 2f;

    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Paintball gun is unequipped.");
    private static float nextFireTime;

    public override string Name => "Paintball Gun";

    public override string Description =>
        "Fire custom-colored paintballs with optional player aim assist, forced respawns, vehicle destruction, and rapid fire.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 0f, Max = 1f, Label = "Paint red")]
    public static Ref<float> PaintRed = new(1f);

    [ModSetting(Order = 20, Min = 0f, Max = 1f, Label = "Paint green")]
    public static Ref<float> PaintGreen = new(0.1f);

    [ModSetting(Order = 30, Min = 0f, Max = 1f, Label = "Paint blue")]
    public static Ref<float> PaintBlue = new(0.65f);

    [ModSetting(Order = 40, Label = "Player aimbot",
        Description = "Redirects shots toward the closest visible player inside the aim-assist angle.")]
    public static Ref<bool> Aimbot = new();

    [ModSetting(Order = 50, Min = 2f, Max = 90f, Label = "Aimbot angle")]
    public static Ref<float> AimbotAngle = new(25f);

    [ModSetting(Order = 60, Min = 10f, Max = 500f, Label = "Aimbot range")]
    public static Ref<float> AimbotRange = new(150f);

    [ModSetting(Order = 70, Label = "Force hit players to respawn")]
    public static Ref<bool> RespawnPlayers = new(true);

    [ModSetting(Order = 80, Label = "Explode hit vehicles")]
    public static Ref<bool> ExplodeVehicles = new(true);

    [ModSetting(Order = 90, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();

    [ModSetting(Order = 100, Min = 0.04f, Max = 1f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.12f);

    [ModSetting(Order = 110, Min = 10f, Max = 250f, Label = "Paintball velocity")]
    public static Ref<float> ProjectileVelocity = new(90f);

    [ModSetting(Order = 120, Min = 0.08f, Max = 0.6f, Label = "Paintball size")]
    public static Ref<float> ProjectileSize = new(0.22f);

    [ModSetting(Order = 130, Label = "Projectile gravity")]
    public static Ref<bool> ProjectileGravity = new(true);

    [ModSetting(Order = 140, Min = -400f, Max = 400f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(100f);

    [ModSetting(Order = 150, Min = -250f, Max = 250f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(35f);

    [ModSetting(Order = 160, Min = 0.25f, Max = 2f, Label = "Splatter size")]
    public static Ref<float> SplatterSize = new(1f);

    [ModSetting(Order = 170, Min = 2f, Max = 60f, Label = "Paint mark lifetime")]
    public static Ref<float> PaintLifetime = new(20f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("PaintballGunHelp",
                "Set the RGB paint color, equip, close F2, and left-click to shoot. Aimbot selects another visible " +
                "player near the crosshair. Player respawn and vehicle explosion effects require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("PaintballGunActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("PaintballGunStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void Equip()
    {
        WindCannonMod.Unequip();
        PropSpawnerGunMod.Unequip();
        VehicleAircraftSpawnerMod.Unequip();
        RocketLauncherMod.Unequip();
        MinecraftBuildingMod.Unequip();
        HeavyAutomaticGunMod.Unequip();
        GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Paintball gun equipped. Close F2, aim, and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Paintball gun unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can fire effect-enabled paintballs.";
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before firing.";
            return;
        }

        var shooter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()
            : null;
        var screenPoint = new Vector3(
            Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value,
            0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        var spawnPosition = ray.origin + ray.direction * 1.4f;
        var direction = Aimbot.Value
            ? FindAimbotDirection(spawnPosition, ray.direction, shooter)
            : ray.direction;

        nextFireTime = Time.unscaledTime + Mathf.Max(0.04f, FireInterval.Value);
        SpawnProjectile(spawnPosition, direction, shooter);
        Status.Value = Aimbot.Value ? "Paintball fired with aim assist." : "Paintball fired.";
    }

    public override void Update()
    {
        if (!EquippedState.Value || Cursor.visible) return;

        if (RapidFire.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0))
            Fire();
    }

    private static void SpawnProjectile(Vector3 position, Vector3 direction, PlayerController shooter)
    {
        var projectile = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        projectile.name = "ExtraMods Paintball";
        projectile.transform.position = position;
        projectile.transform.localScale = Vector3.one * Mathf.Clamp(ProjectileSize.Value, 0.08f, 0.6f);

        var color = CurrentColor();
        var renderer = projectile.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;

        var body = projectile.AddComponent<Rigidbody>();
        body.mass = 0.08f;
        body.useGravity = ProjectileGravity.Value;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.velocity = direction.normalized * Mathf.Max(1f, ProjectileVelocity.Value);

        var hit = projectile.AddComponent<PaintballProjectile>();
        hit.Initialize(shooter, color);
        IgnoreShooter(projectile, shooter);
        Object.Destroy(projectile, 12f);
    }

    private static Vector3 FindAimbotDirection(Vector3 origin, Vector3 fallback, PlayerController shooter)
    {
        if (!GameInstance.InstanceExists) return fallback;

        PlayerCharacter best = null;
        var bestAngle = Mathf.Clamp(AimbotAngle.Value, 1f, 90f);
        var maxRange = Mathf.Max(1f, AimbotRange.Value);

        foreach (var controller in GameInstance.Instance.GetPlayerControllers())
        {
            if (!controller || controller == shooter) continue;
            var character = controller.GetPlayerCharacter();
            if (!character) continue;

            var targetPoint = character.transform.position + Vector3.up * 1.1f;
            var offset = targetPoint - origin;
            if (offset.sqrMagnitude > maxRange * maxRange) continue;

            var angle = Vector3.Angle(fallback, offset);
            if (angle > bestAngle || IsAimbotBlocked(origin, offset, character)) continue;

            bestAngle = angle;
            best = character;
        }

        return best
            ? (best.transform.position + Vector3.up * 1.1f - origin).normalized
            : fallback;
    }

    private static bool IsAimbotBlocked(Vector3 origin, Vector3 offset, PlayerCharacter target)
    {
        if (!Physics.Raycast(origin, offset.normalized, out var hit, offset.magnitude, ~0, QueryTriggerInteraction.Ignore))
            return false;

        return hit.collider.GetComponentInParent<PlayerCharacter>() != target;
    }

    private static void IgnoreShooter(GameObject projectile, PlayerController shooter)
    {
        var character = shooter ? shooter.GetPlayerCharacter() : null;
        if (!character) return;

        var projectileCollider = projectile.GetComponent<Collider>();
        foreach (var collider in character.GetComponentsInChildren<Collider>(true))
        {
            if (projectileCollider && collider)
                Physics.IgnoreCollision(projectileCollider, collider, true);
        }
    }

    internal static void HandleImpact(Collision collision, PlayerController shooter, Color color)
    {
        if (collision.collider.GetComponentInParent<PlayerNPCController>())
            PoliceChaseMod.ReportNpcHarassment("paintball hit");

        if (collision.contactCount > 0)
        {
            var contact = collision.GetContact(0);
            CreatePaintMark(contact.point, contact.normal, collision.collider.transform, color);
        }

        var character = collision.collider.GetComponentInParent<PlayerCharacter>();
        var targetController = character ? character.GetPlayerController() : null;
        if (RespawnPlayers.Value && targetController && targetController != shooter)
        {
            targetController.ServerDestoryPlayerCharacter(true, false);
            Plugin.RunCoroutine(RespawnAfterHit(targetController));
            Status.Value = $"Hit {targetController.GetPlayerName()}: respawn requested.";
            return;
        }

        var vehicle = collision.collider.GetComponentInParent<PlayerVehicle>();
        var destructable = vehicle ? vehicle.GetComponent<PlayerVehicleDestructable>() : null;
        if (ExplodeVehicles.Value && destructable)
        {
            destructable.ServerDamage(short.MaxValue, false, true);
            Status.Value = "Vehicle hit: maximum destruction damage applied.";
        }
    }

    private static IEnumerator RespawnAfterHit(PlayerController controller)
    {
        yield return new WaitForSeconds(0.45f);
        if (controller) controller.ClientRequestRespawn(-1);
    }

    private static void CreatePaintMark(Vector3 point, Vector3 normal, Transform parent, Color color)
    {
        normal.Normalize();
        var size = Mathf.Clamp(SplatterSize.Value, 0.25f, 2f);
        var lifetime = Mathf.Clamp(PaintLifetime.Value, 2f, 60f);
        var root = new GameObject("ExtraMods Paint Splatter");
        root.transform.position = point;
        root.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal);
        if (parent) root.transform.SetParent(parent, true);

        // Overlapping, slightly offset shapes make the center asymmetrical instead of a perfect dot.
        CreateSurfaceBlob(root.transform, Vector3.zero, 0f,
            new Vector3(0.42f, 0.012f, 0.34f) * size, color);
        CreateSurfaceBlob(root.transform, new Vector3(0.09f, 0.002f, -0.05f) * size, 24f,
            new Vector3(0.31f, 0.011f, 0.24f) * size, color);
        CreateSurfaceBlob(root.transform, new Vector3(-0.1f, 0.003f, 0.06f) * size, -18f,
            new Vector3(0.25f, 0.01f, 0.3f) * size, color);

        // Radial streaks and satellite drops form a different-looking splatter on every hit.
        var streakCount = Random.Range(7, 12);
        for (var i = 0; i < streakCount; i++)
        {
            var angle = Random.Range(0f, 360f);
            var radians = angle * Mathf.Deg2Rad;
            var radial = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
            var distance = Random.Range(0.24f, 0.62f) * size;
            var width = Random.Range(0.045f, 0.1f) * size;
            var length = Random.Range(0.14f, 0.38f) * size;

            CreateSurfaceBlob(root.transform, radial * distance + Vector3.up * 0.004f, angle,
                new Vector3(width, 0.008f * size, length), color);

            if (Random.value > 0.35f)
            {
                var dropDistance = distance + Random.Range(0.15f, 0.42f) * size;
                var dropSize = Random.Range(0.035f, 0.085f) * size;
                CreateSurfaceBlob(root.transform, radial * dropDistance + Vector3.up * 0.006f, angle,
                    new Vector3(dropSize, 0.007f * size, dropSize), color);
            }
        }

        CreateFlyingDroplets(point, normal, color, size);
        Object.Destroy(root, lifetime);
    }

    private static void CreateSurfaceBlob(
        Transform root,
        Vector3 localPosition,
        float angle,
        Vector3 localScale,
        Color color)
    {
        var blob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        blob.name = "Paint Blob";
        Object.Destroy(blob.GetComponent<Collider>());
        blob.transform.SetParent(root, false);
        blob.transform.localPosition = localPosition;
        blob.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
        blob.transform.localScale = localScale;

        var renderer = blob.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    private static void CreateFlyingDroplets(Vector3 point, Vector3 normal, Color color, float size)
    {
        for (var i = 0; i < 7; i++)
        {
            var droplet = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            droplet.name = "Flying Paint Droplet";
            Object.Destroy(droplet.GetComponent<Collider>());
            droplet.transform.position = point + normal * 0.04f;
            droplet.transform.localScale = Vector3.one * Random.Range(0.035f, 0.075f) * size;

            var renderer = droplet.GetComponent<Renderer>();
            if (renderer) renderer.material.color = color;

            var tangent = Vector3.Cross(normal, Random.onUnitSphere).normalized;
            var body = droplet.AddComponent<Rigidbody>();
            body.mass = 0.01f;
            body.velocity = normal * Random.Range(1.2f, 3.5f) + tangent * Random.Range(-2.2f, 2.2f);
            Object.Destroy(droplet, Random.Range(0.7f, 1.4f));
        }
    }

    private static Color CurrentColor() => new(
        Mathf.Clamp01(PaintRed.Value),
        Mathf.Clamp01(PaintGreen.Value),
        Mathf.Clamp01(PaintBlue.Value),
        1f);

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;

        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var previous = GUI.color;
        GUI.color = CurrentColor();

        DrawRect(x - CrosshairGap - CrosshairLength, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x + CrosshairGap, y - CrosshairThickness * 0.5f, CrosshairLength, CrosshairThickness);
        DrawRect(x - CrosshairThickness * 0.5f, y - CrosshairGap - CrosshairLength, CrosshairThickness, CrosshairLength);
        DrawRect(x - CrosshairThickness * 0.5f, y + CrosshairGap, CrosshairThickness, CrosshairLength);
        DrawRect(x - 2f, y - 2f, 4f, 4f);

        GUI.color = previous;
    }

    private static void DrawRect(float x, float y, float width, float height)
        => GUI.DrawTexture(new Rect(x, y, width, height), Texture2D.whiteTexture);
}

internal sealed class PaintballProjectile : MonoBehaviour
{
    private PlayerController shooter;
    private Color color;
    private bool hit;

    internal void Initialize(PlayerController owner, Color paintColor)
    {
        shooter = owner;
        color = paintColor;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (hit) return;
        hit = true;
        PaintballGunMod.HandleImpact(collision, shooter, color);
        Destroy(gameObject);
    }
}

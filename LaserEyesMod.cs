using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using ModWobblyLife;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class LaserEyesMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Laser eyes are unequipped.");
    private static readonly List<LineRenderer> Beams = new();
    private static Material beamMaterial;
    private static float beamUntil;
    private static float nextFire;

    public override string Name => "Laser Eyes";
    public override string Description =>
        "Fire twin eye lasers with Q to instantly explode vehicles, Wobblies, destructible objects, bombs, and physics props.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    internal static bool IsEquipped => EquippedState.Value;

    [ModSetting(Order = 10, Min = 20f, Max = 1000f, Label = "Laser range")]
    public static Ref<float> Range = new(500f);

    [ModSetting(Order = 20, Min = 0.05f, Max = 2f, Label = "Fire cooldown")]
    public static Ref<float> Cooldown = new(0.2f);

    [ModSetting(Order = 30, Min = 100f, Max = 5000f, Label = "Explosion force")]
    public static Ref<float> ExplosionForce = new(1800f);

    [ModSetting(Order = 40, Min = 1f, Max = 20f, Label = "Explosion radius")]
    public static Ref<float> ExplosionRadius = new(8f);

    [ModSetting(Order = 50, Min = -300f, Max = 300f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(0f);

    [ModSetting(Order = 60, Min = -300f, Max = 300f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(0f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("LaserEyesHelp",
                "Equip, close F2, aim with the red crosshair, and press Q. Twin beams fire from your Wobbly's head " +
                "and instantly trigger native destruction on cars, Wobblies, bombs, breakable scenery, and physics props. " +
                "Terrain and game-manager roots are protected. Destructive effects require the offline player or lobby host."),
            base.BuildPanel(id),
            new HStack("LaserEyesActions",
                ActionMenu(new Button("Equip laser eyes", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip laser eyes", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire now", Fire), nameof(Fire))
            ).WithContentWidth(),
            new TextWrapped("LaserEyesStatus", "").WithText(Status));
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
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Laser eyes equipped. Close F2, aim, and press Q to explode the target.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        HideBeams();
        Status.Value = "Laser eyes unequipped.";
    }

    public override void Update()
    {
        if (Beams.Count > 0 && Time.unscaledTime >= beamUntil) HideBeams();
        if (!EquippedState.Value || Cursor.visible) return;
        if (!GameInstance.InstanceExists || !GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter())
        {
            Unequip();
            return;
        }
        if (Input.GetKeyDown(KeyCode.Q)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFire) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can fire destructive laser eyes.";
            return;
        }

        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !character)
        {
            Status.Value = "Enter a save before firing laser eyes.";
            return;
        }

        nextFire = Time.unscaledTime + Mathf.Max(0.05f, Cooldown.Value);
        var screenPoint = new Vector3(Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value, 0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        var maxRange = Mathf.Max(20f, Range.Value);
        var hasHit = TryFindTarget(ray, maxRange, character, out var hit);
        var end = hasHit ? hit.point : ray.origin + ray.direction * maxRange;
        ShowTwinBeams(character, camera, end);

        if (!hasHit)
        {
            Status.Value = "Laser eyes fired, but did not hit anything.";
            return;
        }

        ExplodeTarget(hit, controller, character);
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

    private static void ExplodeTarget(RaycastHit hit, PlayerController shooter, PlayerCharacter shooterCharacter)
    {
        var collider = hit.collider;
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        if (vehicle)
        {
            CreateExplosion(hit.point);
            var destructable = vehicle.GetComponent<PlayerVehicleDestructable>();
            if (destructable) destructable.ServerDamage(short.MaxValue, false, true);
            Plugin.RunCoroutine(RemoveVehicle(vehicle));
            Status.Value = $"Laser eyes instantly exploded vehicle {CleanName(vehicle.name)}.";
            return;
        }

        var character = collider.GetComponentInParent<PlayerCharacter>();
        if (character && character != shooterCharacter)
        {
            CreateExplosion(hit.point);
            LaunchWobbly(character, hit.point);
            var targetController = character.GetPlayerController();
            if (targetController && targetController != shooter)
            {
                targetController.ServerDestoryPlayerCharacter(true, false);
                Plugin.RunCoroutine(RespawnPlayer(targetController));
            }
            Status.Value = $"Laser eyes exploded Wobbly {CleanName(character.name)}.";
            return;
        }

        var bomb = collider.GetComponentInParent<Bomb>();
        if (bomb)
        {
            bomb.Explode();
            CreateExplosion(hit.point);
            Status.Value = "Laser eyes instantly detonated the bomb.";
            return;
        }

        var dynamicObject = collider.GetComponentInParent<DynamicObject>();
        var destructableObject = collider.GetComponentInParent<DestructableDynamicObject>();
        var nativeExplosionComponent = collider.GetComponentsInParent<MonoBehaviour>(true)
            .FirstOrDefault(component => component is IModExplode);
        var nativeDestroyComponent = collider.GetComponentsInParent<MonoBehaviour>(true)
            .FirstOrDefault(component => component is IDestoryGameObject);
        var target = dynamicObject ? dynamicObject.gameObject :
            destructableObject ? destructableObject.gameObject :
            nativeExplosionComponent ? nativeExplosionComponent.gameObject :
            nativeDestroyComponent ? nativeDestroyComponent.gameObject :
            collider.attachedRigidbody ? collider.attachedRigidbody.gameObject : collider.gameObject;

        CreateExplosion(hit.point);
        var nativeExplosions = ApplyExplosionInterfaces(target, hit.point);

        if (destructableObject)
        {
            destructableObject.Break();
            Status.Value = $"Laser eyes shattered {CleanName(target.name)}.";
            return;
        }

        if (dynamicObject)
        {
            dynamicObject.GetRigidbody()?.AddExplosionForce(Mathf.Max(100f, ExplosionForce.Value), hit.point,
                Mathf.Max(1f, ExplosionRadius.Value), 3f, ForceMode.Impulse);
            dynamicObject.DestroyGameObject();
            Status.Value = $"Laser eyes exploded prop {CleanName(target.name)}.";
            return;
        }

        if (nativeDestroyComponent is IDestoryGameObject destroyable)
        {
            destroyable.DestroyGameObject();
            Status.Value = $"Laser eyes destroyed {CleanName(target.name)}.";
            return;
        }

        if (nativeExplosions > 0)
        {
            Status.Value = $"Laser eyes triggered {nativeExplosions} native explosion effect(s) on {CleanName(target.name)}.";
            return;
        }

        var body = collider.attachedRigidbody;
        if (body && !body.isKinematic)
        {
            body.AddExplosionForce(Mathf.Max(100f, ExplosionForce.Value), hit.point,
                Mathf.Max(1f, ExplosionRadius.Value), 3f, ForceMode.Impulse);
            UnityEngine.Object.Destroy(body.gameObject, 0.12f);
            Status.Value = $"Laser eyes exploded physics object {CleanName(body.name)}.";
            return;
        }

        Status.Value = $"Laser impact blasted {CleanName(collider.name)}; protected map geometry was not deleted.";
    }

    private static int ApplyExplosionInterfaces(GameObject target, Vector3 point)
    {
        if (!target) return 0;
        var data = new ExplodeData
        {
            position = point,
            force = Mathf.Max(100f, ExplosionForce.Value),
            radius = Mathf.Max(1f, ExplosionRadius.Value),
            explodeGameObject = target
        };
        var invoked = new HashSet<IModExplode>();
        foreach (var component in target.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (component is IModExplode explode && invoked.Add(explode))
                explode.Explode(data);
        }
        return invoked.Count;
    }

    private static void LaunchWobbly(PlayerCharacter character, Vector3 point)
    {
        var ragdoll = character.GetRagdollController();
        var body = character.GetComponentInChildren<PlayerBody>(true);
        if (!ragdoll || !body) return;
        ragdoll.Ragdoll();
        var away = Vector3.ProjectOnPlane(body.transform.position - point, Vector3.up).normalized;
        body.SetRagdollVelocity(Vector3.up * 55f + away * 12f);
    }

    private static IEnumerator RemoveVehicle(PlayerVehicle vehicle)
    {
        yield return new WaitForSeconds(0.15f);
        if (vehicle) vehicle.DestroyGameObject();
    }

    private static IEnumerator RespawnPlayer(PlayerController controller)
    {
        yield return new WaitForSeconds(0.45f);
        if (controller) controller.ClientRequestRespawn(-1);
    }

    private static void ShowTwinBeams(PlayerCharacter character, Camera camera, Vector3 end)
    {
        EnsureBeams();
        var head = character.GetComponentsInChildren<Transform>(true)
            .FirstOrDefault(transform => transform.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0);
        var center = head ? head.position + camera.transform.forward * 0.13f : camera.transform.position;
        var separation = camera.transform.right * 0.1f;
        SetBeam(Beams[0], center - separation, end);
        SetBeam(Beams[1], center + separation, end);
        beamUntil = Time.unscaledTime + 0.14f;
    }

    private static void EnsureBeams()
    {
        if (Beams.Count == 2 && Beams.All(beam => beam)) return;
        Beams.Clear();
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader) beamMaterial = new Material(shader) { color = new Color(1f, 0.015f, 0.01f, 1f) };
        for (var i = 0; i < 2; i++)
        {
            var root = new GameObject($"ExtraMods Laser Eye Beam {i + 1}");
            var beam = root.AddComponent<LineRenderer>();
            beam.positionCount = 2;
            beam.useWorldSpace = true;
            beam.startWidth = 0.065f;
            beam.endWidth = 0.018f;
            beam.startColor = Color.white;
            beam.endColor = new Color(1f, 0.01f, 0.005f, 1f);
            if (beamMaterial) beam.sharedMaterial = beamMaterial;
            Beams.Add(beam);
        }
    }

    private static void SetBeam(LineRenderer beam, Vector3 start, Vector3 end)
    {
        beam.SetPosition(0, start);
        beam.SetPosition(1, end);
        beam.enabled = true;
    }

    private static void HideBeams()
    {
        foreach (var beam in Beams)
            if (beam) beam.enabled = false;
    }

    private static void CreateExplosion(Vector3 point)
    {
        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "Laser Eyes Explosion";
        flash.transform.position = point;
        flash.transform.localScale = Vector3.one * 0.25f;
        var collider = flash.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        var material = new Material(Shader.Find("Sprites/Default"));
        material.color = new Color(1f, 0.04f, 0.01f, 0.95f);
        flash.GetComponent<Renderer>().material = material;
        Plugin.RunCoroutine(AnimateExplosion(flash, material));

        foreach (var nearby in Physics.OverlapSphere(point, Mathf.Max(1f, ExplosionRadius.Value), ~0,
                     QueryTriggerInteraction.Ignore))
        {
            var body = nearby.attachedRigidbody;
            if (!body || body.isKinematic) continue;
            body.AddExplosionForce(Mathf.Max(100f, ExplosionForce.Value), point,
                Mathf.Max(1f, ExplosionRadius.Value), 3f, ForceMode.Impulse);
        }
    }

    private static IEnumerator AnimateExplosion(GameObject flash, Material material)
    {
        var elapsed = 0f;
        while (flash && elapsed < 0.4f)
        {
            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / 0.4f);
            flash.transform.localScale = Vector3.one * Mathf.Lerp(0.25f, ExplosionRadius.Value * 1.6f, progress);
            material.color = new Color(1f, Mathf.Lerp(0.25f, 0.01f, progress), 0.005f, 1f - progress);
            yield return null;
        }
        if (flash) UnityEngine.Object.Destroy(flash);
        if (material) UnityEngine.Object.Destroy(material);
    }

    private static string CleanName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "target" : value.Replace("(Clone)", string.Empty).Trim();

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var old = GUI.color;
        GUI.color = new Color(1f, 0.03f, 0.01f, 0.98f);
        GUI.DrawTexture(new Rect(x - 16f, y - 1.5f, 11f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 5f, y - 1.5f, 11f, 3f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1.5f, y - 16f, 3f, 11f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1.5f, y + 5f, 3f, 11f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

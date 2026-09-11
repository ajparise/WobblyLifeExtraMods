using System;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class ShrinkRayMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<bool> GrowMode = new();
    private static readonly Ref<string> Status = new("Shrink ray is unequipped.");
    private static readonly Dictionary<Transform, Vector3> OriginalScales = new();
    private static readonly Dictionary<Transform, float> ScaleMultipliers = new();
    private static readonly List<Transform> Cleanup = new();
    private static float nextFire;
    private static LineRenderer beam;
    private static Material beamMaterial;
    private static float beamUntil;

    public override string Name => "Shrink Ray";
    public override string Description => "Shrink or grow cars, NPCs, props, buildings, trees, roads, and other world objects hit by the ray.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 0.2f, Max = 0.95f, Label = "Scale per hit")]
    public static Ref<float> ScalePerHit = new(0.7f);

    [ModSetting(Order = 20, Min = 0.01f, Max = 0.5f, Label = "Minimum scale")]
    public static Ref<float> MinimumScale = new(0.05f);

    [ModSetting(Order = 25, Min = 1.05f, Max = 3f, Label = "Growth per hit")]
    public static Ref<float> GrowthPerHit = new(1.35f);

    [ModSetting(Order = 27, Min = 2f, Max = 25f, Label = "Maximum scale")]
    public static Ref<float> MaximumScale = new(10f);

    [ModSetting(Order = 30, Min = 25f, Max = 1000f, Label = "Ray range")]
    public static Ref<float> Range = new(500f);

    [ModSetting(Order = 40, Min = 0.05f, Max = 1f, Label = "Fire cooldown")]
    public static Ref<float> Cooldown = new(0.18f);

    [ModSetting(Order = 50, Min = -300f, Max = 300f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(0f);

    [ModSetting(Order = 60, Min = -300f, Max = 300f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(0f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("ShrinkRayHelp",
                "Equip and close F2. Press Q or the mode button to switch between shrink and grow. Left-click changes the aimed object. Right-click restores the aimed " +
                "object. Whole vehicles, NPCs, props, buildings, trees, and road pieces are targeted when possible. " +
                "All scaling is local to this play session."),
            base.BuildPanel(id),
            new HStack("ShrinkRayActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Toggle shrink / grow mode", ToggleMode), nameof(ToggleMode)),
                ActionMenu(new Button("Restore all sizes", RestoreAll), nameof(RestoreAll))
            ).WithContentWidth(),
            new TextWrapped("ShrinkRayStatus", "").WithText(Status));
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
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        LightningGunMod.Unequip();
        WobblyHeadHomingGunMod.Unequip();
        BananaPeelLauncherMod.Unequip();
        TornadoGunMod.Unequip();
        FireworkMinigunMod.Unequip();
        EquippedState.Value = true;
        Status.Value = $"Shrink ray equipped in {ModeName()} mode. Press Q to toggle; right-click restores.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        HideBeam();
        Status.Value = "Shrink ray unequipped. Previously shrunk objects keep their size until restored.";
    }

    [ModAction(ShowInUI = false)]
    public static void RestoreAll()
    {
        var restored = 0;
        foreach (var saved in OriginalScales)
        {
            if (!saved.Key) continue;
            saved.Key.localScale = saved.Value;
            restored++;
        }
        OriginalScales.Clear();
        ScaleMultipliers.Clear();
        Status.Value = $"Restored {restored} objects to their original sizes.";
    }

    [ModAction(ShowInUI = false)]
    public static void ToggleMode()
    {
        GrowMode.Value = !GrowMode.Value;
        Status.Value = $"Ray switched to {ModeName()} mode. Left-click to {ModeName().ToLowerInvariant()} objects.";
    }

    public override void Update()
    {
        EnforceModifiedScales();
        if (beam && Time.unscaledTime >= beamUntil) beam.enabled = false;
        if (!EquippedState.Value || Cursor.visible) return;
        if (Input.GetKeyDown(KeyCode.Q)) ToggleMode();
        if (Input.GetMouseButton(0)) Fire(false);
        if (Input.GetMouseButtonDown(1)) Fire(true);
    }

    private static void Fire(bool restore)
    {
        if (!restore && Time.unscaledTime < nextFire) return;
        var camera = Camera.main;
        if (!camera) { Status.Value = "No gameplay camera found."; return; }
        if (!TryAim(camera, out var hit, out var target))
        { Status.Value = $"The {ModeName().ToLowerInvariant()} ray did not hit a scalable object."; return; }

        nextFire = Time.unscaledTime + Mathf.Max(0.05f, Cooldown.Value);
        ShowBeam(camera.transform.position + camera.transform.forward * 0.45f, hit.point, restore, GrowMode.Value);

        if (restore)
        {
            if (OriginalScales.TryGetValue(target, out var original))
            {
                target.localScale = original;
                OriginalScales.Remove(target);
                ScaleMultipliers.Remove(target);
                Status.Value = $"Restored {FriendlyName(target)}.";
            }
            else Status.Value = $"{FriendlyName(target)} is already at its original size.";
            return;
        }

        if (!OriginalScales.ContainsKey(target))
        {
            OriginalScales[target] = target.localScale;
            ScaleMultipliers[target] = 1f;
        }
        var multiplier = GrowMode.Value
            ? Mathf.Min(Mathf.Clamp(MaximumScale.Value, 2f, 25f),
                ScaleMultipliers[target] * Mathf.Clamp(GrowthPerHit.Value, 1.05f, 3f))
            : Mathf.Max(Mathf.Clamp(MinimumScale.Value, 0.01f, 0.9f),
                ScaleMultipliers[target] * Mathf.Clamp(ScalePerHit.Value, 0.2f, 0.95f));
        ScaleMultipliers[target] = multiplier;
        target.localScale = Vector3.Scale(OriginalScales[target], Vector3.one * multiplier);
        Status.Value = $"{(GrowMode.Value ? "Grew" : "Shrank")} {FriendlyName(target)} to {multiplier * 100f:0}% size.";
    }

    private static bool TryAim(Camera camera, out RaycastHit selectedHit, out Transform target)
    {
        selectedHit = default;
        target = null;
        var aim = new Vector3(Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value, 0f);
        var ray = camera.ScreenPointToRay(aim);
        var hits = Physics.RaycastAll(ray, Mathf.Max(25f, Range.Value), ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        var localCharacter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;

        foreach (var hit in hits)
        {
            if (!hit.collider || (localCharacter && hit.transform.IsChildOf(localCharacter.transform))) continue;
            var resolved = ResolveTarget(hit.collider);
            if (!resolved) continue;
            selectedHit = hit;
            target = resolved;
            return true;
        }
        return false;
    }

    private static Transform ResolveTarget(Collider collider)
    {
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        if (vehicle) return vehicle.transform;
        var npc = collider.GetComponentInParent<PlayerNPCController>();
        if (npc) return npc.transform;
        var dynamicObject = collider.GetComponentInParent<DynamicObject>();
        if (dynamicObject) return dynamicObject.transform;
        if (collider.attachedRigidbody) return collider.attachedRigidbody.transform;

        foreach (var behaviour in collider.GetComponentsInParent<MonoBehaviour>(true))
        {
            if (!behaviour) continue;
            var typeName = behaviour.GetType().Name;
            if (typeName == "StaticObject" || typeName == "WorldObject" || typeName == "ToolChunkSorter")
                return behaviour.transform;
        }

        // Static road, tree, and building colliders often have no gameplay component.
        // Use their closest render-bearing transform without climbing to a scene-wide root.
        var current = collider.transform;
        for (var depth = 0; current && depth < 3; depth++, current = current.parent)
            if (current.GetComponent<Renderer>()) return current;
        return collider.transform;
    }

    private static void EnforceModifiedScales()
    {
        Cleanup.Clear();
        foreach (var item in ScaleMultipliers)
        {
            if (!item.Key || !OriginalScales.TryGetValue(item.Key, out var original))
            { Cleanup.Add(item.Key); continue; }
            item.Key.localScale = Vector3.Scale(original, Vector3.one * item.Value);
        }
        foreach (var dead in Cleanup) { ScaleMultipliers.Remove(dead); OriginalScales.Remove(dead); }
    }

    private static string FriendlyName(Transform target) =>
        string.IsNullOrWhiteSpace(target.name) ? "object" : target.name.Replace("(Clone)", "").Trim();

    private static void ShowBeam(Vector3 start, Vector3 end, bool restore, bool grow)
    {
        if (!beam)
        {
            var root = new GameObject("ExtraMods Shrink Ray Beam");
            beam = root.AddComponent<LineRenderer>();
            beam.positionCount = 2;
            beam.useWorldSpace = true;
            beam.startWidth = 0.055f;
            beam.endWidth = 0.018f;
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            if (shader) { beamMaterial = new Material(shader); beam.material = beamMaterial; }
        }
        var color = restore
            ? new Color(0.2f, 0.75f, 1f)
            : grow ? new Color(1f, 0.3f, 0.15f) : new Color(0.25f, 1f, 0.2f);
        beam.startColor = color; beam.endColor = Color.white;
        if (beamMaterial) beamMaterial.color = color;
        beam.SetPosition(0, start); beam.SetPosition(1, end);
        beam.enabled = true; beamUntil = Time.unscaledTime + 0.1f;
    }

    private static void HideBeam() { if (beam) beam.enabled = false; }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var old = GUI.color;
        GUI.color = GrowMode.Value ? new Color(1f, 0.3f, 0.15f, 0.95f) : new Color(0.25f, 1f, 0.2f, 0.95f);
        GUI.DrawTexture(new Rect(x - 13f, y - 1f, 9f, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 4f, y - 1f, 9f, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1f, y - 13f, 2f, 9f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1f, y + 4f, 2f, 9f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.color = old;

        var indicator = new Rect(Screen.width * 0.5f - 85f, Screen.height - 92f, 170f, 34f);
        GUI.color = GrowMode.Value ? new Color(0.75f, 0.12f, 0.05f, 0.9f) : new Color(0.08f, 0.55f, 0.16f, 0.9f);
        GUI.Box(indicator, $"{ModeName()} MODE  [Q]");
        GUI.color = old;
    }

    private static string ModeName() => GrowMode.Value ? "GROW" : "SHRINK";
}

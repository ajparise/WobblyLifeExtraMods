using System;
using System.Collections.Generic;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>Local, grid-snapped block placement and mining mode.</summary>
public sealed class MinecraftBuildingMod : BaseMod
{
    private sealed class BlockStyle
    {
        internal readonly string Name;
        internal readonly Color Color;
        internal readonly bool Emissive;

        internal BlockStyle(string name, Color color, bool emissive = false)
        {
            Name = name;
            Color = color;
            Emissive = emissive;
        }
    }

    private const float CrosshairGap = 5f;
    private const float CrosshairLength = 9f;
    private const float CrosshairThickness = 2f;

    private static readonly BlockStyle[] Styles =
    {
        new("Grass", new Color(0.24f, 0.62f, 0.18f)),
        new("Dirt", new Color(0.42f, 0.24f, 0.11f)),
        new("Stone", new Color(0.48f, 0.5f, 0.52f)),
        new("Sand", new Color(0.83f, 0.75f, 0.46f)),
        new("Wood", new Color(0.52f, 0.31f, 0.13f)),
        new("Bricks", new Color(0.62f, 0.2f, 0.14f)),
        new("Obsidian", new Color(0.12f, 0.08f, 0.19f)),
        new("Diamond", new Color(0.15f, 0.8f, 0.84f)),
        new("Glowstone", new Color(1f, 0.62f, 0.12f), true)
    };

    private static readonly Ref<string[]> StyleNames = new(Array.ConvertAll(Styles, style => style.Name));
    private static readonly Ref<int> SelectedStyle = new();
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Minecraft building mode is unequipped.");
    private static readonly HashSet<MinecraftPlacedBlock> Blocks = new();
    private static readonly List<MinecraftPlacedBlock> DeadBlocks = new();
    private static float nextPlaceTime;
    private static float nextMineTime;

    public override string Name => "Minecraft Building Mode";

    public override string Description =>
        "Place colored blocks on a snapped grid, mine them again, and build structures anywhere in the world.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 0.5f, Max = 4f, Label = "Block size")]
    public static Ref<float> BlockSize = new(1f);

    [ModSetting(Order = 20, Min = 3f, Max = 80f, Label = "Build range")]
    public static Ref<float> BuildRange = new(18f);

    [ModSetting(Order = 30, Label = "Rapid building",
        Description = "Hold left-click to place blocks continuously.")]
    public static Ref<bool> RapidBuilding = new();

    [ModSetting(Order = 40, Label = "Rapid mining",
        Description = "Hold right-click to mine blocks continuously.")]
    public static Ref<bool> RapidMining = new();

    [ModSetting(Order = 50, Min = 0.04f, Max = 0.75f, Label = "Build interval")]
    public static Ref<float> BuildInterval = new(0.12f);

    [ModSetting(Order = 60, Min = 0.04f, Max = 0.75f, Label = "Mining interval")]
    public static Ref<float> MiningInterval = new(0.1f);

    [ModSetting(Order = 70, Label = "Show break fragments")]
    public static Ref<bool> BreakFragments = new(true);

    [ModSetting(Order = 80, Min = -400f, Max = 400f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(100f);

    [ModSetting(Order = 90, Min = -250f, Max = 250f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(35f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("MinecraftBuildHelp",
                "Equip and close F2. Left-click places a grid-snapped block, right-click mines one of your blocks, " +
                "and middle-click copies the type of the block under the crosshair. Blocks are local to this game session."),
            new SearchableCombo("Block type", Array.Empty<string>())
                .WithItems(StyleNames)
                .WithSelectedIndex(SelectedStyle),
            base.BuildPanel(id),
            new HStack("MinecraftBuildActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Place once", PlaceBlock), nameof(PlaceBlock)),
                ActionMenu(new Button("Mine aimed block", MineBlock), nameof(MineBlock))
            ).WithContentWidth(),
            new HStack("MinecraftBuildCleanup",
                ActionMenu(new Button("Remove all placed blocks", RemoveAllBlocks), nameof(RemoveAllBlocks))
            ).WithContentWidth(),
            new TextWrapped("MinecraftBuildStatus", "").WithText(Status)
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
        HeavyAutomaticGunMod.Unequip();
        GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Building mode equipped. Left place, right mine, middle pick block.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = "Minecraft building mode unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void PlaceBlock()
    {
        if (Time.unscaledTime < nextPlaceTime) return;
        nextPlaceTime = Time.unscaledTime + Mathf.Max(0.04f, BuildInterval.Value);

        if (!TryGetAimHit(out var hit))
        {
            Status.Value = "Aim at the ground, scenery, or another block to place.";
            return;
        }

        var size = Mathf.Clamp(BlockSize.Value, 0.5f, 4f);
        var rawPosition = hit.point + hit.normal * (size * 0.51f);
        var position = SnapToGrid(rawPosition, size);
        var halfExtents = Vector3.one * (size * 0.47f);

        // Permit face-to-face blocks but reject a duplicate or a block intersecting a character/vehicle.
        var overlaps = Physics.OverlapBox(position, halfExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        foreach (var overlap in overlaps)
        {
            if (!overlap) continue;
            var existing = overlap.GetComponentInParent<MinecraftPlacedBlock>();
            if (existing && Vector3.Distance(existing.transform.position, position) < size * 0.2f)
            {
                Status.Value = "A block already occupies that grid cell.";
                return;
            }

            if (overlap.GetComponentInParent<PlayerCharacter>() || overlap.GetComponentInParent<PlayerVehicle>())
            {
                Status.Value = "Cannot place a block inside a player or vehicle.";
                return;
            }
        }

        var styleIndex = Mathf.Clamp(SelectedStyle.Value, 0, Styles.Length - 1);
        var style = Styles[styleIndex];
        var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
        block.name = $"Minecraft Block - {style.Name}";
        block.transform.position = position;
        block.transform.rotation = Quaternion.identity;
        block.transform.localScale = Vector3.one * size;

        var renderer = block.GetComponent<Renderer>();
        ApplyStyle(renderer, style);

        var marker = block.AddComponent<MinecraftPlacedBlock>();
        marker.StyleIndex = styleIndex;
        Blocks.Add(marker);
        CleanupBlockSet();
        Status.Value = $"Placed {style.Name} block. Total blocks: {Blocks.Count}.";
    }

    [ModAction(ShowInUI = false)]
    public static void MineBlock()
    {
        if (Time.unscaledTime < nextMineTime) return;
        nextMineTime = Time.unscaledTime + Mathf.Max(0.04f, MiningInterval.Value);

        if (!TryGetAimHit(out var hit))
        {
            Status.Value = "No block is in mining range.";
            return;
        }

        var block = hit.collider.GetComponentInParent<MinecraftPlacedBlock>();
        if (!block)
        {
            Status.Value = "Only blocks placed by Minecraft Building Mode can be mined.";
            return;
        }

        Blocks.Remove(block);
        if (BreakFragments.Value) SpawnBreakFragments(block);
        var label = Styles[Mathf.Clamp(block.StyleIndex, 0, Styles.Length - 1)].Name;
        Object.Destroy(block.gameObject);
        Status.Value = $"Mined {label} block. Total blocks: {Blocks.Count}.";
    }

    [ModAction(ShowInUI = false)]
    public static void RemoveAllBlocks()
    {
        foreach (var block in Blocks)
        {
            if (block) Object.Destroy(block.gameObject);
        }

        Blocks.Clear();
        Status.Value = "Removed every block placed this session.";
    }

    public override void Update()
    {
        if (!EquippedState.Value || Cursor.visible) return;

        if (RapidBuilding.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0))
            PlaceBlock();
        if (RapidMining.Value ? Input.GetMouseButton(1) : Input.GetMouseButtonDown(1))
            MineBlock();
        if (Input.GetMouseButtonDown(2)) PickBlock();
    }

    private static void PickBlock()
    {
        if (!TryGetAimHit(out var hit)) return;
        var block = hit.collider.GetComponentInParent<MinecraftPlacedBlock>();
        if (!block) return;

        SelectedStyle.Value = Mathf.Clamp(block.StyleIndex, 0, Styles.Length - 1);
        Status.Value = $"Picked {Styles[SelectedStyle.Value].Name} block.";
    }

    private static bool TryGetAimHit(out RaycastHit selectedHit)
    {
        selectedHit = default;
        var camera = Camera.main;
        if (!camera) return false;

        var screenPoint = new Vector3(
            Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value,
            0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        var hits = Physics.RaycastAll(ray, Mathf.Max(1f, BuildRange.Value), ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));

        var localCharacter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;

        foreach (var hit in hits)
        {
            if (!hit.collider) continue;
            var character = hit.collider.GetComponentInParent<PlayerCharacter>();
            if (localCharacter && character == localCharacter) continue;
            selectedHit = hit;
            return true;
        }

        return false;
    }

    private static Vector3 SnapToGrid(Vector3 position, float size)
    {
        var half = size * 0.5f;
        return new Vector3(
            Mathf.Floor(position.x / size) * size + half,
            Mathf.Floor(position.y / size) * size + half,
            Mathf.Floor(position.z / size) * size + half);
    }

    private static void ApplyStyle(Renderer renderer, BlockStyle style)
    {
        if (!renderer) return;
        renderer.material.color = style.Color;
        if (style.Emissive)
        {
            renderer.material.EnableKeyword("_EMISSION");
            renderer.material.SetColor("_EmissionColor", style.Color * 1.5f);
        }
    }

    private static void SpawnBreakFragments(MinecraftPlacedBlock block)
    {
        var style = Styles[Mathf.Clamp(block.StyleIndex, 0, Styles.Length - 1)];
        var baseSize = block.transform.localScale.x;
        for (var i = 0; i < 8; i++)
        {
            var fragment = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fragment.name = "Minecraft Block Fragment";
            fragment.transform.position = block.transform.position + UnityEngine.Random.insideUnitSphere * baseSize * 0.25f;
            fragment.transform.localScale = Vector3.one * baseSize * UnityEngine.Random.Range(0.1f, 0.2f);
            Object.Destroy(fragment.GetComponent<Collider>());
            ApplyStyle(fragment.GetComponent<Renderer>(), style);

            var body = fragment.AddComponent<Rigidbody>();
            body.mass = 0.02f;
            body.velocity = UnityEngine.Random.onUnitSphere * UnityEngine.Random.Range(1f, 3f) + Vector3.up * 1.5f;
            body.angularVelocity = UnityEngine.Random.onUnitSphere * 8f;
            Object.Destroy(fragment, UnityEngine.Random.Range(0.65f, 1.2f));
        }
    }

    private static void CleanupBlockSet()
    {
        DeadBlocks.Clear();
        foreach (var block in Blocks)
        {
            if (!block) DeadBlocks.Add(block);
        }
        foreach (var block in DeadBlocks) Blocks.Remove(block);
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;

        var style = Styles[Mathf.Clamp(SelectedStyle.Value, 0, Styles.Length - 1)];
        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var previous = GUI.color;
        GUI.color = style.Color;

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

internal sealed class MinecraftPlacedBlock : MonoBehaviour
{
    internal int StyleIndex;
}

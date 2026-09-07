using System;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS.WobblyLife.SharedObjects;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;

namespace WobblyLifeExtraMods;

/// <summary>
/// An lstwoMODS panel that exposes the existing, update-resistant asset catalog and
/// spawn manager in a compact searchable list.
/// </summary>
public sealed class UniversalSpawnerMod : BaseMod
{
    private static readonly Ref<string[]> DisplayItems = new(Array.Empty<string>());
    private static readonly Ref<int> SelectedIndex = new();
    private static readonly Ref<bool> Networked = new();
    private static readonly Ref<string> Status = new("Waiting for the asset database...");

    private static List<AssetDatabase.AssetEntry> entries = new();

    public override string Name => "Universal Spawner (Extra)";

    public override string Description =>
        "Search Wobbly Life's scanned GameObject prefabs and spawn the selected object in front of the local player.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += RefreshCatalog;

        if (AssetDatabase.IsInitialized)
            RefreshCatalog();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("UniversalSpawnerHelp",
                "Select a prefab and spawn it in front of your character. Networked mode is for offline play or a lobby you host; unsupported prefabs may fail cleanly."),
            new SearchableCombo("Prefab", Array.Empty<string>())
                .WithItems(DisplayItems)
                .WithSelectedIndex(SelectedIndex),
            new Checkbox("Networked / visible to lobby", false)
                .WithValue(Networked),
            new HStack("UniversalSpawnerActions",
                ActionMenu(new Button("Spawn selected", SpawnSelected), nameof(SpawnSelected)),
                ActionMenu(new Button("Refresh catalog", RefreshCatalog), nameof(RefreshCatalog))
            ).WithContentWidth(),
            new TextWrapped("UniversalSpawnerStatus", "")
                .WithText(Status)
        );
    }

    [ModAction(ShowInUI = false, Label = "Spawn selected prefab")]
    public static void SpawnSelected()
    {
        if (entries.Count == 0)
        {
            Status.Value = "No spawnable prefabs are loaded. Enter the main menu or a save, then refresh.";
            return;
        }

        var index = SelectedIndex.Value;
        if (index < 0 || index >= entries.Count)
        {
            Status.Value = "The selected prefab is no longer in the catalog. Refresh and choose it again.";
            return;
        }

        var entry = entries[index];
        if (string.IsNullOrEmpty(entry.LoadKey))
        {
            Status.Value = $"{entry.Name} has no usable Addressables key.";
            return;
        }

        if (Networked.Value && string.IsNullOrEmpty(entry.NetworkAssetId))
        {
            Status.Value = $"{entry.Name} is not registered as a network prefab. Turn off Networked mode to spawn it locally.";
            return;
        }

        PropSpawnManager.SpawnFromLibrary(new SpawnPropMessage
        {
            Address = entry.LoadKey,
            Networked = Networked.Value
        });

        Status.Value = $"Spawn requested: {entry.Name} ({(Networked.Value ? "networked" : "local")}).";
        Plugin.Log?.LogInfo($"Spawn requested for '{entry.LoadKey}', networked={Networked.Value}.");
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshCatalog()
    {
        var previousKey = SelectedIndex.Value >= 0 && SelectedIndex.Value < entries.Count
            ? entries[SelectedIndex.Value].LoadKey
            : null;

        entries = AssetDatabase.Entries
            .Where(entry => entry.IsGameObject && !string.IsNullOrEmpty(entry.LoadKey))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LoadKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        DisplayItems.Value = entries
            .Select(entry => string.Equals(entry.Name, entry.LoadKey, StringComparison.OrdinalIgnoreCase)
                ? entry.Name
                : $"{entry.Name}  —  {entry.LoadKey}")
            .ToArray();

        var restoredIndex = previousKey == null
            ? 0
            : entries.FindIndex(entry => string.Equals(entry.LoadKey, previousKey, StringComparison.OrdinalIgnoreCase));

        SelectedIndex.Value = restoredIndex >= 0 ? restoredIndex : 0;
        Status.Value = entries.Count == 0
            ? "The asset database is not ready yet. Wait for lstwoMODS to finish scanning, then refresh."
            : $"Catalog ready: {entries.Count:N0} GameObject prefabs.";

        Plugin.Log?.LogInfo(Status.Value);
    }
}

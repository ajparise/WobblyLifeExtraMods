using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HawkNetworking;
using lstwoMODS.WobblyLife.SharedObjects;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Spawns one copy of every genuine museum artifact near the local player.</summary>
public sealed class ArtifactSpawnerMod : BaseMod
{
    private static readonly Ref<string> Status = new("Waiting for the artifact catalog...");
    private static List<AssetDatabase.AssetEntry> artifacts = new();
    private static bool spawning;
    private static int completed;
    private static int succeeded;

    public override string Name => "Every Artifact Spawner";

    public override string Description =>
        "Spawns one of every museum artifact in a tidy grid near you with one button press.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 3f, Max = 12f, Label = "Artifacts per row")]
    public static Ref<int> Columns = new(7);

    [ModSetting(Order = 20, Min = 1.5f, Max = 5f, Label = "Artifact spacing")]
    public static Ref<float> Spacing = new(2.75f);

    [ModSetting(Order = 30, Min = 4f, Max = 20f, Label = "Distance in front")]
    public static Ref<float> ForwardDistance = new(7f);

    [ModSetting(Order = 40, Min = 0.02f, Max = 0.5f, Label = "Spawn interval",
        Description = "A short delay between artifacts reduces stutter while the full set is created.")]
    public static Ref<float> SpawnInterval = new(0.07f);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += RefreshCatalog;
        if (AssetDatabase.IsInitialized) RefreshCatalog();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("ArtifactSpawnerHelp",
                "Press once to spawn one copy of every genuine museum artifact near your current position. " +
                "They appear in rows in front of the gameplay camera. Host/offline only."),
            base.BuildPanel(id),
            new HStack("ArtifactSpawnerActions",
                ActionMenu(new Button("Spawn every artifact near me", SpawnEveryArtifact), nameof(SpawnEveryArtifact)),
                ActionMenu(new Button("Refresh artifacts", RefreshCatalog), nameof(RefreshCatalog))
            ).WithContentWidth(),
            new TextWrapped("ArtifactSpawnerStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshCatalog()
    {
        artifacts = AssetDatabase.Entries
            .Where(entry => entry.IsGameObject &&
                            entry.HasComponent("MuseumArtifact") &&
                            !string.IsNullOrEmpty(entry.LoadKey) &&
                            !string.IsNullOrEmpty(entry.NetworkAssetId))
            .GroupBy(entry => entry.NetworkAssetId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!spawning)
        {
            Status.Value = artifacts.Count == 0
                ? "No networked museum artifacts found yet. Wait for the asset scan, then refresh."
                : $"Ready to spawn all {artifacts.Count:N0} museum artifacts near you.";
        }
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnEveryArtifact()
    {
        if (spawning)
        {
            Status.Value = $"Still spawning artifacts: {completed:N0}/{artifacts.Count:N0} finished.";
            return;
        }

        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can spawn the artifact collection.";
            return;
        }

        if (artifacts.Count == 0) RefreshCatalog();
        if (artifacts.Count == 0)
        {
            Status.Value = "The artifact catalog is empty. Enter a save, wait for scanning, and refresh.";
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before spawning artifacts.";
            return;
        }

        Plugin.RunCoroutine(SpawnAllRoutine(camera.transform.position, camera.transform.forward));
    }

    private static IEnumerator SpawnAllRoutine(Vector3 cameraPosition, Vector3 cameraForward)
    {
        spawning = true;
        completed = 0;
        succeeded = 0;

        var forward = Vector3.ProjectOnPlane(cameraForward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.1f) forward = Vector3.forward;
        var right = Vector3.Cross(Vector3.up, forward).normalized;
        var count = artifacts.Count;
        var columns = Mathf.Clamp(Columns.Value, 3, 12);
        var spacing = Mathf.Clamp(Spacing.Value, 1.5f, 5f);
        var basePoint = cameraPosition + forward * Mathf.Clamp(ForwardDistance.Value, 4f, 20f);

        Status.Value = $"Spawning all {count:N0} artifacts near you...";

        for (var index = 0; index < count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var rowWidth = Mathf.Min(columns, count - row * columns);
            var horizontal = (column - (rowWidth - 1) * 0.5f) * spacing;
            var candidate = basePoint + right * horizontal + forward * (row * spacing);
            var position = FindGround(candidate);
            var rotation = Quaternion.Euler(0f, Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg + 180f, 0f);

            SpawnArtifact(artifacts[index], position, rotation);
            Status.Value = $"Spawning artifacts: {index + 1:N0}/{count:N0} requested, {succeeded:N0} visible.";
            yield return new WaitForSecondsRealtime(Mathf.Clamp(SpawnInterval.Value, 0.02f, 0.5f));
        }

        var timeout = Time.realtimeSinceStartup + 20f;
        while (completed < count && Time.realtimeSinceStartup < timeout)
            yield return null;

        spawning = false;
        var failed = count - succeeded;
        Status.Value = failed == 0
            ? $"Spawned all {succeeded:N0} artifacts near you."
            : $"Spawned {succeeded:N0}/{count:N0} artifacts near you; {failed:N0} were unavailable in this world.";
        Plugin.Log?.LogInfo(Status.Value);
    }

    private static void SpawnArtifact(AssetDatabase.AssetEntry entry, Vector3 position, Quaternion rotation)
    {
        NetworkPrefab.SpawnNetworkPrefab(
            entry.NetworkAssetId,
            behaviour => FinishSpawn(entry, behaviour, position),
            position: position,
            rotation: rotation,
            owner: null,
            bUseChunkSystem: false,
            bSendTransform: true,
            bCheckChunk: false);
    }

    private static void FinishSpawn(AssetDatabase.AssetEntry entry, HawkNetworkBehaviour behaviour, Vector3 position)
    {
        completed++;
        if (behaviour == null) return;

        var artifact = behaviour.gameObject;
        artifact.SetActive(true);
        artifact.transform.position = position;

        foreach (var component in artifact.GetComponents<MonoBehaviour>())
        {
            if (component && component.GetType().Name == "ToolChunkSorter") component.enabled = false;
        }

        foreach (var renderer in artifact.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer) continue;
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
        }

        foreach (var body in artifact.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!body || body.isKinematic) continue;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.collisionDetectionMode = CollisionDetectionMode.Continuous;
            body.WakeUp();
        }

        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = entry.LoadKey,
            NetworkAssetId = entry.NetworkAssetId,
            Guid = entry.Guid,
            AssetName = FriendlyName(entry),
            Networked = true
        };

        PropSpawnManager.Register(artifact, identity, null, null);
        succeeded++;
    }

    private static Vector3 FindGround(Vector3 candidate)
    {
        var hits = Physics.RaycastAll(candidate + Vector3.up * 40f, Vector3.down, 100f, ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));

        foreach (var hit in hits)
        {
            if (!hit.collider || hit.normal.y < 0.5f) continue;
            if (hit.collider.GetComponentInParent<PlayerCharacter>() ||
                hit.collider.GetComponentInParent<PlayerVehicle>() ||
                hit.collider.GetComponentInParent<DynamicObject>())
                continue;

            return hit.point + Vector3.up * 0.65f;
        }

        return new Vector3(candidate.x, cameraFallbackHeight(candidate.y), candidate.z);
    }

    private static float cameraFallbackHeight(float cameraY) => cameraY - 1.25f;

    private static string FriendlyName(AssetDatabase.AssetEntry entry)
    {
        var value = !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : entry.LoadKey;
        if (string.IsNullOrWhiteSpace(value)) return "Unnamed artifact";

        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slash >= 0) value = value.Substring(slash + 1);
        if (value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - ".prefab".Length);
        if (value.StartsWith("Artifact_", StringComparison.OrdinalIgnoreCase))
            value = value.Substring("Artifact_".Length);
        return value.Replace('_', ' ');
    }
}

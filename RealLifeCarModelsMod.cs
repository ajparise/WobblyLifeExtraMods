using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Replaces stock road-car renderers with embedded CC0 real-world-style vehicle meshes.</summary>
public sealed class RealLifeCarModelsMod : BaseMod
{
    private static readonly string[] ModelFiles =
    {
        "Automatic", "BasicCar", "RaceCar", "SimpleCarHigh", "SimpleCarShort", "Taxi", "CopCar"
    };

    private static readonly string[] DisplayNames =
    {
        "Automatic by vehicle type", "Standard sedan", "Sports / race car", "Tall compact", "Short compact",
        "Taxi sedan", "Police sedan"
    };

    private static readonly Ref<bool> EnabledState = new();
    private static readonly Ref<string[]> ModelChoices = new(DisplayNames);
    private static readonly Ref<int> SelectedModel = new();
    private static readonly Ref<string> Status = new("Real-life car models are disabled.");
    private static readonly List<RealLifeCarVisual> Cleanup = new();
    private static float nextScan;

    public override string Name => "Real-Life Car Models";

    public override string Description =>
        "Replaces Wobbly road-car visuals with CC0 real-world-style sedans, compacts, race cars, taxis, and police cars.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = -180f, Max = 180f, Label = "Model yaw correction")]
    public static Ref<float> ModelYaw = new(0f);

    [ModSetting(Order = 20, Min = 0.65f, Max = 1.35f, Label = "Model size multiplier")]
    public static Ref<float> ModelScale = new(1f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("RealCarModelsHelp",
                "Changes appearance only: the original seats, controls, colliders, physics, and networking remain. " +
                "Automatic mode gives police and taxi vehicles matching bodies and distributes the other styles among cars. " +
                "Motorcycles, scooters, trains, boats, and aircraft are not replaced."),
            new SearchableCombo("Replacement model", DisplayNames)
                .WithItems(ModelChoices)
                .WithSelectedIndex(SelectedModel),
            base.BuildPanel(id),
            new HStack("RealCarModelsActions",
                ActionMenu(new Button("Enable real-life models", Enable), nameof(Enable)),
                ActionMenu(new Button("Apply selected model", Reapply), nameof(Reapply)),
                ActionMenu(new Button("Restore Wobbly models", Disable), nameof(Disable))
            ).WithContentWidth(),
            new TextWrapped("RealCarModelsStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void Enable()
    {
        EnabledState.Value = true;
        nextScan = 0f;
        Status.Value = "Real-life car models enabled. Scanning road vehicles...";
    }

    [ModAction(ShowInUI = false)]
    public static void Reapply()
    {
        RemoveAllReplacements();
        EnabledState.Value = true;
        nextScan = 0f;
        Status.Value = "Reapplying the selected real-life model...";
    }

    [ModAction(ShowInUI = false)]
    public static void Disable()
    {
        EnabledState.Value = false;
        RemoveAllReplacements();
        Status.Value = "Restored the original Wobbly vehicle models.";
    }

    public override void Update()
    {
        if (!EnabledState.Value || Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 1f;

        var added = 0;
        foreach (var vehicle in UnityEngine.Object.FindObjectsOfType<PlayerVehicle>())
        {
            if (!vehicle || !IsReplaceableRoadCar(vehicle) || vehicle.GetComponent<RealLifeCarVisual>()) continue;
            var model = ResolveModel(vehicle.name);
            var replacement = vehicle.gameObject.AddComponent<RealLifeCarVisual>();
            if (replacement.Apply(vehicle, model, ModelYaw.Value, ModelScale.Value)) added++;
            else UnityEngine.Object.Destroy(replacement);
        }

        if (added > 0)
        {
            var total = UnityEngine.Object.FindObjectsOfType<RealLifeCarVisual>().Length;
            Status.Value = $"Real-life models active on {total:N0} road cars.";
        }
    }

    private static bool IsReplaceableRoadCar(PlayerVehicle vehicle)
    {
        var roadVehicle = vehicle.GetComponent("PlayerVehicleRoad") || vehicle.GetComponents<MonoBehaviour>()
            .Any(component => component && component.GetType().Name == "PlayerVehicleRoad");
        if (!roadVehicle) return false;

        var name = Normalize(vehicle.name);
        return !name.Contains("scooter") && !name.Contains("motorbike") && !name.Contains("motorcycle") &&
               !name.Contains("bicycle") && !name.Contains("quad") && !name.Contains("atv") &&
               !name.Contains("gokart") && !name.Contains("train") && !name.Contains("hover");
    }

    private static string ResolveModel(string vehicleName)
    {
        var selected = Mathf.Clamp(SelectedModel.Value, 0, ModelFiles.Length - 1);
        if (selected > 0) return ModelFiles[selected];

        var normalized = Normalize(vehicleName);
        if (normalized.Contains("police") || normalized.Contains("cop")) return "CopCar";
        if (normalized.Contains("taxi")) return "Taxi";
        if (normalized.Contains("race") || normalized.Contains("formula") || normalized.Contains("super"))
            return "RaceCar";
        if (normalized.Contains("small") || normalized.Contains("compact") || normalized.Contains("threewheel"))
            return "SimpleCarShort";
        if (normalized.Contains("van") || normalized.Contains("truck") || normalized.Contains("large"))
            return "SimpleCarHigh";

        var choices = new[] { "BasicCar", "RaceCar", "SimpleCarHigh", "SimpleCarShort" };
        return choices[Math.Abs(StableHash(vehicleName)) % choices.Length];
    }

    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value ?? string.Empty) hash = hash * 31 + character;
            return hash == int.MinValue ? 0 : hash;
        }
    }

    private static string Normalize(string value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static void RemoveAllReplacements()
    {
        Cleanup.Clear();
        Cleanup.AddRange(UnityEngine.Object.FindObjectsOfType<RealLifeCarVisual>());
        foreach (var replacement in Cleanup)
            if (replacement) UnityEngine.Object.Destroy(replacement);
        Cleanup.Clear();
    }
}

internal sealed class RealLifeCarVisual : MonoBehaviour
{
    private readonly Dictionary<Renderer, bool> originalRenderers = new();
    private GameObject visualRoot;
    private bool restoring;

    internal bool Apply(PlayerVehicle vehicle, string modelName, float yaw, float sizeMultiplier)
    {
        var model = EmbeddedObjCarLibrary.Load(modelName);
        if (model == null || model.Parts.Count == 0)
        {
            Plugin.Log?.LogWarning($"Could not load embedded real-life car model '{modelName}'.");
            return false;
        }

        var targetBounds = CalculateLocalBounds(vehicle.transform, vehicle.GetComponentsInChildren<Renderer>(true));
        if (targetBounds.size.sqrMagnitude < 0.01f) return false;

        foreach (var renderer in vehicle.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer || renderer.GetComponentInParent<PlayerCharacter>()) continue;
            originalRenderers[renderer] = renderer.enabled;
            renderer.enabled = false;
        }

        visualRoot = new GameObject($"ExtraMods Real Car {modelName}");
        visualRoot.transform.SetParent(vehicle.transform, false);
        visualRoot.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

        var sourceSize = model.Bounds.size;
        var widthScale = targetBounds.size.x / Mathf.Max(0.01f, sourceSize.x);
        var lengthScale = targetBounds.size.z / Mathf.Max(0.01f, sourceSize.z);
        var fitScale = Mathf.Clamp(Mathf.Min(widthScale, lengthScale) * Mathf.Clamp(sizeMultiplier, 0.65f, 1.35f),
            0.25f, 4f);
        visualRoot.transform.localScale = Vector3.one * fitScale;
        visualRoot.transform.localPosition = new Vector3(
            targetBounds.center.x - model.Bounds.center.x * fitScale,
            targetBounds.min.y - model.Bounds.min.y * fitScale,
            targetBounds.center.z - model.Bounds.center.z * fitScale);

        var bodyColor = ColorFor(vehicle.name, modelName);
        foreach (var part in model.Parts)
        {
            var partObject = new GameObject(part.Name);
            partObject.transform.SetParent(visualRoot.transform, false);
            var filter = partObject.AddComponent<MeshFilter>();
            filter.sharedMesh = part.Mesh;
            var renderer = partObject.AddComponent<MeshRenderer>();
            renderer.material = CreateMaterial(part.IsWheel ? new Color(0.055f, 0.06f, 0.065f) : bodyColor,
                part.IsWheel ? 0.1f : 0.7f);
        }

        Plugin.Log?.LogInfo($"Replaced '{vehicle.name}' visuals with CC0 model '{modelName}'.");
        return true;
    }

    private void OnDestroy()
    {
        if (restoring) return;
        restoring = true;
        foreach (var saved in originalRenderers)
            if (saved.Key) saved.Key.enabled = saved.Value;
        originalRenderers.Clear();
        if (visualRoot) UnityEngine.Object.Destroy(visualRoot);
    }

    private static Bounds CalculateLocalBounds(Transform vehicleRoot, IEnumerable<Renderer> renderers)
    {
        var initialized = false;
        var result = new Bounds();
        foreach (var renderer in renderers)
        {
            if (!renderer || renderer.GetComponentInParent<PlayerCharacter>()) continue;
            var bounds = renderer.bounds;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var world = bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z));
                var local = vehicleRoot.InverseTransformPoint(world);
                if (!initialized) { result = new Bounds(local, Vector3.zero); initialized = true; }
                else result.Encapsulate(local);
            }
        }
        return result;
    }

    private static Color ColorFor(string vehicleName, string modelName)
    {
        if (modelName == "CopCar") return new Color(0.12f, 0.24f, 0.48f);
        if (modelName == "Taxi") return new Color(0.95f, 0.66f, 0.05f);
        var palette = new[]
        {
            new Color(0.68f, 0.06f, 0.05f), new Color(0.06f, 0.24f, 0.62f),
            new Color(0.08f, 0.48f, 0.22f), new Color(0.82f, 0.82f, 0.84f),
            new Color(0.08f, 0.08f, 0.1f), new Color(0.85f, 0.34f, 0.04f)
        };
        return palette[Math.Abs(vehicleName?.GetHashCode() ?? 0) % palette.Length];
    }

    private static Material CreateMaterial(Color color, float smoothness)
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse") ?? Shader.Find("Sprites/Default");
        var material = new Material(shader) { color = color };
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", smoothness * 0.35f);
        return material;
    }
}

internal sealed class EmbeddedObjCarModel
{
    internal readonly List<EmbeddedObjCarPart> Parts = new();
    internal Bounds Bounds;
}

internal sealed class EmbeddedObjCarPart
{
    internal string Name;
    internal Mesh Mesh;
    internal bool IsWheel;
}

internal static class EmbeddedObjCarLibrary
{
    private static readonly Dictionary<string, EmbeddedObjCarModel> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static EmbeddedObjCarModel Load(string modelName)
    {
        if (Cache.TryGetValue(modelName, out var cached)) return cached;

        var resourceName = $"WobblyLifeExtraMods.Assets.RealCars.{modelName}.obj";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var parsed = Parse(reader, modelName);
        Cache[modelName] = parsed;
        return parsed;
    }

    private static EmbeddedObjCarModel Parse(TextReader reader, string modelName)
    {
        var sourceVertices = new List<Vector3>();
        var builders = new List<ObjPartBuilder>();
        var current = new ObjPartBuilder(modelName + " Body");
        builders.Add(current);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.StartsWith("v ", StringComparison.Ordinal))
            {
                var values = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length >= 4 && TryFloat(values[1], out var x) && TryFloat(values[2], out var y) &&
                    TryFloat(values[3], out var z)) sourceVertices.Add(new Vector3(x, y, z));
            }
            else if (line.StartsWith("o ", StringComparison.Ordinal))
            {
                current = new ObjPartBuilder(line.Substring(2).Trim());
                builders.Add(current);
            }
            else if (line.StartsWith("f ", StringComparison.Ordinal))
            {
                var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 4) continue;
                var polygon = new List<int>();
                for (var index = 1; index < tokens.Length; index++)
                {
                    var vertexToken = tokens[index].Split('/')[0];
                    if (!int.TryParse(vertexToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var objIndex))
                        continue;
                    var sourceIndex = objIndex > 0 ? objIndex - 1 : sourceVertices.Count + objIndex;
                    if (sourceIndex >= 0 && sourceIndex < sourceVertices.Count) polygon.Add(sourceIndex);
                }
                for (var index = 1; index + 1 < polygon.Count; index++)
                    current.AddTriangle(sourceVertices[polygon[0]], sourceVertices[polygon[index + 1]],
                        sourceVertices[polygon[index]]);
            }
        }

        var model = new EmbeddedObjCarModel();
        var boundsInitialized = false;
        foreach (var builder in builders.Where(candidate => candidate.Vertices.Count > 0))
        {
            var mesh = new Mesh { name = $"ExtraMods {modelName} {builder.Name}" };
            mesh.SetVertices(builder.Vertices);
            mesh.SetTriangles(builder.Triangles, 0);
            mesh.RecalculateBounds();
            mesh.RecalculateNormals();
            model.Parts.Add(new EmbeddedObjCarPart
            {
                Name = builder.Name,
                Mesh = mesh,
                IsWheel = builder.Name.IndexOf("wheel", StringComparison.OrdinalIgnoreCase) >= 0
            });
            if (!boundsInitialized) { model.Bounds = mesh.bounds; boundsInitialized = true; }
            else model.Bounds.Encapsulate(mesh.bounds);
        }
        return model;
    }

    private static bool TryFloat(string value, out float result) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);

    private sealed class ObjPartBuilder
    {
        internal readonly string Name;
        internal readonly List<Vector3> Vertices = new();
        internal readonly List<int> Triangles = new();

        internal ObjPartBuilder(string name) { Name = string.IsNullOrWhiteSpace(name) ? "Car Body" : name; }

        internal void AddTriangle(Vector3 first, Vector3 second, Vector3 third)
        {
            var start = Vertices.Count;
            Vertices.Add(first);
            Vertices.Add(second);
            Vertices.Add(third);
            Triangles.Add(start);
            Triangles.Add(start + 1);
            Triangles.Add(start + 2);
        }
    }
}

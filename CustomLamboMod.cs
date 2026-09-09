using System;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Spawns a functional road-car chassis with a generated wedge supercar body.</summary>
public sealed class CustomLamboMod : BaseMod
{
    private static readonly string[] PreferredChassisNames =
    {
        "lab race car", "labracecar", "formula wobbly", "super car", "supercar", "sports car",
        "sport car", "race car", "racing car", "formula", "convertable"
    };

    private static readonly Ref<string> Status = new("Waiting for the Wobbly Life vehicle catalog...");
    private static AssetDatabase.AssetEntry chassis;

    public override string Name => "Custom Cars: Lambo";
    public override string Description =>
        "Spawns a fast, functional custom Lambo-style wedge supercar built over a native Wobbly Life road chassis.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 35f, Label = "Spawn distance")]
    public static Ref<float> SpawnDistance = new(10f);

    [ModSetting(Order = 20, Min = 0f, Max = 1f, Label = "Body red")]
    public static Ref<float> BodyRed = new(1f);

    [ModSetting(Order = 30, Min = 0f, Max = 1f, Label = "Body green")]
    public static Ref<float> BodyGreen = new(0.34f);

    [ModSetting(Order = 40, Min = 0f, Max = 1f, Label = "Body blue")]
    public static Ref<float> BodyBlue = new(0.02f);

    [ModSetting(Order = 50, Min = 30f, Max = 220f, Label = "Maximum speed")]
    public static Ref<float> MaximumSpeed = new(105f);

    [ModSetting(Order = 60, Min = 5f, Max = 120f, Label = "Extra acceleration")]
    public static Ref<float> Acceleration = new(48f);

    [ModSetting(Order = 70, Min = 0f, Max = 30f, Label = "High-speed downforce")]
    public static Ref<float> Downforce = new(8f);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += ResolveChassis;
        if (AssetDatabase.IsInitialized) ResolveChassis();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("CustomLamboHelp",
                "Choose a body color and spawn the Lambo after entering a save. It uses a genuine networked road-car " +
                "chassis for driving, seats, wheels, collisions, damage, and multiplayer synchronization. The mod replaces " +
                "the stock body locally with a low wedge shell, glass cabin, sharp lights, intakes, diffuser, and rear wing. " +
                "W adds the configured supercar acceleration. Host/offline spawning is required."),
            base.BuildPanel(id),
            new HStack("CustomLamboActions",
                ActionMenu(new Button("Spawn custom Lambo", SpawnLambo), nameof(SpawnLambo)),
                ActionMenu(new Button("Refresh chassis", ResolveChassis), nameof(ResolveChassis))
            ).WithContentWidth(),
            new TextWrapped("CustomLamboStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void ResolveChassis()
    {
        var roadVehicles = AssetDatabase.Entries
            .Where(entry => entry.IsGameObject && entry.HasComponent("PlayerVehicleRoad") &&
                            !string.IsNullOrEmpty(entry.NetworkAssetId) && !string.IsNullOrEmpty(entry.LoadKey))
            .ToList();

        chassis = roadVehicles
            .Select(entry => new { Entry = entry, Name = FriendlyName(entry).ToLowerInvariant() })
            .OrderBy(candidate => PreferredScore(candidate.Name))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Entry)
            .FirstOrDefault();

        Status.Value = chassis == null
            ? "No networked road-car chassis was found. Enter a save, wait for the asset scan, and refresh."
            : $"Lambo chassis ready: {FriendlyName(chassis)}. The stock body will be replaced when spawned.";
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnLambo()
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can spawn the custom Lambo.";
            return;
        }
        if (chassis == null)
        {
            ResolveChassis();
            if (chassis == null) return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "Enter a save before spawning the custom Lambo.";
            return;
        }

        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        var position = camera.transform.position + forward * Mathf.Max(5f, SpawnDistance.Value) + Vector3.up * 1.5f;
        var rotation = Quaternion.LookRotation(forward, Vector3.up);
        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = chassis.LoadKey,
            NetworkAssetId = chassis.NetworkAssetId,
            Guid = chassis.Guid,
            AssetName = "Custom Lambo",
            Networked = true
        };

        Status.Value = "Spawning the custom Lambo...";
        TrySpawn(PropAddressResolver.Fallbacks(identity).ToList(), 0, position, rotation, identity);
    }

    private static void TrySpawn(IReadOnlyList<string> candidates, int index, Vector3 position,
        Quaternion rotation, PropIdentity identity)
    {
        if (index >= candidates.Count)
        {
            Status.Value = "The Lambo chassis could not be spawned with any registered network key.";
            return;
        }

        NetworkPrefab.SpawnNetworkPrefab(candidates[index], behaviour =>
        {
            if (behaviour == null)
            {
                TrySpawn(candidates, index + 1, position, rotation, identity);
                return;
            }

            var vehicle = behaviour.GetComponent<PlayerVehicle>();
            if (!vehicle)
            {
                Status.Value = "The selected chassis spawned but did not contain a road vehicle.";
                return;
            }

            PropSpawnManager.Register(behaviour.gameObject, identity, null, null);
            BuildLamboBody(vehicle);
            var performance = vehicle.gameObject.AddComponent<CustomLamboPerformance>();
            performance.Configure(vehicle);
            behaviour.gameObject.name = "Custom Lambo";
            Status.Value = "Custom Lambo spawned. Enter it and hold W for supercar acceleration.";
            Plugin.Log?.LogInfo($"Spawned Custom Lambo using chassis {FriendlyName(chassis)}.");
        }, position, rotation, null, true, true, true);
    }

    private static void BuildLamboBody(PlayerVehicle vehicle)
    {
        var bounds = CalculateVehicleBounds(vehicle.transform);
        HideStockBody(vehicle.transform);

        var width = Mathf.Clamp(bounds.size.x, 1.65f, 2.55f);
        var length = Mathf.Clamp(bounds.size.z, 3.5f, 6.2f);
        var height = Mathf.Clamp(bounds.size.y, 1.15f, 2.1f);
        var bottom = bounds.min.y + height * 0.08f;
        var centerZ = bounds.center.z;

        var shell = new GameObject("Custom Lambo Visual Body");
        shell.transform.SetParent(vehicle.transform, false);
        var bodyColor = new Color(Mathf.Clamp01(BodyRed.Value), Mathf.Clamp01(BodyGreen.Value),
            Mathf.Clamp01(BodyBlue.Value), 1f);
        var paint = MakeMaterial(bodyColor);
        var paintDark = MakeMaterial(Color.Lerp(bodyColor, Color.black, 0.42f));
        var glass = MakeMaterial(new Color(0.025f, 0.055f, 0.075f, 1f));
        var black = MakeMaterial(new Color(0.018f, 0.018f, 0.022f, 1f));
        var headlight = MakeMaterial(new Color(0.72f, 0.95f, 1f, 1f));
        var taillight = MakeMaterial(new Color(1f, 0.015f, 0.005f, 1f));

        var body = CreateLoft("Angular Wedge Body", shell.transform, centerZ,
            new[] { length * 0.5f, length * 0.28f, -length * 0.28f, -length * 0.5f },
            new[] { width * 0.34f, width * 0.5f, width * 0.5f, width * 0.43f },
            new[] { bottom, bottom, bottom, bottom + height * 0.03f },
            new[] { bottom + height * 0.25f, bottom + height * 0.38f,
                bottom + height * 0.49f, bottom + height * 0.45f }, paint);

        CreateLoft("Dark Glass Cabin", shell.transform, centerZ,
            new[] { length * 0.12f, -length * 0.28f },
            new[] { width * 0.34f, width * 0.37f },
            new[] { bottom + height * 0.37f, bottom + height * 0.47f },
            new[] { bottom + height * 0.73f, bottom + height * 0.75f }, glass);

        CreateBox("Roof Blade", shell.transform,
            new Vector3(0f, bottom + height * 0.76f, centerZ - length * 0.09f),
            new Vector3(width * 0.61f, height * 0.035f, length * 0.25f), Quaternion.identity, paintDark);
        CreateBox("Front Splitter", shell.transform,
            new Vector3(0f, bottom + height * 0.04f, centerZ + length * 0.49f),
            new Vector3(width * 0.88f, height * 0.045f, length * 0.055f), Quaternion.identity, black);
        CreateBox("Rear Diffuser", shell.transform,
            new Vector3(0f, bottom + height * 0.08f, centerZ - length * 0.49f),
            new Vector3(width * 0.82f, height * 0.1f, length * 0.05f), Quaternion.identity, black);

        for (var side = -1; side <= 1; side += 2)
        {
            CreateBox($"Headlight {(side < 0 ? "L" : "R")}", shell.transform,
                new Vector3(side * width * 0.31f, bottom + height * 0.28f, centerZ + length * 0.485f),
                new Vector3(width * 0.24f, height * 0.055f, length * 0.025f),
                Quaternion.Euler(0f, side * -12f, side * -7f), headlight);
            CreateBox($"Tail Light {(side < 0 ? "L" : "R")}", shell.transform,
                new Vector3(side * width * 0.29f, bottom + height * 0.38f, centerZ - length * 0.49f),
                new Vector3(width * 0.27f, height * 0.045f, length * 0.02f), Quaternion.identity, taillight);
            CreateBox($"Side Intake {(side < 0 ? "L" : "R")}", shell.transform,
                new Vector3(side * width * 0.505f, bottom + height * 0.3f, centerZ - length * 0.19f),
                new Vector3(width * 0.018f, height * 0.19f, length * 0.21f),
                Quaternion.Euler(side * 8f, 0f, 0f), black);
            CreateBox($"Wing Post {(side < 0 ? "L" : "R")}", shell.transform,
                new Vector3(side * width * 0.28f, bottom + height * 0.62f, centerZ - length * 0.38f),
                new Vector3(width * 0.035f, height * 0.25f, length * 0.025f), Quaternion.identity, black);
        }

        CreateBox("Rear Wing", shell.transform,
            new Vector3(0f, bottom + height * 0.74f, centerZ - length * 0.39f),
            new Vector3(width * 0.86f, height * 0.055f, length * 0.16f), Quaternion.Euler(-7f, 0f, 0f), paintDark);
        CreateBox("Hood Center Crease", shell.transform,
            new Vector3(0f, bottom + height * 0.405f, centerZ + length * 0.27f),
            new Vector3(width * 0.055f, height * 0.018f, length * 0.36f), Quaternion.Euler(4f, 0f, 0f), paintDark);

        AddPointLight(shell.transform, new Vector3(-width * 0.31f, bottom + height * 0.3f,
            centerZ + length * 0.5f), new Color(0.65f, 0.9f, 1f));
        AddPointLight(shell.transform, new Vector3(width * 0.31f, bottom + height * 0.3f,
            centerZ + length * 0.5f), new Color(0.65f, 0.9f, 1f));

        // Keep materials owned by the shell so they can be released with the vehicle.
        var resources = shell.AddComponent<CustomLamboVisualResources>();
        resources.Materials = new[] { paint, paintDark, glass, black, headlight, taillight };
    }

    private static Bounds CalculateVehicleBounds(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(true)
            .Where(renderer => renderer && !IsEffectRenderer(renderer))
            .ToArray();
        var initialized = false;
        var local = new Bounds(Vector3.zero, new Vector3(2f, 1.5f, 4f));
        foreach (var renderer in renderers)
        {
            var bounds = renderer.bounds;
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
            {
                var point = root.InverseTransformPoint(bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3(x, y, z)));
                if (!initialized)
                {
                    local = new Bounds(point, Vector3.zero);
                    initialized = true;
                }
                else local.Encapsulate(point);
            }
        }
        return local;
    }

    private static void HideStockBody(Transform vehicle)
    {
        foreach (var renderer in vehicle.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer || IsEffectRenderer(renderer)) continue;
            if (IsWheelRenderer(renderer.transform, vehicle)) continue;
            renderer.enabled = false;
        }
    }

    private static bool IsWheelRenderer(Transform current, Transform vehicle)
    {
        while (current && current != vehicle)
        {
            var name = current.name.ToLowerInvariant();
            if (name.Contains("wheel") || name.Contains("tyre") || name.Contains("tire") || name.Contains("rim"))
                return true;
            current = current.parent;
        }
        return false;
    }

    private static bool IsEffectRenderer(Renderer renderer)
    {
        var typeName = renderer.GetType().Name;
        return typeName == "ParticleSystemRenderer" || typeName == "TrailRenderer" || typeName == "LineRenderer";
    }

    private static GameObject CreateLoft(string name, Transform parent, float centerZ, float[] z,
        float[] halfWidths, float[] bottoms, float[] tops, Material material)
    {
        var vertices = new List<Vector3>();
        for (var i = 0; i < z.Length; i++)
        {
            var sectionZ = centerZ + z[i];
            vertices.Add(new Vector3(-halfWidths[i], bottoms[i], sectionZ));
            vertices.Add(new Vector3(halfWidths[i], bottoms[i], sectionZ));
            vertices.Add(new Vector3(halfWidths[i], tops[i], sectionZ));
            vertices.Add(new Vector3(-halfWidths[i], tops[i], sectionZ));
        }

        var triangles = new List<int>();
        AddQuad(triangles, 0, 1, 2, 3);
        for (var i = 0; i < z.Length - 1; i++)
        {
            var a = i * 4;
            var b = (i + 1) * 4;
            AddQuad(triangles, a, b, b + 1, a + 1);
            AddQuad(triangles, a + 1, b + 1, b + 2, a + 2);
            AddQuad(triangles, a + 2, b + 2, b + 3, a + 3);
            AddQuad(triangles, a + 3, b + 3, b, a);
        }
        var last = (z.Length - 1) * 4;
        AddQuad(triangles, last + 3, last + 2, last + 1, last);

        var mesh = new Mesh { name = name + " Mesh" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.AddComponent<MeshFilter>().sharedMesh = mesh;
        obj.AddComponent<MeshRenderer>().sharedMaterial = material;
        var holder = obj.AddComponent<CustomLamboMeshResource>();
        holder.Mesh = mesh;
        return obj;
    }

    private static void AddQuad(ICollection<int> triangles, int a, int b, int c, int d)
    {
        triangles.Add(a); triangles.Add(b); triangles.Add(c);
        triangles.Add(a); triangles.Add(c); triangles.Add(d);
    }

    private static GameObject CreateBox(string name, Transform parent, Vector3 localPosition,
        Vector3 localScale, Quaternion localRotation, Material material)
    {
        var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
        obj.name = name;
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = localPosition;
        obj.transform.localRotation = localRotation;
        obj.transform.localScale = localScale;
        var collider = obj.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        obj.GetComponent<Renderer>().sharedMaterial = material;
        return obj;
    }

    private static void AddPointLight(Transform parent, Vector3 localPosition, Color color)
    {
        var obj = new GameObject("Lambo Headlight Glow");
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = localPosition;
        var light = obj.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.range = 5f;
        light.intensity = 0.8f;
    }

    private static Material MakeMaterial(Color color)
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        var material = new Material(shader) { color = color };
        if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", 0.82f);
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0.32f);
        return material;
    }

    private static int PreferredScore(string name)
    {
        for (var i = 0; i < PreferredChassisNames.Length; i++)
            if (name.Contains(PreferredChassisNames[i])) return i;
        return PreferredChassisNames.Length + (name.Contains("car") ? 0 : 10);
    }

    private static string FriendlyName(AssetDatabase.AssetEntry entry)
    {
        if (entry == null) return "unknown road car";
        var value = !string.IsNullOrWhiteSpace(entry.Address) ? entry.Address : entry.Name;
        if (string.IsNullOrWhiteSpace(value)) return "unnamed road car";
        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slash >= 0) value = value.Substring(slash + 1);
        if (value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            value = value.Substring(0, value.Length - 7);
        if (value.StartsWith("Vehicle_", StringComparison.OrdinalIgnoreCase)) value = value.Substring(8);
        return value.Replace('_', ' ');
    }
}

internal sealed class CustomLamboPerformance : MonoBehaviour
{
    private PlayerVehicle vehicle;
    private Rigidbody body;

    internal void Configure(PlayerVehicle target)
    {
        vehicle = target;
        body = target.GetComponent<Rigidbody>() ?? target.GetComponentInChildren<Rigidbody>();
    }

    private void FixedUpdate()
    {
        if (!vehicle || !body) return;
        var driver = vehicle.GetDriverPlayerController();
        if (!driver || !driver.IsLocal()) return;

        var forwardSpeed = Vector3.Dot(body.velocity, vehicle.transform.forward);
        if (Input.GetKey(KeyCode.W) && forwardSpeed < CustomLamboMod.MaximumSpeed.Value)
            body.AddForce(vehicle.transform.forward * Mathf.Max(5f, CustomLamboMod.Acceleration.Value),
                ForceMode.Acceleration);

        var flatVelocity = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
        var maxSpeed = Mathf.Max(30f, CustomLamboMod.MaximumSpeed.Value);
        if (flatVelocity.sqrMagnitude > maxSpeed * maxSpeed)
            body.velocity = flatVelocity.normalized * maxSpeed + Vector3.Project(body.velocity, Vector3.up);

        if (flatVelocity.sqrMagnitude > 100f)
            body.AddForce(-vehicle.transform.up * Mathf.Max(0f, CustomLamboMod.Downforce.Value), ForceMode.Acceleration);
    }
}

internal sealed class CustomLamboVisualResources : MonoBehaviour
{
    internal Material[] Materials { get; set; }

    private void OnDestroy()
    {
        if (Materials == null) return;
        foreach (var material in Materials)
            if (material) Destroy(material);
    }
}

internal sealed class CustomLamboMeshResource : MonoBehaviour
{
    internal Mesh Mesh { get; set; }

    private void OnDestroy()
    {
        if (Mesh) Destroy(Mesh);
    }
}

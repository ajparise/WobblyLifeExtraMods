using System;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class KarateDojoMod : BaseMod
{
    private static readonly string[] BeltNames = { "White", "Yellow", "Green", "Red", "Blue", "Black" };
    private static readonly Color[] BeltColors = { Color.white, Color.yellow, new Color(0.15f, 0.75f, 0.2f), Color.red, new Color(0.1f, 0.3f, 1f), Color.black };
    private static readonly Ref<string> Status = new("Enter a world and start karate.");
    private static string saveKey;
    private static bool enabled;
    private static GameObject dojo;
    private static GameObject belt;
    private static Transform beltParent;
    private static Material beltMaterial;
    private static int beltVisualIndex = -1;
    private static int sessions;
    private static int lastDay = -1;
    private static int observedDay = -1;
    private static int strikes;
    private static bool training;
    private static float trainingEnds;
    private static string alert = "";
    private static float alertEnds;
    public override string Name => "Paradise Island Karate Dojo";
    public override string Description => "Build a dojo, train daily and progress from white to black belt.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("KarateHelp", "Go to a clear spot on Paradise Island, start karate, then press Place dojo here to put the building in front of you. Stand on its mat and press K ten times within 30 seconds to complete one class per in-game day. Five completed class days earn each new belt: white, yellow, green, red, blue, black. Progress and placement are stored locally per save slot. The building and visible belt are local to this computer."),
        new HStack("KarateActions", ActionMenu(new Button("Start karate", Enable), nameof(Enable)),
            ActionMenu(new Button("Place dojo here", PlaceDojo), nameof(PlaceDojo)),
            ActionMenu(new Button("Stop karate", Disable), nameof(Disable))).WithContentWidth(),
        new TextWrapped("KarateStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)] public static void Enable()
    {
        if (!EnsureWorld()) { Status.Value = "Load a world and wait for the save and player to be ready."; return; }
        if (!enabled)
        {
            enabled = true; PlayerPrefs.SetInt(saveKey + ".enabled", 1); PlayerPrefs.Save();
            LoadDojo();
        }
        Announce("YOUR KARATE CLASS IS OPEN!");
        Status.Value = dojo ? "Karate class is open! Stand on the mat and press K to train." : "Karate class is open! Go to Paradise Island and press Place dojo here.";
    }
    [ModAction(ShowInUI = false)] public static void Disable()
    {
        if (saveKey != null) { PlayerPrefs.SetInt(saveKey + ".enabled", 0); PlayerPrefs.Save(); }
        enabled = false; training = false; RemoveSceneObjects(); Status.Value = "Karate mode stopped; progress and dojo placement are saved.";
    }
    [ModAction(ShowInUI = false)] public static void PlaceDojo()
    {
        if (!EnsureWorld() || !enabled) { Status.Value = "Enter a world and start karate first."; return; }
        var player = Character(); var camera = Camera.main;
        if (!player || !camera) { Status.Value = "Your player or camera is not ready."; return; }
        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        var candidate = player.GetCenterFeetPosition() + forward * 15f;
        var hits = Physics.RaycastAll(candidate + Vector3.up * 35f, Vector3.down, 100f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        var found = false; var ground = Vector3.zero;
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.normal.y < 0.7f || hit.transform.IsChildOf(player.transform) ||
                hit.collider.GetComponentInParent<PlayerVehicle>() || hit.collider.GetComponentInParent<DynamicObject>() ||
                hit.collider.GetComponentInParent<Water>() || (dojo && hit.transform.IsChildOf(dojo.transform))) continue;
            ground = hit.point; found = true; break;
        }
        if (!found) { Status.Value = "No solid ground found in front. Try a clear spot on Paradise Island."; return; }
        var rotation = Quaternion.LookRotation(forward, Vector3.up);
        if (dojo) UnityEngine.Object.Destroy(dojo);
        dojo = KarateDojoBuilding.Create(ground, rotation);
        PlayerPrefs.SetFloat(saveKey + ".x", ground.x); PlayerPrefs.SetFloat(saveKey + ".y", ground.y);
        PlayerPrefs.SetFloat(saveKey + ".z", ground.z); PlayerPrefs.SetFloat(saveKey + ".yaw", rotation.eulerAngles.y);
        PlayerPrefs.SetInt(saveKey + ".placed", 1); PlayerPrefs.Save();
        Status.Value = "Dojo placed! Enter through the front and stand on the mat.";
    }
    private static void LoadDojo()
    {
        if (dojo || saveKey == null || PlayerPrefs.GetInt(saveKey + ".placed", 0) == 0) return;
        var position = new Vector3(PlayerPrefs.GetFloat(saveKey + ".x"), PlayerPrefs.GetFloat(saveKey + ".y"), PlayerPrefs.GetFloat(saveKey + ".z"));
        dojo = KarateDojoBuilding.Create(position, Quaternion.Euler(0f, PlayerPrefs.GetFloat(saveKey + ".yaw"), 0f));
    }
    private static bool EnsureWorld()
    {
        if (!GameInstance.InstanceExists || !Character() || !SaveGameManager.InstanceExists || !SaveGameManager.InstanceRaw.HasLoadedActiveSlot()) return false;
        var slot = SaveGameManager.InstanceRaw.GetActiveSlotData();
        var path = SaveGameManager.InstanceRaw.GetFullSavePath();
        if (slot == null || string.IsNullOrEmpty(path)) return false;
        var key = "WLE.Karate." + StableHash(path + "|" + slot.GetSaveSlot());
        if (saveKey != key)
        {
            RemoveSceneObjects(); saveKey = key; sessions = Mathf.Max(0, PlayerPrefs.GetInt(key + ".sessions", 0));
            lastDay = PlayerPrefs.GetInt(key + ".lastDay", -1); observedDay = -1; training = false;
            enabled = PlayerPrefs.GetInt(key + ".enabled", 0) != 0;
            if (enabled) LoadDojo();
        }
        return true;
    }
    private static string StableHash(string value)
    {
        unchecked { uint hash = 2166136261; foreach (var c in value) { hash ^= c; hash *= 16777619; } return hash.ToString("X8"); }
    }
    private static PlayerCharacter Character() => GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter() : null;
    public override void Update()
    {
        if (!GameInstance.InstanceExists)
        {
            if (saveKey != null) { RemoveSceneObjects(); saveKey = null; enabled = false; training = false; }
            return;
        }
        if (!EnsureWorld() || !enabled) return;
        LoadDojo(); UpdateBelt();
        if (!DayNightCycle.InstanceExists) return;
        var day = DayNightCycle.Instance.GetDayNum();
        if (observedDay != day)
        {
            if (lastDay > day) { lastDay = -1; PlayerPrefs.SetInt(saveKey + ".lastDay", lastDay); PlayerPrefs.Save(); }
            if (observedDay >= 0) { training = false; Announce("YOUR KARATE CLASS IS OPEN!"); }
            observedDay = day;
        }
        if (training && (Time.unscaledTime >= trainingEnds || !OnMat()))
        { training = false; Status.Value = "Class ended. Stand on the mat and try again today."; }
        if (Cursor.visible || !Input.GetKeyDown(KeyCode.K) || !OnMat()) return;
        if (day == lastDay) { Status.Value = "Today's class is complete. Come back on the next in-game day."; return; }
        if (!training) { training = true; strikes = 0; trainingEnds = Time.unscaledTime + 30f; }
        strikes++;
        if (strikes >= 10)
        {
            training = false; lastDay = day; var oldBelt = BeltIndex; sessions++;
            PlayerPrefs.SetInt(saveKey + ".sessions", sessions); PlayerPrefs.SetInt(saveKey + ".lastDay", day); PlayerPrefs.Save();
            if (BeltIndex > oldBelt) { Announce("NEW BELT: " + BeltNames[BeltIndex].ToUpperInvariant() + "!"); Status.Value = "Congratulations! You earned your " + BeltNames[BeltIndex] + " belt."; }
            else { Announce("CLASS COMPLETE!"); Status.Value = "Class complete! " + ProgressText(); }
        }
        else Status.Value = "Karate practice: " + strikes + "/10. Press K again!";
    }
    private static int BeltIndex => Mathf.Clamp(sessions / 5, 0, BeltNames.Length - 1);
    private static string ProgressText() => BeltIndex == BeltNames.Length - 1 ? "Black belt achieved!" : (sessions % 5) + "/5 classes toward " + BeltNames[BeltIndex + 1] + " belt.";
    private static bool OnMat()
    {
        var character = Character(); if (!character || !dojo) return false;
        var local = dojo.transform.InverseTransformPoint(character.GetCenterFeetPosition());
        return Mathf.Abs(local.x) < 3.8f && Mathf.Abs(local.z) < 3.4f && local.y > -1f && local.y < 3f;
    }
    private static void UpdateBelt()
    {
        var character = Character(); var body = character ? character.GetPlayerBody() : null;
        var parent = body ? body.transform : null;
        if (!parent) { RemoveBelt(); return; }
        if (belt && beltParent == parent && beltVisualIndex == BeltIndex) return;
        RemoveBelt(); beltParent = parent; beltVisualIndex = BeltIndex;
        belt = new GameObject("Karate belt visual"); belt.transform.SetParent(parent, false); belt.transform.localPosition = new Vector3(0f, -0.1f, 0f);
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        if (shader) beltMaterial = new Material(shader) { color = BeltColors[BeltIndex] };
        AddBeltPart(new Vector3(0f, 0f, 0.31f), new Vector3(0.65f, 0.11f, 0.05f));
        AddBeltPart(new Vector3(0f, 0f, -0.31f), new Vector3(0.65f, 0.11f, 0.05f));
        AddBeltPart(new Vector3(0.31f, 0f, 0f), new Vector3(0.05f, 0.11f, 0.62f));
        AddBeltPart(new Vector3(-0.31f, 0f, 0f), new Vector3(0.05f, 0.11f, 0.62f));
    }
    private static void AddBeltPart(Vector3 position, Vector3 scale)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Cube); part.name = "Karate belt";
        var collider = part.GetComponent<Collider>(); if (collider) { collider.enabled = false; UnityEngine.Object.Destroy(collider); }
        part.transform.SetParent(belt.transform, false); part.transform.localPosition = position; part.transform.localScale = scale;
        if (beltMaterial) part.GetComponent<Renderer>().sharedMaterial = beltMaterial;
    }
    private static void RemoveBelt()
    {
        if (belt) UnityEngine.Object.Destroy(belt); if (beltMaterial) UnityEngine.Object.Destroy(beltMaterial);
        belt = null; beltMaterial = null; beltParent = null; beltVisualIndex = -1;
    }
    private static void RemoveSceneObjects() { if (dojo) UnityEngine.Object.Destroy(dojo); dojo = null; RemoveBelt(); }
    private static void Announce(string message) { alert = message; alertEnds = Time.unscaledTime + 5f; }
    internal static void DrawOverlay()
    {
        if (!enabled || !GameInstance.InstanceExists || Event.current.type != EventType.Repaint) return;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        var rect = new Rect((Screen.width - 530f) * 0.5f, 100f, 530f, 65f);
        var old = GUI.color; GUI.color = new Color(0f, 0f, 0f, 0.78f); GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = old;
        var text = Time.unscaledTime < alertEnds ? alert : BeltNames[BeltIndex].ToUpperInvariant() + " BELT  •  " + ProgressText();
        if (training) text = "KARATE PRACTICE: " + strikes + "/10  •  " + Mathf.CeilToInt(Mathf.Max(0, trainingEnds - Time.unscaledTime)) + "s";
        else if (OnMat() && Time.unscaledTime >= alertEnds) text += "  •  Press K";
        GUI.Label(rect, text, style);
    }
    internal static void Shutdown() { RemoveSceneObjects(); saveKey = null; enabled = false; training = false; }
}

internal sealed class KarateDojoBuilding : MonoBehaviour
{
    private readonly List<Material> materials = new();
    internal static GameObject Create(Vector3 position, Quaternion rotation)
    {
        var root = new GameObject("Paradise Island Karate Dojo"); root.transform.SetPositionAndRotation(position, rotation);
        root.AddComponent<KarateDojoBuilding>().Build(); return root;
    }
    private void Build()
    {
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        var wood = Mat(shader, new Color(0.40f, 0.20f, 0.09f));
        var walls = Mat(shader, new Color(0.86f, 0.80f, 0.64f));
        var roof = Mat(shader, new Color(0.18f, 0.15f, 0.14f));
        var mat = Mat(shader, new Color(0.78f, 0.12f, 0.13f));
        var trim = Mat(shader, new Color(0.12f, 0.17f, 0.24f));
        Part("Dojo floor", new Vector3(0, 0.1f, 0), new Vector3(12, 0.2f, 11), wood, true);
        Part("Training mat", new Vector3(0, 0.23f, 0), new Vector3(7.2f, 0.08f, 6.4f), mat, true);
        Part("Mat center", new Vector3(0, 0.28f, 0), new Vector3(2.2f, 0.025f, 2.2f), trim, false);
        Part("Left wall", new Vector3(-5.9f, 2.2f, 0), new Vector3(0.2f, 4.2f, 11), walls, true);
        Part("Right wall", new Vector3(5.9f, 2.2f, 0), new Vector3(0.2f, 4.2f, 11), walls, true);
        Part("Back wall", new Vector3(0, 2.2f, 5.4f), new Vector3(12, 4.2f, 0.2f), walls, true);
        Part("Front left", new Vector3(-4.1f, 2.2f, -5.4f), new Vector3(3.8f, 4.2f, 0.2f), walls, true);
        Part("Front right", new Vector3(4.1f, 2.2f, -5.4f), new Vector3(3.8f, 4.2f, 0.2f), walls, true);
        Part("Door lintel", new Vector3(0, 3.8f, -5.4f), new Vector3(4.5f, 1f, 0.25f), wood, true);
        Part("Roof", new Vector3(0, 4.45f, 0), new Vector3(12.8f, 0.5f, 11.8f), roof, true);
        foreach (var x in new[] { -5.6f, 5.6f }) foreach (var z in new[] { -5.2f, 5.2f })
            Part("Wooden pillar", new Vector3(x, 2.2f, z), new Vector3(0.4f, 4.3f, 0.4f), wood, true);
        Part("Training post", new Vector3(4.5f, 1.2f, 3.5f), new Vector3(0.5f, 2.3f, 0.5f), wood, true);
        Part("Training post arms", new Vector3(4.5f, 1.65f, 3.5f), new Vector3(1.4f, 0.2f, 0.2f), wood, false);
    }
    private Material Mat(Shader shader, Color color) { if (!shader) return null; var result = new Material(shader) { color = color }; materials.Add(result); return result; }
    private void Part(string name, Vector3 position, Vector3 scale, Material material, bool collision)
    {
        var part = GameObject.CreatePrimitive(PrimitiveType.Cube); part.name = name;
        part.transform.SetParent(transform, false); part.transform.localPosition = position; part.transform.localScale = scale;
        if (material) part.GetComponent<Renderer>().sharedMaterial = material;
        if (!collision) { var collider = part.GetComponent<Collider>(); if (collider) { collider.enabled = false; Destroy(collider); } }
    }
    private void OnDestroy() { foreach (var material in materials) if (material) Destroy(material); materials.Clear(); }
}

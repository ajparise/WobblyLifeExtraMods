using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace WobblyLifeExtraMods;

public sealed class MinecraftMusicPlayerMod : BaseMod
{
    private sealed class Track { public string Name; public string Path; }
    private static readonly List<Track> Tracks = new();
    private static readonly Ref<string> Status = new("Press F11 to open the music player.");
    private static AudioSource source;
    private static AudioClip clip;
    private static bool visible;
    private static bool previousCursor;
    private static CursorLockMode previousLock;
    private static Rect window = new(80, 60, 520, 600);
    private static Vector2 scroll;
    private static string search = "";
    private static string currentName = "";
    private static int loadId;
    private static float volume = 0.7f;
    public override string Name => "Minecraft Music Player";
    public override string Description => "Play locally installed Minecraft Java music and music discs with F11.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("MinecraftMusicHelp", "Press F11 to open or close the player. It reads music and records from your local Minecraft Java asset indexes; missing tracks require downloading the relevant Minecraft version. Search for a song, play, pause, stop, and adjust volume."),
        new HStack("MinecraftMusicButtons", ActionMenu(new Button("Open player", Open), nameof(Open)), ActionMenu(new Button("Rescan library", Scan), nameof(Scan))).WithContentWidth(),
        new TextWrapped("MinecraftMusicStatus", "").WithText(Status));
    public override void Update()
    {
        if (Input.GetKeyDown(KeyCode.F11)) { if (visible) Close(); else Open(); }
        if (visible) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
    }
    [ModAction(ShowInUI = false)] public static void Open()
    {
        if (visible) return;
        previousCursor = Cursor.visible; previousLock = Cursor.lockState;
        visible = true; Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
        Scan();
    }
    private static void Close() { if (!visible) return; visible = false; Cursor.visible = previousCursor; Cursor.lockState = previousLock; }
    [ModAction(ShowInUI = false)] public static void Scan()
    {
        Tracks.Clear();
        try
        {
            var assets = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "assets");
            var indexes = Path.Combine(assets, "indexes");
            if (!Directory.Exists(indexes)) { Status.Value = "Minecraft Java asset indexes were not found."; return; }
            var found = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
            foreach (var index in Directory.GetFiles(indexes, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var objects = JObject.Parse(File.ReadAllText(index))["objects"] as JObject;
                    if (objects == null) continue;
                    foreach (var item in objects.Properties())
                    {
                        var key = item.Name;
                        if (!key.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) ||
                            !(key.StartsWith("minecraft/sounds/music/", StringComparison.OrdinalIgnoreCase) || key.StartsWith("minecraft/sounds/records/", StringComparison.OrdinalIgnoreCase))) continue;
                        var hash = (string)item.Value["hash"];
                        if (hash == null || hash.Length < 2) continue;
                        var path = Path.Combine(assets, "objects", hash.Substring(0, 2), hash);
                        if (!File.Exists(path)) continue;
                        var name = Path.GetFileNameWithoutExtension(key).Replace('_', ' ');
                        var category = key.StartsWith("minecraft/sounds/records/", StringComparison.OrdinalIgnoreCase) ? "Disc" : "Music";
                        found[key] = new Track { Name = name + " (" + category + ")", Path = path };
                    }
                }
                catch (Exception exception) { Plugin.Log?.LogWarning("Music index skipped: " + exception.Message); }
            }
            Tracks.AddRange(found.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase));
            Status.Value = $"Found {Tracks.Count} installed Minecraft tracks. Missing assets are not downloaded.";
        }
        catch (Exception exception) { Status.Value = "Could not read Minecraft music: " + exception.Message; }
    }
    internal static void DrawPlayer()
    {
        if (!visible) return;
        window = GUI.Window(823721, window, DrawWindow, "Minecraft Music Player — F11 closes");
    }
    private static void DrawWindow(int id)
    {
        GUILayout.Label(string.IsNullOrEmpty(currentName) ? "Choose a track" : "Now playing: " + currentName);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Pause / Resume", GUILayout.Width(120))) { if (source && source.isPlaying) source.Pause(); else if (source && clip) source.UnPause(); }
        if (GUILayout.Button("Stop", GUILayout.Width(70))) Stop();
        if (GUILayout.Button("Rescan", GUILayout.Width(70))) Scan();
        GUILayout.Label("Volume", GUILayout.Width(50));
        volume = GUILayout.HorizontalSlider(volume, 0f, 1f, GUILayout.Width(90));
        if (source) source.volume = volume;
        GUILayout.EndHorizontal();
        GUILayout.Label(Status.Value);
        GUILayout.BeginHorizontal(); GUILayout.Label("Search:", GUILayout.Width(55)); search = GUILayout.TextField(search ?? ""); GUILayout.EndHorizontal();
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(430));
        foreach (var track in Tracks)
            if (string.IsNullOrEmpty(search) || track.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                if (GUILayout.Button(track.Name)) Play(track);
        GUILayout.EndScrollView();
        if (GUILayout.Button("Close")) Close();
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }
    private static void Play(Track track)
    {
        Stop();
        var requestId = ++loadId;
        Status.Value = "Loading " + track.Name + "...";
        Plugin.RunCoroutine(Load(track, requestId));
    }
    private static IEnumerator Load(Track track, int requestId)
    {
        using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(track.Path).AbsoluteUri, AudioType.OGGVORBIS))
        {
            yield return request.SendWebRequest();
            if (requestId != loadId) yield break;
            if (request.result != UnityWebRequest.Result.Success) { Status.Value = "Could not load track: " + request.error; yield break; }
            clip = DownloadHandlerAudioClip.GetContent(request);
            if (!clip) { Status.Value = "Could not decode the selected track."; yield break; }
            if (!source) { var root = new GameObject("Minecraft Music Player"); UnityEngine.Object.DontDestroyOnLoad(root); source = root.AddComponent<AudioSource>(); source.playOnAwake = false; source.spatialBlend = 0f; }
            source.clip = clip; source.volume = volume; source.Play(); currentName = track.Name;
            Status.Value = "Playing " + track.Name;
        }
    }
    private static void Stop()
    {
        loadId++;
        if (source) { source.Stop(); source.clip = null; }
        if (clip) UnityEngine.Object.Destroy(clip);
        clip = null; currentName = ""; Status.Value = "Playback stopped.";
    }
    internal static void Shutdown() { Close(); Stop(); if (source) UnityEngine.Object.Destroy(source.gameObject); source = null; }
}

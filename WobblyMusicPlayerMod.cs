using System;
using System.IO;
using System.Runtime.InteropServices;
using FMODUnity;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class WobblyMusicPlayerMod : BaseMod
{
    private static readonly Ref<string> Status = new("Press 6 to open the Wobbly music player.");
    private static readonly string[] Names = { "Wobbly Dubstep", "Wobbly Heavy Metal", "Wobbly Rock" };
    private static readonly AudioClip[] Clips = new AudioClip[3];
    private static FMOD.Sound sound;
    private static FMOD.Channel channel;
    private static bool visible;
    private static bool cursorVisible;
    private static CursorLockMode cursorLock;
    private static Rect window = new(100, 80, 430, 310);
    private static float volume = 0.8f;
    private static int current = -1;
    private static bool paused;
    public override string Name => "Wobbly Music Player";
    public override string Description => "Play three original Wobbly music loops. Press 6 to open.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("WobblyMusicHelp", "Press 6 in a world to open the player. Press 7 to play heavy metal directly. Choose an original dubstep, heavy metal or rock loop, adjust the volume, pause or stop. Close with 6 or the close button."),
        new HStack("WobblyMusicActions", ActionMenu(new Button("Open music player", Open), nameof(Open)), ActionMenu(new Button("Stop music", Stop), nameof(Stop))).WithContentWidth(),
        new TextWrapped("WobblyMusicStatus", "").WithText(Status));

    public override void Update()
    {
        if (GameInstance.InstanceExists && Input.GetKeyDown(KeyCode.Alpha6) && (visible || !Cursor.visible))
        { if (visible) Close(); else Open(); }
        if (GameInstance.InstanceExists && Input.GetKeyDown(KeyCode.Alpha7) && (visible || !Cursor.visible)) Play(1);
        if (visible) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        if (!GameInstance.InstanceExists && (visible || current >= 0)) { Close(); Stop(); }
    }
    [ModAction(ShowInUI = false)] public static void Open()
    {
        if (visible) return;
        if (!GameInstance.InstanceExists) { Status.Value = "Enter a world first."; return; }
        cursorVisible = Cursor.visible; cursorLock = Cursor.lockState;
        visible = true; Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
    }
    private static void Close() { if (!visible) return; visible = false; Cursor.visible = cursorVisible; Cursor.lockState = cursorLock; }
    [ModAction(ShowInUI = false)] public static void Stop()
    {
        if (channel.hasHandle()) { channel.stop(); channel.clearHandle(); }
        if (sound.hasHandle()) { sound.release(); sound.clearHandle(); }
        current = -1; paused = false; Status.Value = "Music stopped.";
    }
    private static void Play(int index)
    {
        Stop();
        try
        {
            if (!Clips[index]) Clips[index] = MakeTrack(index);
            var bytes = EncodeWave(Clips[index]);
            var info = new FMOD.CREATESOUNDEXINFO { cbsize = Marshal.SizeOf(typeof(FMOD.CREATESOUNDEXINFO)), length = (uint)bytes.Length };
            var result = RuntimeManager.CoreSystem.createSound(bytes, FMOD.MODE.OPENMEMORY | FMOD.MODE.CREATESAMPLE | FMOD.MODE._2D | FMOD.MODE.LOOP_NORMAL, ref info, out sound);
            if (result != FMOD.RESULT.OK) throw new InvalidOperationException("createSound: " + result);
            result = RuntimeManager.CoreSystem.playSound(sound, default(FMOD.ChannelGroup), true, out channel);
            if (result != FMOD.RESULT.OK) throw new InvalidOperationException("playSound: " + result);
            result = channel.setVolume(volume);
            if (result != FMOD.RESULT.OK) throw new InvalidOperationException("volume: " + result);
            result = channel.setPaused(false);
            if (result != FMOD.RESULT.OK) throw new InvalidOperationException("start: " + result);
            current = index; paused = false; Status.Value = "Playing " + Names[index] + ".";
            Plugin.Log?.LogInfo("Wobbly music playing through FMOD: " + Names[index]);
        }
        catch (Exception exception)
        {
            Stop(); Status.Value = "Audio error: " + exception.Message;
            Plugin.Log?.LogWarning("Wobbly music playback failed: " + exception);
        }
    }
    internal static void DrawPlayer()
    {
        if (visible) window = GUI.Window(823722, window, DrawWindow, "Wobbly Music Player — 6 closes");
    }
    private static void DrawWindow(int id)
    {
        GUILayout.Label(current >= 0 ? (paused ? "Paused: " : "Playing: ") + Names[current] : "Choose a track:");
        for (var index = 0; index < Names.Length; index++) if (GUILayout.Button(Names[index], GUILayout.Height(30))) Play(index);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button(paused ? "Resume" : "Pause") && channel.hasHandle() && current >= 0)
        { paused = !paused; channel.setPaused(paused); }
        if (GUILayout.Button("Stop")) Stop();
        GUILayout.EndHorizontal();
        GUILayout.BeginHorizontal(); GUILayout.Label("Volume", GUILayout.Width(60));
        volume = GUILayout.HorizontalSlider(volume, 0f, 1f); if (channel.hasHandle()) channel.setVolume(volume);
        GUILayout.EndHorizontal();
        GUILayout.Label(Status.Value);
        GUILayout.Label("Original instrumental loops — they repeat until stopped.");
        if (GUILayout.Button("Close")) Close();
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }

    private static AudioClip MakeTrack(int style)
    {
        const int sampleRate = 22050;
        const int seconds = 8;
        var samples = new float[sampleRate * seconds];
        var random = new System.Random(1729 + style);
        var notes = style == 0 ? new[] { 55f, 55f, 65.4f, 49f, 55f, 73.4f, 65.4f, 49f }
            : style == 1 ? new[] { 82.4f, 82.4f, 98f, 82.4f, 110f, 103.8f, 98f, 73.4f }
            : new[] { 110f, 138.6f, 146.8f, 164.8f, 146.8f, 138.6f, 110f, 98f };
        for (var i = 0; i < samples.Length; i++)
        {
            var t = (double)i / sampleRate;
            var beat = t * 2.0;
            var beatPhase = beat - Math.Floor(beat);
            var beatNumber = (int)beat;
            var note = notes[(beatNumber / 2) % notes.Length];
            var kick = Math.Sin(2 * Math.PI * (52 * t - 12 * Math.Exp(-beatPhase * 20))) * Math.Exp(-beatPhase * 16) * 0.7;
            var noise = random.NextDouble() * 2 - 1;
            var snare = beatNumber % 4 == 2 ? noise * Math.Exp(-beatPhase * 18) * 0.3 : 0;
            var hatPhase = beat * 2 - Math.Floor(beat * 2);
            var hat = noise * Math.Exp(-hatPhase * 28) * 0.06;
            var envelope = Math.Exp(-beatPhase * (style == 1 ? 1.8 : 1.0));
            double instrument;
            if (style == 0)
            {
                var wobble = 0.35 + 0.65 * Math.Abs(Math.Sin(2 * Math.PI * t * 2));
                instrument = Math.Tanh((Math.Sin(2 * Math.PI * note * t) + 0.55 * Math.Sin(2 * Math.PI * note * 2 * t)) * wobble * 3) * 0.28;
            }
            else
            {
                var raw = Math.Sin(2 * Math.PI * note * t) + 0.5 * Math.Sin(2 * Math.PI * note * 1.5 * t) + 0.3 * Math.Sin(2 * Math.PI * note * 2 * t);
                instrument = Math.Tanh(raw * (style == 1 ? 5 : 2)) * envelope * (style == 1 ? 0.3 : 0.24);
            }
            samples[i] = (float)Math.Max(-0.95, Math.Min(0.95, kick + snare + hat + instrument));
        }
        var result = AudioClip.Create(Names[style], samples.Length, 1, sampleRate, false);
        result.SetData(samples, 0);
        return result;
    }
    private static byte[] EncodeWave(AudioClip audio)
    {
        var data = new float[audio.samples];
        if (!audio.GetData(data, 0)) throw new InvalidOperationException("Could not read generated audio.");
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { 'R', 'I', 'F', 'F' }); writer.Write(36 + data.Length * 2);
            writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(audio.frequency);
            writer.Write(audio.frequency * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' }); writer.Write(data.Length * 2);
            foreach (var sample in data) writer.Write((short)(Mathf.Clamp(sample, -1f, 1f) * 32767f));
            writer.Flush(); return stream.ToArray();
        }
    }
    internal static void Shutdown()
    {
        Close(); Stop();
        for (var i = 0; i < Clips.Length; i++) { if (Clips[i]) UnityEngine.Object.Destroy(Clips[i]); Clips[i] = null; }
    }
}

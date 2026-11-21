using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FMODUnity;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class SurvivalNeedsMod : BaseMod
{
    private sealed class PoopEntry { public GameObject Object; public float Expires; }
    private static readonly Ref<string> Status = new("Survival mode is off.");
    private static readonly List<PoopEntry> Poops = new();
    private static bool enabled;
    private static float hunger = 100f;
    private static float thirst = 100f;
    private static float bathroom;
    private static float nextGurgle;
    private static float nextUse;
    private static FMOD.Sound gurgleSound;
    private static FMOD.Channel gurgleChannel;
    private static Material poopMaterial;
    public override string Name => "Survival: Hunger, Thirst & Bathroom";
    public override string Description => "Manage hunger, thirst and bathroom needs while exploring.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 1f, Max = 30f, Label = "Hunger loss per minute")]
    public static Ref<float> HungerRate = new(5f);
    [ModSetting(Order = 20, Min = 1f, Max = 30f, Label = "Thirst loss per minute")]
    public static Ref<float> ThirstRate = new(7f);
    [ModSetting(Order = 30, Min = 1f, Max = 30f, Label = "Bathroom gain per minute")]
    public static Ref<float> BathroomRate = new(12f);
    [ModSetting(Order = 40, Min = 30f, Max = 600f, Label = "Poop lifetime (seconds)")]
    public static Ref<float> PoopLifetime = new(180f);

    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("SurvivalHelp", "Enable in a world. F5 eats a survival snack, F6 drinks from your refillable survival bottle, and F10 uses a nearby toilet. Sit on a toilet to use it automatically. The bathroom meter fills over time; when urgent your stomach gurgles, and at 100 you have an accident. Snacks and water are unlimited and do not consume game items. Poop is local temporary physics, not a saved or networked prop. Empty hunger/thirst show a warning."),
        base.BuildPanel(id),
        new HStack("SurvivalActions", ActionMenu(new Button("Enable survival", Enable), nameof(Enable)),
            ActionMenu(new Button("Disable and clean up", Disable), nameof(Disable)),
            ActionMenu(new Button("Eat snack", Eat), nameof(Eat)), ActionMenu(new Button("Drink water", Drink), nameof(Drink)),
            ActionMenu(new Button("Use nearby toilet", UseToilet), nameof(UseToilet))).WithContentWidth(),
        new TextWrapped("SurvivalStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)] public static void Enable()
    {
        if (!Character()) { Status.Value = "Enter a world before enabling survival."; return; }
        enabled = true; hunger = 100f; thirst = 100f; bathroom = 0f; nextGurgle = 0f; nextUse = 0f;
        Status.Value = "Survival enabled: F5 snack, F6 water, F10 near a toilet.";
    }
    [ModAction(ShowInUI = false)] public static void Disable()
    {
        enabled = false;
        foreach (var poop in Poops) if (poop.Object) UnityEngine.Object.Destroy(poop.Object);
        Poops.Clear();
        if (gurgleChannel.hasHandle()) { gurgleChannel.stop(); gurgleChannel.clearHandle(); }
        if (gurgleSound.hasHandle()) { gurgleSound.release(); gurgleSound.clearHandle(); }
        if (poopMaterial) UnityEngine.Object.Destroy(poopMaterial);
        poopMaterial = null;
        Status.Value = "Survival off; temporary poop cleaned up.";
    }
    [ModAction(ShowInUI = false)] public static void Eat()
    {
        if (!enabled || !Character()) { Status.Value = "Enable survival in a world first."; return; }
        hunger = Mathf.Min(100f, hunger + 30f); bathroom = Mathf.Min(100f, bathroom + 3f);
        Status.Value = "Ate a survival snack.";
    }
    [ModAction(ShowInUI = false)] public static void Drink()
    {
        if (!enabled || !Character()) { Status.Value = "Enable survival in a world first."; return; }
        thirst = Mathf.Min(100f, thirst + 35f); bathroom = Mathf.Min(100f, bathroom + 2f);
        Status.Value = "Drank from the survival bottle.";
    }
    [ModAction(ShowInUI = false)] public static void UseToilet()
    {
        var character = Character();
        if (!enabled || !character) { Status.Value = "Enable survival in a world first."; return; }
        if (!AtToilet(character)) { Status.Value = "Move closer to a toilet or sit on one."; return; }
        bathroom = 0f; nextGurgle = Time.unscaledTime + 20f; nextUse = Time.unscaledTime + 3f;
        Status.Value = "Used the toilet. Bathroom need cleared!";
    }
    public override void Update()
    {
        if (!enabled) return;
        var character = Character();
        if (!GameInstance.InstanceExists) { Disable(); return; }
        if (!character) return;
        var elapsed = Mathf.Min(Time.unscaledDeltaTime, 1f) / 60f;
        hunger = Mathf.Max(0f, hunger - Mathf.Clamp(HungerRate.Value, 1f, 30f) * elapsed);
        thirst = Mathf.Max(0f, thirst - Mathf.Clamp(ThirstRate.Value, 1f, 30f) * elapsed);
        bathroom = Mathf.Min(100f, bathroom + Mathf.Clamp(BathroomRate.Value, 1f, 30f) * elapsed);
        for (var i = Poops.Count - 1; i >= 0; i--)
            if (!Poops[i].Object || Time.unscaledTime >= Poops[i].Expires)
            { if (Poops[i].Object) UnityEngine.Object.Destroy(Poops[i].Object); Poops.RemoveAt(i); }

        var controller = character.GetPlayerController();
        var entered = controller ? controller.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject() : null;
        if (bathroom > 0f && Time.unscaledTime >= nextUse && entered && entered.GetComponentInParent<Toilet>()) UseToilet();
        if (!Cursor.visible)
        {
            if (Input.GetKeyDown(KeyCode.F5)) Eat();
            if (Input.GetKeyDown(KeyCode.F6)) Drink();
            if (Input.GetKeyDown(KeyCode.F10)) UseToilet();
        }
        if (bathroom >= 100f)
        {
            CreateAccident(character); bathroom = 0f; nextGurgle = Time.unscaledTime + 20f;
            Status.Value = "Oops! You had an accident.";
        }
        else if (bathroom >= 70f && Time.unscaledTime >= nextGurgle)
        {
            nextGurgle = Time.unscaledTime + 25f; PlayGurgle();
            Status.Value = "Your stomach is gurgling! Find a toilet.";
        }
    }
    private static PlayerCharacter Character() => GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter() : null;
    private static bool AtToilet(PlayerCharacter character)
    {
        var entered = character.GetPlayerController()?.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
        if (entered && entered.GetComponentInParent<Toilet>()) return true;
        foreach (var toilet in UnityEngine.Object.FindObjectsOfType<Toilet>())
            if (toilet && (toilet.transform.position - character.transform.position).sqrMagnitude <= 16f) return true;
        return false;
    }
    private static void CreateAccident(PlayerCharacter character)
    {
        while (Poops.Count >= 20) { if (Poops[0].Object) UnityEngine.Object.Destroy(Poops[0].Object); Poops.RemoveAt(0); }
        var root = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        root.name = "Survival temporary poop";
        var position = character.transform.position + Vector3.up * 0.4f;
        if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out var hit, 5f, ~0, QueryTriggerInteraction.Ignore) && !hit.transform.IsChildOf(character.transform)) position = hit.point + Vector3.up * 0.25f;
        root.transform.position = position;
        root.transform.localScale = new Vector3(0.48f, 0.30f, 0.48f);
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        if (!poopMaterial && shader) { poopMaterial = new Material(shader); poopMaterial.color = new Color(0.32f, 0.14f, 0.045f); }
        if (poopMaterial) root.GetComponent<Renderer>().sharedMaterial = poopMaterial;
        var body = root.AddComponent<Rigidbody>(); body.mass = 0.7f;
        body.AddForce((character.transform.forward * -1f + Vector3.up) * 1.5f, ForceMode.VelocityChange);
        Poops.Add(new PoopEntry { Object = root, Expires = Time.unscaledTime + Mathf.Clamp(PoopLifetime.Value, 30f, 600f) });
    }
    private static void PlayGurgle()
    {
        try
        {
            if (!gurgleSound.hasHandle())
            {
                var bytes = CreateGurgleWave();
                var info = new FMOD.CREATESOUNDEXINFO { cbsize = Marshal.SizeOf(typeof(FMOD.CREATESOUNDEXINFO)), length = (uint)bytes.Length };
                var created = RuntimeManager.CoreSystem.createSound(bytes, FMOD.MODE.OPENMEMORY | FMOD.MODE.CREATESAMPLE | FMOD.MODE._2D, ref info, out gurgleSound);
                if (created != FMOD.RESULT.OK) throw new InvalidOperationException("createSound: " + created);
            }
            var result = RuntimeManager.CoreSystem.playSound(gurgleSound, default(FMOD.ChannelGroup), false, out gurgleChannel);
            if (result != FMOD.RESULT.OK) throw new InvalidOperationException("playSound: " + result);
            gurgleChannel.setVolume(0.9f);
        }
        catch (Exception exception) { Plugin.Log?.LogWarning("Survival gurgle failed: " + exception.Message); }
    }
    private static byte[] CreateGurgleWave()
    {
        const int rate = 22050; const int count = rate * 2;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { 'R', 'I', 'F', 'F' }); writer.Write(36 + count * 2);
            writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' }); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' }); writer.Write(count * 2);
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / rate;
                var envelope = Math.Sin(Math.PI * t / 2) * (0.45 + 0.55 * Math.Abs(Math.Sin(2 * Math.PI * 3.2 * t)));
                var sound = (Math.Sin(2 * Math.PI * (110 * t + 8 * Math.Sin(t * 7))) + 0.45 * Math.Sin(2 * Math.PI * (173 * t + 5 * Math.Sin(t * 9)))) * envelope * 0.3;
                writer.Write((short)(Math.Max(-1, Math.Min(1, sound)) * 32767));
            }
            writer.Flush(); return stream.ToArray();
        }
    }
    internal static void DrawOverlay()
    {
        if (!enabled || !GameInstance.InstanceExists || Event.current.type != EventType.Repaint) return;
        var box = new Rect(18f, 130f, 230f, 112f);
        var old = GUI.color; GUI.color = new Color(0, 0, 0, 0.75f); GUI.DrawTexture(box, Texture2D.whiteTexture); GUI.color = old;
        DrawMeter(box.x + 10, box.y + 8, "HUNGER", hunger, hunger <= 20f ? Color.red : new Color(1f, 0.65f, 0.18f));
        DrawMeter(box.x + 10, box.y + 40, "THIRST", thirst, thirst <= 20f ? Color.red : new Color(0.2f, 0.7f, 1f));
        DrawMeter(box.x + 10, box.y + 72, "BATHROOM", bathroom, bathroom >= 70 ? Color.red : new Color(0.7f, 0.45f, 0.2f));
    }
    private static void DrawMeter(float x, float y, string label, float value, Color color)
    {
        var old = GUI.color; GUI.color = Color.gray; GUI.DrawTexture(new Rect(x + 92, y + 5, 118, 15), Texture2D.whiteTexture);
        GUI.color = color; GUI.DrawTexture(new Rect(x + 92, y + 5, 118 * Mathf.Clamp01(value / 100f), 15), Texture2D.whiteTexture); GUI.color = old;
        GUI.Label(new Rect(x, y, 95, 25), label); GUI.Label(new Rect(x + 95, y, 120, 25), Mathf.RoundToInt(value) + "%");
    }
    internal static void Shutdown() => Disable();
}

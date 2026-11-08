using System.Collections.Generic;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class GiantWobblyMod : BaseMod
{
    private static readonly Ref<string> Status = new("Giant Wobbly is off.");
    private static Transform scaled;
    private static PlayerCharacter activeCharacter;
    private static GiantScaleDriver scaleDriver;
    private static Vector3 originalScale;
    private static float nextStomp;
    public override string Name => "Giant Wobbly";
    public override string Description => "Grow your Wobbly and stomp nearby physics objects.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 1.5f, Max = 6f, Label = "Giant size")]
    public static Ref<float> GiantSize = new(3f);

    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("GiantHelp", "Turn on Giant Wobbly in a world. Press G to stomp and push nearby physics objects. Turn it off to restore your original size. Stomping requires the host or offline player."),
        base.BuildPanel(id),
        new HStack("GiantActions", ActionMenu(new Button("Become giant", Enable), nameof(Enable)),
            ActionMenu(new Button("Return to normal", Disable), nameof(Disable))).WithContentWidth(),
        new TextWrapped("GiantStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)] public static void Enable()
    {
        Disable();
        var player = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var character = player ? player.GetPlayerCharacter() : null;
        if (!character) { Status.Value = "Enter a world first."; return; }
        var body = character.GetPlayerBody();
        if (!body) { Status.Value = "Your Wobbly body is not ready yet; try again."; return; }
        activeCharacter = character;
        scaled = body.transform;
        originalScale = scaled.localScale;
        scaleDriver = scaled.gameObject.AddComponent<GiantScaleDriver>();
        scaleDriver.Initialize(originalScale * Mathf.Clamp(GiantSize.Value, 1.5f, 6f));
        Status.Value = "Giant Wobbly active. Press G to stomp.";
    }

    [ModAction(ShowInUI = false)] public static void Disable()
    {
        if (scaleDriver) { scaleDriver.enabled = false; Object.Destroy(scaleDriver); }
        scaleDriver = null;
        if (scaled) scaled.localScale = originalScale;
        scaled = null;
        activeCharacter = null;
        Status.Value = "Giant Wobbly off; size restored.";
    }

    public override void Update()
    {
        if (!scaled) return;
        if (!GameInstance.InstanceExists) { Disable(); return; }
        var player = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = player ? player.GetPlayerCharacter() : null;
        if (!character || character != activeCharacter) { Disable(); return; }
        if (Cursor.visible || !Input.GetKeyDown(KeyCode.G) || Time.unscaledTime < nextStomp) return;
        if (!PropSpawnManager.IsServer) { Status.Value = "Only the host or offline player can stomp."; return; }
        nextStomp = Time.unscaledTime + 1f;
        var origin = scaled.position;
        var bodies = new HashSet<Rigidbody>();
        foreach (var hit in Physics.OverlapSphere(origin, 12f, ~0, QueryTriggerInteraction.Ignore))
        {
            var body = hit.attachedRigidbody;
            if (!body || body.isKinematic || body.transform.IsChildOf(character.transform) || !bodies.Add(body)) continue;
            body.AddExplosionForce(20f, origin, 12f, 0.8f, ForceMode.VelocityChange);
        }
        Status.Value = "STOMP!";
    }

    internal static void Shutdown() => Disable();
}

public sealed class GiantScaleDriver : MonoBehaviour
{
    private Vector3 targetScale;
    public void Initialize(Vector3 scale) { targetScale = scale; transform.localScale = scale; }
    private void LateUpdate() => transform.localScale = targetScale;
}

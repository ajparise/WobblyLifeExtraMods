using System.Collections.Generic;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class DisasterRemoteMod : BaseMod
{
    private static readonly Ref<string> Status = new("Choose a disaster to equip.");
    private static bool earthquakeEquipped;
    public override string Name => "Disaster Remote";
    public override string Description => "Choose a tornado, tsunami, meteor storm or earthquake.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("DisasterHelp", "Choose a disaster, close F2 and left-click. The tornado, tsunami and meteor options use your existing disaster guns. Earthquake shakes movable physics objects near the aimed location. Host or offline only."),
        new HStack("DisasterChoices", ActionMenu(new Button("Tornado", Tornado), nameof(Tornado)),
            ActionMenu(new Button("Tsunami", Tsunami), nameof(Tsunami)),
            ActionMenu(new Button("Meteor storm", Meteor), nameof(Meteor)),
            ActionMenu(new Button("Earthquake", Earthquake), nameof(Earthquake)),
            ActionMenu(new Button("Put away", PutAway), nameof(PutAway))).WithContentWidth(),
        new TextWrapped("DisasterStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)] public static void Tornado() { PutAway(); TornadoGunMod.Equip(); Status.Value = "Tornado selected; left-click to fire."; }
    [ModAction(ShowInUI = false)] public static void Tsunami() { PutAway(); TsunamiGunMod.Equip(); Status.Value = "Tsunami selected; left-click to fire."; }
    [ModAction(ShowInUI = false)] public static void Meteor() { PutAway(); MeteorShowerCannonMod.Equip(); Status.Value = "Meteor storm selected; left-click to fire."; }
    [ModAction(ShowInUI = false)] public static void Earthquake() { PutAway(); earthquakeEquipped = true; Status.Value = "Earthquake selected; left-click a location."; }
    [ModAction(ShowInUI = false)] public static void PutAway()
    {
        earthquakeEquipped = false;
        TornadoGunMod.Unequip(); TsunamiGunMod.Unequip(); MeteorShowerCannonMod.Unequip();
        Status.Value = "Disaster remote put away.";
    }

    public override void Update()
    {
        if (!earthquakeEquipped) return;
        if (!GameInstance.InstanceExists) { earthquakeEquipped = false; return; }
        if (Cursor.visible || !Input.GetMouseButtonDown(0)) return;
        if (!PropSpawnManager.IsServer) { Status.Value = "Only the host or offline player can create earthquakes."; return; }
        var camera = Camera.main;
        if (!camera) return;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
        var center = Physics.Raycast(ray, out var hit, 200f) ? hit.point : ray.GetPoint(40f);
        var affected = new HashSet<Rigidbody>();
        foreach (var collider in Physics.OverlapSphere(center, 22f, ~0, QueryTriggerInteraction.Ignore))
        {
            var body = collider.attachedRigidbody;
            if (!body || body.isKinematic || !affected.Add(body)) continue;
            var direction = new Vector3(Random.Range(-1f, 1f), Random.Range(0.4f, 1f), Random.Range(-1f, 1f)).normalized;
            body.AddForce(direction * Random.Range(8f, 18f), ForceMode.VelocityChange);
        }
        Status.Value = $"Earthquake shook {affected.Count} physics objects.";
    }
}

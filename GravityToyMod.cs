using System.Collections.Generic;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class GravityToyMod : BaseMod
{
    private static readonly Ref<string> Status = new("Gravity toys are off.");
    private static readonly Dictionary<Rigidbody, bool> Floating = new();
    private static bool equipped;
    private static bool blackHole;
    private static Vector3 holePosition;
    private static float holeEnds;
    private static GameObject holeVisual;
    public override string Name => "Gravity Swap & Black Hole";
    public override string Description => "Toggle floating props or fire a temporary black hole.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("GravityHelp", "Equip, close F2 and left-click. Gravity Swap makes a physics object float; shoot it again to drop it. Black Hole pulls nearby physics objects in, then blasts them outward. Host or offline only. Restore gravity when finished."),
        new HStack("GravityActions", ActionMenu(new Button("Equip gravity swap", EquipGravity), nameof(EquipGravity)),
            ActionMenu(new Button("Equip black hole", EquipHole), nameof(EquipHole)),
            ActionMenu(new Button("Unequip and restore", Restore), nameof(Restore))).WithContentWidth(),
        new TextWrapped("GravityStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)] public static void EquipGravity() { equipped = true; blackHole = false; Status.Value = "Gravity Swap equipped. Left-click a movable object."; }
    [ModAction(ShowInUI = false)] public static void EquipHole() { equipped = true; blackHole = true; Status.Value = "Black Hole equipped. Left-click to fire."; }
    [ModAction(ShowInUI = false)] public static void Restore()
    {
        equipped = false;
        foreach (var pair in Floating) if (pair.Key) pair.Key.useGravity = pair.Value;
        Floating.Clear();
        if (holeVisual) Object.Destroy(holeVisual);
        holeVisual = null; holeEnds = 0f;
        Status.Value = "Gravity toys off; gravity restored.";
    }

    public override void Update()
    {
        if (!GameInstance.InstanceExists) { if (equipped || Floating.Count > 0 || holeVisual) Restore(); return; }
        if (holeVisual) UpdateHole();
        if (!equipped || Cursor.visible || !Input.GetMouseButtonDown(0)) return;
        if (!PropSpawnManager.IsServer) { Status.Value = "Host or offline player only."; return; }
        var camera = Camera.main;
        if (!camera || !Physics.Raycast(camera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f)), out var hit, 250f)) return;
        if (blackHole)
        {
            if (holeVisual) Object.Destroy(holeVisual);
            holePosition = hit.point;
            holeEnds = Time.unscaledTime + 4f;
            holeVisual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(holeVisual.GetComponent<Collider>());
            holeVisual.transform.position = holePosition;
            holeVisual.transform.localScale = Vector3.one * 1.5f;
            holeVisual.GetComponent<Renderer>().material.color = Color.black;
            Status.Value = "Black hole active!";
            return;
        }
        var body = hit.collider.attachedRigidbody;
        if (!body || body.isKinematic) { Status.Value = "Aim at a movable physics object."; return; }
        if (Floating.TryGetValue(body, out var previous)) { body.useGravity = previous; Floating.Remove(body); Status.Value = "Gravity restored."; }
        else { Floating[body] = body.useGravity; body.useGravity = false; body.AddForce(Vector3.up * 4f, ForceMode.VelocityChange); Status.Value = "Object floating; shoot it again to drop it."; }
    }

    private static void UpdateHole()
    {
        var exploding = Time.unscaledTime >= holeEnds;
        var bodies = new HashSet<Rigidbody>();
        foreach (var collider in Physics.OverlapSphere(holePosition, 14f))
        {
            var body = collider.attachedRigidbody;
            if (!body || body.isKinematic || !bodies.Add(body)) continue;
            if (exploding) body.AddExplosionForce(30f, holePosition, 14f, 0.5f, ForceMode.VelocityChange);
            else body.AddForce((holePosition - body.worldCenterOfMass).normalized * 22f, ForceMode.Acceleration);
        }
        if (exploding) { Object.Destroy(holeVisual); holeVisual = null; Status.Value = "Black hole burst!"; }
    }

    internal static void Shutdown() => Restore();
}

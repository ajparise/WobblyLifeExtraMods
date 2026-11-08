using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class SuperheroRescueMod : BaseMod
{
    private static readonly Ref<string> Status = new("Start a rescue near an NPC Wobbly.");
    private static PlayerNPCController target;
    private static GameObject marker;
    private static bool fireOut;
    public override string Name => "Superhero Rescue Missions";
    public override string Description => "Find a nearby NPC, put out a fire, and rescue them for a reward.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("RescueHelp", "Host/offline: start a mission near an existing NPC Wobbly. Reach the orange fire marker and press E to put it out, then reach the NPC and press E to rescue them. Completing the mission pays $100. The marker is a mission effect, not a spreading game fire."),
        new HStack("RescueActions", ActionMenu(new Button("Start rescue", StartMission), nameof(StartMission)), ActionMenu(new Button("Cancel mission", Cancel), nameof(Cancel))).WithContentWidth(),
        new TextWrapped("RescueStatus", "").WithText(Status));
    [ModAction(ShowInUI = false)] public static void StartMission()
    {
        Cancel();
        if (!PropSpawnManager.IsServer || !GameInstance.InstanceExists) { Status.Value = "Enter a world as host or offline player first."; return; }
        var player = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = player ? player.GetPlayerCharacter() : null;
        if (!character) { Status.Value = "Player not ready."; return; }
        target = Object.FindObjectsOfType<PlayerNPCController>().Where(npc => npc && npc.gameObject.activeInHierarchy)
            .OrderBy(npc => (npc.transform.position - character.transform.position).sqrMagnitude).FirstOrDefault();
        if (!target) { Status.Value = "No NPC found nearby. Find a Wobbly NPC and try again."; return; }
        var point = target.transform.position + target.transform.right * 3f;
        if (Physics.Raycast(point + Vector3.up * 5f, Vector3.down, out var hit, 15f, ~0, QueryTriggerInteraction.Ignore)) point = hit.point;
        marker = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        marker.name = "Rescue mission fire marker";
        Object.Destroy(marker.GetComponent<Collider>());
        marker.transform.position = point + Vector3.up;
        marker.transform.localScale = new Vector3(1.5f, 2f, 1.5f);
        marker.GetComponent<Renderer>().material.color = new Color(1f, 0.28f, 0f);
        fireOut = false;
        Status.Value = "Put out the orange fire marker: approach it and press E.";
    }
    [ModAction(ShowInUI = false)] public static void Cancel()
    {
        if (marker) Object.Destroy(marker);
        marker = null; target = null; fireOut = false;
        Status.Value = "Rescue mission cancelled.";
    }
    public override void Update()
    {
        if (!target) { if (marker) Cancel(); return; }
        if (!GameInstance.InstanceExists) { Cancel(); return; }
        if (Cursor.visible || !Input.GetKeyDown(KeyCode.E)) return;
        var player = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = player ? player.GetPlayerCharacter() : null;
        if (!character) return;
        if (!fireOut)
        {
            if (!marker || Vector3.Distance(character.transform.position, marker.transform.position) > 5f) { Status.Value = "Get closer to the orange fire marker."; return; }
            Object.Destroy(marker); marker = null; fireOut = true;
            Status.Value = "Fire out! Reach the NPC and press E to rescue them.";
            return;
        }
        if (Vector3.Distance(character.transform.position, target.transform.position) > 5f) { Status.Value = "Get closer to the NPC Wobbly."; return; }
        var employment = player.GetComponent<PlayerControllerEmployment>() ?? player.GetComponentInChildren<PlayerControllerEmployment>(true);
        if (!employment) { Status.Value = "Money system not ready; press E again."; return; }
        employment.UpdateMoney(100);
        if (SaveGameManager.InstanceExists) SaveGameManager.InstanceRaw.ForceSaveNextFrame();
        target = null; fireOut = false; Status.Value = "Rescue complete! You earned $100.";
    }
    internal static void Shutdown() => Cancel();
}

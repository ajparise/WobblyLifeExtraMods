using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class SwitchorGunMod : BaseMod
{
    private const string Marker = "[SWITCH:";
    private static readonly Ref<string> Status = new("Switchor is unequipped.");
    private sealed class Target { public PlayerController Player; public Rigidbody Body; public string Name; }
    private sealed class Pending { public PlayerController Player; public string Original; public string Marked; public Vector3 Destination; }
    private static readonly List<Pending> PendingPlayers = new();
    private static readonly Dictionary<PlayerController, string> ProcessedMarkers = new();
    private static bool equipped;
    private static Target first, second;
    private static bool playerWindow;
    private static bool previousCursor;
    private static CursorLockMode previousLock;
    private static Rect window = new(100, 80, 470, 440);
    private static Vector2 scroll;
    private static string search = "";
    private static PlayerController playerOne, playerTwo;
    private static float restoreAt;
    internal static bool IsEquipped => equipped;
    internal static bool BlocksMusicHotkey => equipped || playerWindow;
    public override string Name => "Switchor Gun";
    public override string Description => "Select two things and swap their positions, or search for two players.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;
    protected override void OnStaticInit() => new Harmony("com.aj.wobblylife.extramods.switchor").PatchAll(typeof(SwitchorOtherWeaponsPatch));
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("SwitchorHelp", "Host/offline: equip, close F2, left-click two different players or movable physics objects, then press 6. R clears the selection. For player names, open the player switch window below, search and select two players, then press Enter or Switch. Players must be out of vehicles; every remote player being moved must have this mod installed. Player name tags briefly carry the teleport request."),
        new HStack("SwitchorActions", ActionMenu(new Button("Equip Switchor", Equip), nameof(Equip)),
            ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
            ActionMenu(new Button("Search players", OpenPlayerWindow), nameof(OpenPlayerWindow)),
            ActionMenu(new Button("Clear selections", Clear), nameof(Clear))).WithContentWidth(),
        new TextWrapped("SwitchorStatus", "").WithText(Status));
    [ModAction(ShowInUI = false)] public static void Equip()
    {
        if (!PropSpawnManager.IsServer) { Status.Value = "Only the host or offline player can use Switchor."; return; }
        WindCannonMod.Unequip(); PropSpawnerGunMod.Unequip(); VehicleAircraftSpawnerMod.Unequip(); RocketLauncherMod.Unequip();
        PaintballGunMod.Unequip(); MinecraftBuildingMod.Unequip(); HeavyAutomaticGunMod.Unequip(); GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip(); LaserEyesMod.Unequip(); CamouflagePropHuntMod.Unequip(); LavaGunMod.Unequip();
        ChaosWandMod.Unequip(); PortalGunMod.Unequip(); LightningGunMod.Unequip(); WobblyHeadHomingGunMod.Unequip();
        BananaPeelLauncherMod.Unequip(); TornadoGunMod.Unequip(); FireworkMinigunMod.Unequip(); JellyGunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip(); MosesStaffMod.Unequip(); PowerSwordMod.Unequip(); BubbleBlasterMod.Unequip();
        TsunamiGunMod.Unequip(); MeteorShowerCannonMod.Unequip();
        GravityToyMod.Restore();
        equipped = true; Clear(); Status.Value = "Switchor equipped: select two targets, then press 6.";
    }
    [ModAction(ShowInUI = false)] public static void Unequip() { equipped = false; first = null; second = null; ClosePlayerWindow(); Status.Value = "Switchor unequipped."; }
    [ModAction(ShowInUI = false)] public static void Clear() { first = null; second = null; Status.Value = "Selections cleared."; }
    [ModAction(ShowInUI = false)] public static void OpenPlayerWindow()
    {
        if (!PropSpawnManager.IsServer || !GameInstance.InstanceExists) { Status.Value = "Host a world before switching players."; return; }
        if (playerWindow) return;
        previousCursor = Cursor.visible; previousLock = Cursor.lockState; playerWindow = true;
        Cursor.visible = true; Cursor.lockState = CursorLockMode.None; playerOne = null; playerTwo = null; search = "";
    }
    private static void ClosePlayerWindow() { if (!playerWindow) return; playerWindow = false; Cursor.visible = previousCursor; Cursor.lockState = previousLock; }
    public override void Update()
    {
        if (!GameInstance.InstanceExists) { if (equipped) Unequip(); RestoreNames(); ProcessedMarkers.Clear(); return; }
        ProcessLocalRequest();
        if (PendingPlayers.Count > 0 && Time.unscaledTime >= restoreAt) FinishPending();
        if (playerWindow) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
        if (!equipped || Cursor.visible) return;
        if (Input.GetMouseButtonDown(0)) SelectTarget();
        if (Input.GetKeyDown(KeyCode.R)) Clear();
        if (Input.GetKeyDown(KeyCode.Alpha6)) Swap(first, second);
    }
    private static void SelectTarget()
    {
        var camera = Camera.main;
        var local = GameInstance.Instance.GetFirstLocalPlayerController();
        var self = local ? local.GetPlayerCharacter() : null;
        if (!camera || !self) return;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f));
        var hits = Physics.RaycastAll(ray, 350f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(self.transform)) continue;
            var character = hit.collider.GetComponentInParent<PlayerCharacter>();
            var player = character ? character.GetPlayerController() : null;
            var vehicle = hit.collider.GetComponentInParent<PlayerVehicle>();
            var body = vehicle ? vehicle.GetComponentInChildren<Rigidbody>() : hit.collider.attachedRigidbody;
            if (!player && (!body || body.isKinematic)) { Status.Value = "Select a player or a movable physics object."; return; }
            var target = new Target { Player = player, Body = player ? null : body, Name = player ? player.GetPlayerName() : body.name };
            if (first == null || second != null) { first = target; second = null; Status.Value = "First: " + target.Name + ". Select a second target."; }
            else if (Same(first, target)) Status.Value = "Choose a different second target.";
            else { second = target; Status.Value = first.Name + " <-> " + second.Name + ". Press 6 to switch."; }
            return;
        }
        Status.Value = "No target found. Aim at a player or movable object.";
    }
    private static bool Same(Target a, Target b) => a != null && b != null && ((a.Player && a.Player == b.Player) || (a.Body && a.Body == b.Body));
    private static bool Valid(Target t) => t != null && ((t.Player && t.Player.GetPlayerCharacter()) || (t.Body && !t.Body.isKinematic));
    private static Vector3 Position(Target t) => t.Player ? t.Player.GetPlayerCharacter().GetCenterFeetPosition() : t.Body.position;
    private static bool InVehicle(Target t) => t.Player && t.Player.GetPlayerControllerInteractor()?.GetEnteredAction() != null;
    private static void Swap(Target a, Target b)
    {
        if (!PropSpawnManager.IsServer || !GameInstance.InstanceExists) { Status.Value = "Only the host can swap targets in a world."; return; }
        if (PendingPlayers.Count > 0) { Status.Value = "Wait for the previous player switch to finish."; return; }
        if (!Valid(a) || !Valid(b) || Same(a, b)) { Status.Value = "Select two different available targets."; return; }
        if (InVehicle(a) || InVehicle(b)) { Status.Value = "Players must leave vehicles or seats before switching."; return; }
        foreach (var target in new[] { a, b })
        {
            if (!target.Player) continue;
            var name = target.Player.GetPlayerName() ?? "";
            if (name.StartsWith(Marker) || name.StartsWith("[PARTY IT] ") || name.StartsWith("[H&S SEEKER] "))
            { Status.Value = "Finish the active party or Hide & Seek round before switching marked players."; return; }
        }
        var posA = Position(a); var posB = Position(b);
        Move(a, posB); Move(b, posA);
        if (PendingPlayers.Count > 0) { restoreAt = Time.unscaledTime + 8f; Status.Value = "Switch requested. Waiting for the players' games..."; ProcessLocalRequest(); }
        else Status.Value = "Switched " + a.Name + " and " + b.Name + ".";
        first = null; second = null;
    }
    private static void Move(Target target, Vector3 destination)
    {
        if (!target.Player) { target.Body.position = destination; target.Body.velocity = Vector3.zero; target.Body.angularVelocity = Vector3.zero; target.Body.WakeUp(); return; }
        var name = target.Player.GetPlayerName() ?? "Player";
        var c = CultureInfo.InvariantCulture;
        var payload = Marker + Guid.NewGuid().ToString("N").Substring(0, 8) + ":" + destination.x.ToString("R", c) + ":" + destination.y.ToString("R", c) + ":" + destination.z.ToString("R", c) + "] " + name;
        PendingPlayers.Add(new Pending { Player = target.Player, Original = name, Marked = payload, Destination = destination });
        target.Player.SetServerPlayerName(payload);
    }
    private static void ProcessLocalRequest()
    {
        var locals = GameInstance.Instance.GetLocalPlayerControllers();
        foreach (var player in locals)
        {
            if (!player) continue;
            var name = player.GetPlayerName() ?? "";
            if (!name.StartsWith(Marker)) { ProcessedMarkers.Remove(player); continue; }
            var end = name.IndexOf(']');
            if (end < 0) continue;
            var token = name.Substring(0, end + 1);
            if (ProcessedMarkers.TryGetValue(player, out var processed) && token == processed) continue;
            var parts = token.Substring(Marker.Length, token.Length - Marker.Length - 1).Split(':');
            if (parts.Length != 4 || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
            var character = player.GetPlayerCharacter();
            if (!character || player.GetPlayerControllerInteractor()?.GetEnteredAction() != null) continue;
            character.SetPlayerPosition(new Vector3(x, y, z));
            ProcessedMarkers[player] = token;
        }
    }
    private static void FinishPending()
    {
        var success = PendingPlayers.All(p => p.Player && p.Player.GetPlayerCharacter() &&
            (p.Player.GetPlayerCharacter().GetCenterFeetPosition() - p.Destination).sqrMagnitude < 100f);
        RestoreNames();
        Status.Value = success ? "Player switch completed." : "Player switch not confirmed. Both players need the mod and must stay out of vehicles.";
        Plugin.Log?.LogInfo("Switchor: " + Status.Value);
    }
    private static void RestoreNames()
    {
        foreach (var p in PendingPlayers)
            if (p.Player && PropSpawnManager.IsServer && p.Player.GetPlayerName() == p.Marked) p.Player.SetServerPlayerName(p.Original);
        PendingPlayers.Clear();
    }
    internal static void DrawOverlay()
    {
        if (playerWindow) window = GUI.Window(823723, window, DrawPlayerWindow, "Switchor: Search Players");
        if (!equipped || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.yellow } };
        GUI.Label(new Rect(Screen.width / 2f - 20, Screen.height / 2f - 25, 40, 50), "+", style);
        GUI.Label(new Rect(Screen.width / 2f - 300, Screen.height / 2f + 28, 600, 35), second != null ? "Two targets selected — press 6" : first != null ? "Select target 2" : "Select target 1", style);
    }
    private static void DrawPlayerWindow(int id)
    {
        GUILayout.Label("Search connected players, select two, then press Enter.");
        search = GUILayout.TextField(search ?? "");
        GUILayout.Label("1: " + (playerOne ? playerOne.GetPlayerName() : "not selected") + "   2: " + (playerTwo ? playerTwo.GetPlayerName() : "not selected"));
        scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(250));
        if (GameInstance.InstanceExists)
            foreach (var player in GameInstance.Instance.GetPlayerControllers())
            {
                if (!player || !player.GetPlayerCharacter()) continue;
                var name = player.GetPlayerName() ?? "Player";
                if (name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (GUILayout.Button(name)) { if (!playerOne || playerTwo) { playerOne = player; playerTwo = null; } else if (player != playerOne) playerTwo = player; }
            }
        GUILayout.EndScrollView();
        var submit = GUILayout.Button("Switch selected players");
        if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)) { submit = true; Event.current.Use(); }
        if (submit)
        {
            if (!playerOne || !playerTwo) Status.Value = "Select two different players first.";
            else Swap(new Target { Player = playerOne, Name = playerOne.GetPlayerName() }, new Target { Player = playerTwo, Name = playerTwo.GetPlayerName() });
        }
        GUILayout.Label(Status.Value);
        if (GUILayout.Button("Close")) ClosePlayerWindow();
        GUI.DragWindow(new Rect(0, 0, 10000, 22));
    }
    internal static void Shutdown() { Unequip(); RestoreNames(); }
}

[HarmonyPatch]
internal static class SwitchorOtherWeaponsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var types = new[] { typeof(WindCannonMod), typeof(PropSpawnerGunMod), typeof(RocketLauncherMod),
            typeof(PaintballGunMod), typeof(MinecraftBuildingMod), typeof(HeavyAutomaticGunMod), typeof(GrapplingHookMod),
            typeof(ShrinkRayMod), typeof(LaserEyesMod), typeof(CamouflagePropHuntMod), typeof(LavaGunMod),
            typeof(ChaosWandMod), typeof(PortalGunMod), typeof(LightningGunMod), typeof(WobblyHeadHomingGunMod),
            typeof(BananaPeelLauncherMod), typeof(TornadoGunMod), typeof(FireworkMinigunMod), typeof(JellyGunMod),
            typeof(TemporaryTunnelDrillMod), typeof(MosesStaffMod), typeof(PowerSwordMod), typeof(BubbleBlasterMod),
            typeof(TsunamiGunMod), typeof(MeteorShowerCannonMod) };
        foreach (var type in types) { var method = AccessTools.Method(type, "Equip"); if (method != null) yield return method; }
        var vehicle = AccessTools.Method(typeof(VehicleAircraftSpawnerMod), "Equip");
        if (vehicle != null) yield return vehicle;
        foreach (var methodName in new[] { "EquipGravity", "EquipHole" })
        { var method = AccessTools.Method(typeof(GravityToyMod), methodName); if (method != null) yield return method; }
    }
    private static void Prefix() { if (SwitchorGunMod.IsEquipped) SwitchorGunMod.Unequip(); }
}

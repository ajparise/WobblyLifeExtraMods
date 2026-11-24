using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using HawkNetworking;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Configures the Crossplay host capacity before session creation and displays a scrollable roster.</summary>
public sealed class LargeCrossplayLobbyMod : BaseMod
{
    private static readonly Ref<string> Status = new("Set the limit before starting a new Crossplay world.");
    private static bool patchReady;
    private static bool visible;
    private static bool previousCursor;
    private static CursorLockMode previousLock;
    private static Rect window = new(80f, 60f, 560f, 520f);
    private static Vector2 scroll;
    public override string Name => "Large Crossplay Lobby & Player List";
    public override string Description => "Set the capacity for a new Crossplay world and see a scrollable player list.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Label = "Enable larger Crossplay lobbies")]
    public static Ref<bool> EnabledState = new(true);
    [ModSetting(Order = 20, Min = 4f, Max = 16f, Label = "Players per new lobby",
        Description = "Includes the host. Change this before hosting; an existing session must be recreated.")]
    public static Ref<int> PlayerLimit = new(16);

    protected override void OnStaticInit()
    {
        BindData(EnabledState, "LargeCrossplayEnabled", true);
        BindData(PlayerLimit, "LargeCrossplayLimit", 16);
        try
        {
            var harmony = new Harmony("com.aj.wobblylife.extramods.largecrossplay");
            var host = AccessTools.Method(typeof(WobblyNetworkManager), "HostServer");
            if (host == null) throw new MissingMethodException("WobblyNetworkManager.HostServer");
            var hostPrefix = new HarmonyMethod(typeof(LargeCrossplayLobbyMod), nameof(HostPrefix)) { priority = Priority.Last };
            harmony.Patch(host, prefix: hostPrefix);
            patchReady = true;
            PatchDiagnostic(harmony, "OnPlayerNotAccepted", nameof(NotAcceptedPrefix));
            PatchDiagnostic(harmony, "OnAuthenticationFailed", nameof(AuthenticationFailedPrefix));
            PatchDiagnostic(harmony, "OnPlayerDisconnected", nameof(DisconnectedPrefix));
            PatchDiagnostic(harmony, "DisconnectPlayer", nameof(KickPrefix));
            var eosType = AccessTools.TypeByName("HawkNetworking.EOSNetworkManager");
            if (eosType != null)
            {
                PatchEosDiagnostic(harmony, eosType, "OnPeerConnectionClosed", nameof(PeerClosedPrefix));
                PatchEosDiagnostic(harmony, eosType, "HandleDoSendError", nameof(SendErrorPrefix));
            }
            var eosHost = eosType == null ? null : AccessTools.Method(eosType, "HostServer", new[] { typeof(int) });
            if (eosHost != null)
            {
                try { harmony.Patch(eosHost, postfix: new HarmonyMethod(typeof(LargeCrossplayLobbyMod), nameof(EosHostPostfix))); }
                catch (Exception exception) { Plugin.Log?.LogWarning("Crossplay session diagnostic unavailable: " + exception.Message); }
            }
            Plugin.Log?.LogInfo("Large Crossplay lobby host patch installed; configure the limit before hosting.");
        }
        catch (Exception exception)
        {
            patchReady = false;
            Status.Value = "Lobby capacity patch failed; see BepInEx log.";
            Plugin.Log?.LogError("Large Crossplay lobby patch failed: " + exception);
        }
    }
    private static void PatchEosDiagnostic(Harmony harmony, Type eosType, string targetName, string patchName)
    {
        var target = AccessTools.DeclaredMethod(eosType, targetName);
        if (target == null) { Plugin.Log?.LogWarning("Crossplay diagnostic unavailable: " + targetName); return; }
        try { harmony.Patch(target, prefix: new HarmonyMethod(typeof(LargeCrossplayLobbyMod), patchName)); }
        catch (Exception exception) { Plugin.Log?.LogWarning("Crossplay diagnostic " + targetName + " failed: " + exception.Message); }
    }
    private static void KickPrefix(HawkNetworkManager __instance, HawkConnection __0, int __1)
    {
        if (!__instance || !__instance.IsServer()) return;
        Plugin.Log?.LogWarning($"Large Crossplay diagnostic: host is disconnecting connection {(__0 == null ? -1 : __0.Id)}, reason={__1}; caller: {new System.Diagnostics.StackTrace(1, false)}");
    }
    private static void PeerClosedPrefix(object[] __args)
    {
        try
        {
            var info = __args != null && __args.Length > 0 ? __args[0] : null;
            var reason = info?.GetType().GetProperty("Reason")?.GetValue(info, null);
            Plugin.Log?.LogWarning("Large Crossplay diagnostic: EOS peer connection closed; reason=" + (reason ?? "unavailable"));
        }
        catch (Exception exception) { Plugin.Log?.LogWarning("Crossplay closure diagnostic failed: " + exception.Message); }
    }
    private static void SendErrorPrefix(HawkConnection __0)
    {
        Plugin.Log?.LogWarning($"Large Crossplay diagnostic: EOS send error for connection {(__0 == null ? -1 : __0.Id)}.");
    }
    private static void PatchDiagnostic(Harmony harmony, string targetName, string patchName)
    {
        var target = AccessTools.Method(typeof(HawkNetworkManager), targetName);
        if (target == null) { Plugin.Log?.LogWarning("Lobby diagnostic unavailable: " + targetName); return; }
        try { harmony.Patch(target, prefix: new HarmonyMethod(typeof(LargeCrossplayLobbyMod), patchName)); }
        catch (Exception exception) { Plugin.Log?.LogWarning("Lobby diagnostic " + targetName + " failed: " + exception.Message); }
    }
    private static void HostPrefix(ref int maxPlayers, WobblyNetworkFilter ___previousFilter, HawkNetworkManager ___networkManager)
    {
        if (!EnabledState.Value || ___previousFilter != WobblyNetworkFilter.Game || !___networkManager) return;
        var type = ___networkManager.GetType();
        while (type != null && type.FullName != "HawkNetworking.EOSNetworkManager") type = type.BaseType;
        if (type == null) return;
        var original = maxPlayers;
        maxPlayers = Mathf.Clamp(PlayerLimit.Value, 4, 16);
        Plugin.Log?.LogInfo($"Large Crossplay: normal-world host requested {original} slots; creating EOS session with {maxPlayers} slots including host.");
    }
    private static void NotAcceptedPrefix(HawkNetworkManager __instance, HawkConnection __0)
    {
        if (!__instance || !__instance.IsServer()) return;
        Plugin.Log?.LogWarning($"Large Crossplay diagnostic: player not accepted (connection {(__0 == null ? -1 : __0.Id)}, connections={__instance.GetCurrentPlayerCount()}, limit={__instance.GetMaxPlayerCount()}, joinable={__instance.IsJoinable()}). The game did not supply a rejection reason here.");
    }
    private static void AuthenticationFailedPrefix(HawkNetworkManager __instance, HawkConnection __0)
    {
        if (!__instance || !__instance.IsServer()) return;
        Plugin.Log?.LogWarning($"Large Crossplay diagnostic: authentication failed (connection {(__0 == null ? -1 : __0.Id)}); authentication was not bypassed.");
    }
    private static void DisconnectedPrefix(HawkNetworkManager __instance, HawkConnection __0)
    {
        if (!__instance || !__instance.IsServer()) return;
        Plugin.Log?.LogInfo($"Large Crossplay diagnostic: connection {(__0 == null ? -1 : __0.Id)} disconnected; connections before removal={__instance.GetCurrentPlayerCount()}, limit={__instance.GetMaxPlayerCount()}. No reason is exposed by this callback.");
    }
    private static void EosHostPostfix(HawkNetworkManager __instance, int __0)
    {
        if (!EnabledState.Value || !__instance) return;
        Plugin.Log?.LogInfo($"Large Crossplay diagnostic: EOS host received {__0} slots; manager currently reports {__instance.GetMaxPlayerCount()}. A failed session creation can still prevent joining.");
    }
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("LargeLobbyHelp", "Crossplay normal worlds only. Set the player limit before hosting, then create a new online lobby; the host counts as a player. Changing the value does not resize an existing lobby. Up to 16 is experimental and the game or network service may impose other limits. All participants should use the same mod version. Open the scrollable roster with F4 or the button below. Authentication and bans are unchanged."),
        base.BuildPanel(id),
        new HStack("LargeLobbyActions", ActionMenu(new Button("Open player list", OpenRoster), nameof(OpenRoster)),
            ActionMenu(new Button("Refresh lobby status", RefreshStatus), nameof(RefreshStatus))).WithContentWidth(),
        new TextWrapped("LargeLobbyStatus", "").WithText(Status));
    [ModAction(ShowInUI = false)] public static void RefreshStatus()
    {
        var manager = HawkNetworkManager.DefaultInstance;
        if (!patchReady) { Status.Value = "Capacity patch unavailable; check the BepInEx log."; return; }
        if (!manager || !manager.IsConnected())
        { Status.Value = $"Next Crossplay world: {(EnabledState.Value ? Mathf.Clamp(PlayerLimit.Value, 4, 16).ToString() : "game default")} players. Rehost after changing settings."; return; }
        Status.Value = $"Current session: {manager.GetCurrentPlayerCount()} connections, advertised limit {manager.GetMaxPlayerCount()}. Changes apply only to a new hosted world.";
    }
    [ModAction(ShowInUI = false)] public static void OpenRoster()
    {
        if (visible) return;
        if (!HasRosterContext()) { Status.Value = "Connect to or host a world to view players."; return; }
        previousCursor = Cursor.visible; previousLock = Cursor.lockState;
        visible = true; Cursor.visible = true; Cursor.lockState = CursorLockMode.None;
    }
    private static void CloseRoster()
    {
        if (!visible) return;
        visible = false; Cursor.visible = previousCursor; Cursor.lockState = previousLock;
    }
    public override void Update()
    {
        if (!HasRosterContext()) { if (visible) CloseRoster(); return; }
        if (Input.GetKeyDown(KeyCode.F4) && (visible || !Cursor.visible)) { if (visible) CloseRoster(); else OpenRoster(); }
        if (visible) { Cursor.visible = true; Cursor.lockState = CursorLockMode.None; }
    }
    private static bool HasRosterContext()
    {
        var manager = HawkNetworkManager.DefaultInstance;
        return GameInstance.InstanceExists || (manager && manager.IsConnected());
    }
    internal static void DrawRoster()
    {
        if (!visible) return;
        window.x = Mathf.Clamp(window.x, 0f, Mathf.Max(0f, Screen.width - window.width));
        window.y = Mathf.Clamp(window.y, 0f, Mathf.Max(0f, Screen.height - window.height));
        window = GUI.Window(823724, window, DrawWindow, "Crossplay Players — F4 closes");
    }
    private static void DrawWindow(int id)
    {
        var manager = HawkNetworkManager.DefaultInstance;
        var players = GameInstance.InstanceExists ? GameInstance.Instance.GetPlayerControllers().Where(p => p).ToList() : null;
        var connections = manager ? manager.GetPlayers() : null;
        GUILayout.Label("World players: " + (players?.Count ?? 0) + "   |   Accepted network connections: " + (connections?.Count ?? 0));
        GUILayout.Label("A connection can contain multiple local players. Players still loading may appear later.");
        scroll = GUILayout.BeginScrollView(scroll, GUI.skin.box, GUILayout.Height(350f));
        GUILayout.Label("WORLD PLAYERS");
        if (players != null)
            foreach (var player in players)
                GUILayout.Label((player.IsLocal() ? "[You] " : "") + (string.IsNullOrWhiteSpace(player.GetPlayerName()) ? "Player (loading)" : player.GetPlayerName()));
        GUILayout.Space(10f); GUILayout.Label("ACCEPTED CONNECTIONS");
        if (connections != null)
            foreach (var connection in connections)
            {
                if (connection == null) continue;
                GUILayout.Label((connection.IsHost ? "[Host] " : "") + (string.IsNullOrWhiteSpace(connection.Name) ? "Connection " + connection.Id : connection.Name));
            }
        GUILayout.EndScrollView();
        if (GUILayout.Button("Close")) CloseRoster();
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 22f));
    }
    internal static void Shutdown() => CloseRoster();
}

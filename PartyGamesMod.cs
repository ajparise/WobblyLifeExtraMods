using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class PartyGamesMod : BaseMod
{
    private const string Marker = "[PARTY IT] ";
    private static readonly Ref<string> Status = new("Host a world with at least two players to start.");
    private static readonly List<PlayerController> Players = new();
    private static readonly Dictionary<PlayerController, int> Scores = new();
    private static PlayerController carrier;
    private static string carrierName;
    private static string mode;
    private static float endsAt;
    private static float nextTag;
    private static float showResultUntil;
    private static string result;
    private static bool armed;
    public override string Name => "Party Games: Hot Potato & Tag";
    public override string Description => "Host free-roam hot potato and tag for two or more players.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("PartyHelp", "Two or more players. Host starts the round. The player with [PARTY IT] before their name passes it by getting close to another player. Move apart after a pass before tagging again. Hot Potato: whoever holds it after 45 seconds loses. Tag: score a point each time you pass it; highest score after two minutes wins. The host sees the timer and results. Players should stay in the same area."),
        new HStack("PartyActions", ActionMenu(new Button("Start Hot Potato", StartPotato), nameof(StartPotato)),
            ActionMenu(new Button("Start Tag", StartTag), nameof(StartTag)), ActionMenu(new Button("Stop", Stop), nameof(Stop))).WithContentWidth(),
        new TextWrapped("PartyStatus", "").WithText(Status));
    [ModAction(ShowInUI = false)] public static void StartPotato() => Start("HOT POTATO", 45f);
    [ModAction(ShowInUI = false)] public static void StartTag() => Start("TAG", 120f);
    private static void Start(string selected, float duration)
    {
        if (!PropSpawnManager.IsServer || !GameInstance.InstanceExists) { Status.Value = "Only the host can start a party game in a world."; return; }
        StopInternal();
        Players.AddRange(GameInstance.Instance.GetPlayerControllers().Where(p => p && p.GetPlayerCharacter()).Distinct());
        if (Players.Count < 2) { Players.Clear(); Status.Value = "At least two players are needed."; return; }
        if (Players.Any(p => (p.GetPlayerName() ?? "").StartsWith("[H&S SEEKER] ")))
        { Players.Clear(); Status.Value = "Stop Hide & Seek before starting a party game."; return; }
        foreach (var player in Players) Scores[player] = 0;
        mode = selected; endsAt = Time.unscaledTime + duration; nextTag = Time.unscaledTime + 3f; armed = false;
        SetCarrier(Players[Random.Range(0, Players.Count)]);
        result = ""; Status.Value = mode + " started! Find the player marked [PARTY IT].";
    }
    [ModAction(ShowInUI = false)] public static void Stop()
    {
        if (!PropSpawnManager.IsServer) { Status.Value = "Only the host can stop the round."; return; }
        StopInternal(); Status.Value = "Party game stopped.";
    }
    private static void SetCarrier(PlayerController player)
    {
        RestoreCarrier(); carrier = player;
        if (!carrier) return;
        carrierName = carrier.GetPlayerName();
        if (string.IsNullOrEmpty(carrierName)) carrierName = "Player";
        carrier.SetServerPlayerName(Marker + carrierName);
    }
    private static void RestoreCarrier()
    {
        if (carrier && PropSpawnManager.IsServer && (carrier.GetPlayerName() ?? "").StartsWith(Marker)) carrier.SetServerPlayerName(carrierName ?? "Player");
        carrier = null; carrierName = null;
    }
    private static void StopInternal()
    {
        RestoreCarrier(); Players.Clear(); Scores.Clear(); mode = null; result = null; showResultUntil = 0f; armed = false;
    }
    public override void Update()
    {
        if (string.IsNullOrEmpty(mode)) return;
        if (!GameInstance.InstanceExists || !PropSpawnManager.IsServer) { StopInternal(); return; }
        Players.RemoveAll(p => !p);
        if (!carrier || Players.Count < 2) { StopInternal(); Status.Value = "Round stopped: not enough players remain."; return; }
        var character = carrier.GetPlayerCharacter();
        if (!character) return;
        if (Time.unscaledTime >= endsAt) { Finish(); return; }
        var origin = Position(carrier, character);
        var nearest = Players.Where(p => p && p != carrier && p.GetPlayerCharacter())
            .Select(p => new { Player = p, Distance = Vector3.Distance(origin, Position(p, p.GetPlayerCharacter())) })
            .OrderBy(x => x.Distance).FirstOrDefault();
        if (nearest == null) return;
        if (nearest.Distance > 4f) armed = true;
        if (!armed || Time.unscaledTime < nextTag || nearest.Distance > 2.5f) return;
        Scores[carrier] = Scores[carrier] + 1;
        var oldName = carrierName;
        SetCarrier(nearest.Player);
        nextTag = Time.unscaledTime + 2f; armed = false;
        Status.Value = oldName + " passed it to " + carrierName + "!";
    }
    private static Vector3 Position(PlayerController player, PlayerCharacter character)
    {
        var entered = player.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
        var vehicle = entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
        return vehicle ? vehicle.transform.position : character.transform.position;
    }
    private static void Finish()
    {
        if (mode == "HOT POTATO") result = "TIME UP! " + carrierName + " has the potato!";
        else
        {
            var max = Players.Max(p => Scores[p]);
            var winners = Players.Where(p => Scores[p] == max).Select(p => p == carrier ? carrierName : p.GetPlayerName()).ToArray();
            result = "TAG WINNER: " + string.Join(", ", winners) + " (" + max + " tags)";
        }
        RestoreCarrier(); Players.Clear(); Scores.Clear(); mode = null;
        Status.Value = result; showResultUntil = Time.unscaledTime + 10f;
    }
    internal static void DrawOverlay()
    {
        if (Event.current.type != EventType.Repaint || (string.IsNullOrEmpty(mode) && Time.unscaledTime >= showResultUntil)) return;
        var label = !string.IsNullOrEmpty(mode) ? mode + "  " + Mathf.CeilToInt(Mathf.Max(0f, endsAt - Time.unscaledTime)) + "s   IT: " + carrierName : result;
        var style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 25, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        var rect = new Rect((Screen.width - 620f) / 2f, 22f, 620f, 58f);
        var previous = GUI.color; GUI.color = new Color(0, 0, 0, 0.8f); GUI.DrawTexture(rect, Texture2D.whiteTexture); GUI.color = previous;
        GUI.Label(rect, label, style);
    }
    internal static void Shutdown() => StopInternal();
}

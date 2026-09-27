using System;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Host-controlled Hide & Seek rounds that run directly in the free-roam world.</summary>
public sealed class FreeRoamHideAndSeekMod : BaseMod
{
    private enum RoundPhase
    {
        Idle,
        Hiding,
        Seeking,
        Finished
    }

    private const string SeekerMarker = "[H&S SEEKER] ";
    private const int SeekerReward = 1000;
    private const int HiderReward = 100;
    private static readonly object FreezeHandle = new();
    private static readonly Ref<string> Status =
        new("Ready. Enter a free-roam world with at least two players, then start a round.");
    private static readonly List<PlayerController> Hiders = new();
    private static readonly HashSet<PlayerController> FoundHiders = new();
    private static readonly Dictionary<PlayerCharacter, bool> PreviousNameVisibility = new();
    private static readonly Dictionary<GameObject, bool> HiddenNavigationObjects = new();

    private static RoundPhase phase;
    private static PlayerController seeker;
    private static PlayerControllerInputManager seekerInput;
    private static Vector3 seekerRootAnchor;
    private static Vector3 seekerBodyAnchor;
    private static float phaseEndsAt;
    private static float resultEndsAt;
    private static float roleRevealEndsAt;
    private static float timesUpMessageEndsAt;
    private static float tagsEnabledAt;
    private static string seekerOriginalName = "";
    private static bool ownsSeekerMarker;
    private static string resultText = "";
    private static string lastFoundText = "";
    private static float lastFoundTextEndsAt;
    private static float missingSeekerSince = -1f;

    public override string Name => "Free-Roam Hide & Seek";

    public override string Description =>
        "Assigns random seeker and hider roles for rewarded one-minute-hide, five-minute-seek free-roam rounds.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 15f, Max = 180f, Label = "Hiding time (seconds)")]
    public static Ref<float> HidingTime = new(60f);

    [ModSetting(Order = 20, Min = 60f, Max = 900f, Label = "Seeking time (seconds)")]
    public static Ref<float> SeekingTime = new(300f);

    [ModSetting(Order = 30, Min = 1f, Max = 8f, Label = "Tag distance")]
    public static Ref<float> TagDistance = new(2.5f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("HideAndSeekHelp",
                "This game runs in the normal free-roam world, not Arcade Mode. A random connected player becomes the " +
                "seeker and everyone else becomes a hider. A role reveal appears on screen. Hiders can immediately move and " +
                "use cars or planes while the seeker is frozen behind a one-minute blackout. The seeker then has five " +
                "minutes to tag everyone. A seeker win pays $1,000; a hider-team win pays each hider $100. Install the mod " +
                "for every player who should receive the private role overlay and seeker blackout."),
            base.BuildPanel(id),
            new HStack("HideAndSeekActions",
                ActionMenu(new Button("Start Hide & Seek", StartRound), nameof(StartRound)),
                ActionMenu(new Button("Stop round", StopRound), nameof(StopRound))
            ).WithContentWidth(),
            new TextWrapped("HideAndSeekStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void StartRound()
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can start Hide & Seek.";
            return;
        }
        if (!GameInstance.InstanceExists)
        {
            Status.Value = "Enter a free-roam world before starting Hide & Seek.";
            return;
        }

        StopRoundInternal(false);
        var players = GameInstance.Instance.GetPlayerControllers()
            .Where(player => player && player.GetPlayerCharacter())
            .Distinct()
            .ToList();
        if (players.Count < 2)
        {
            Status.Value = "Hide & Seek needs at least two players. Invite a hider, then start the round.";
            return;
        }

        var selectedSeeker = players[UnityEngine.Random.Range(0, players.Count)];
        var originalName = PlayerName(selectedSeeker);
        selectedSeeker.SetServerPlayerName(SeekerMarker + originalName);
        InitializeRound(players, selectedSeeker, true);
        seekerOriginalName = originalName;
    }

    private static void InitializeRound(
        IReadOnlyCollection<PlayerController> players,
        PlayerController selectedSeeker,
        bool ownsMarker)
    {
        StopRoundInternal(false);
        seeker = selectedSeeker;
        ownsSeekerMarker = ownsMarker;
        if (!seeker || !seeker.GetPlayerCharacter())
        {
            Status.Value = "The selected seeker is not ready yet.";
            seeker = null;
            return;
        }

        Hiders.AddRange(players.Where(player => player && player != seeker && player.GetPlayerCharacter()));
        if (Hiders.Count == 0)
        {
            Status.Value = "Hide & Seek needs at least two players. Invite a hider, then start the round.";
            seeker = null;
            return;
        }

        if (IsLocalPlayer(seeker))
        {
            foreach (var hider in Hiders)
            {
                var character = hider.GetPlayerCharacter();
                if (!character) continue;
                PreviousNameVisibility[character] = IsNameVisible(character);
                character.SetCharacterNameVisible(false);
            }
        }

        phase = RoundPhase.Hiding;
        phaseEndsAt = Time.unscaledTime + Mathf.Clamp(HidingTime.Value, 15f, 180f);
        roleRevealEndsAt = Time.unscaledTime + 4.5f;
        timesUpMessageEndsAt = 0f;
        resultText = "";
        lastFoundText = "";
        missingSeekerSince = -1f;
        HideRoundNavigationUi();
        FreezeSeeker();
        var localRole = IsLocalPlayer(seeker) ? "SEEKER" : "HIDER";
        Status.Value = $"Your role is {localRole}. {Hiders.Count} hider{Plural(Hiders.Count)} have " +
                       $"{FormatTime(SecondsRemaining)} to hide. Cars and planes are allowed.";
        Plugin.Log?.LogInfo($"Hide & Seek started with seeker {PlayerName(seeker)} and {Hiders.Count} hiders.");
    }

    [ModAction(ShowInUI = false)]
    public static void StopRound()
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can stop the shared Hide & Seek round.";
            return;
        }
        if (phase == RoundPhase.Idle)
        {
            Status.Value = "No Hide & Seek round is running.";
            return;
        }
        StopRoundInternal(true);
    }

    public override void Update()
    {
        if (phase == RoundPhase.Idle)
        {
            TryJoinAdvertisedRound();
            return;
        }
        if (phase == RoundPhase.Finished) return;
        HideRoundNavigationUi();
        if (!GameInstance.InstanceExists)
        {
            StopRoundInternal(false);
            return;
        }
        if (!ownsSeekerMarker && !HasSeekerMarker(seeker))
        {
            Status.Value = "The host stopped the Hide & Seek round.";
            StopRoundInternal(false);
            return;
        }

        var seekerCharacter = seeker ? seeker.GetPlayerCharacter() : null;
        if (!seekerCharacter)
        {
            if (missingSeekerSince < 0f) missingSeekerSince = Time.unscaledTime;
            if (Time.unscaledTime - missingSeekerSince > 4f)
            {
                Status.Value = "Hide & Seek stopped because the seeker left the world.";
                StopRoundInternal(false);
            }
            return;
        }
        missingSeekerSince = -1f;

        if (phase == RoundPhase.Hiding)
        {
            HoldSeekerAtAnchor(seekerCharacter);
            if (Time.unscaledTime >= phaseEndsAt) BeginSeeking();
            return;
        }

        PruneDisconnectedHiders();
        if (Hiders.Count == 0)
        {
            Status.Value = "Hide & Seek stopped because every hider left the world.";
            StopRoundInternal(false);
            return;
        }
        if (Time.unscaledTime >= tagsEnabledAt) CheckForTags(seekerCharacter);
        if (RemainingHiders == 0)
        {
            FinishRound("SEEKER WINS!", "The seeker found every hider.", true);
            return;
        }
        if (Time.unscaledTime >= phaseEndsAt)
            FinishRound("HIDERS WIN!", $"{RemainingHiders} hider{Plural(RemainingHiders)} stayed hidden.", false);
    }

    private static void TryJoinAdvertisedRound()
    {
        if (!GameInstance.InstanceExists) return;
        var players = GameInstance.Instance.GetPlayerControllers()
            .Where(player => player && player.GetPlayerCharacter())
            .Distinct()
            .ToList();
        if (players.Count < 2) return;

        var advertisedSeeker = players.FirstOrDefault(HasSeekerMarker);
        if (!advertisedSeeker) return;
        var originalName = PlayerName(advertisedSeeker);
        InitializeRound(players, advertisedSeeker, false);
        seekerOriginalName = originalName;
    }

    internal static void DrawOverlay()
    {
        if (Event.current.type != EventType.Repaint) return;
        if (phase == RoundPhase.Idle) return;
        if (phase == RoundPhase.Finished && Time.unscaledTime >= resultEndsAt)
        {
            StopRoundInternal(false);
            return;
        }

        if (phase == RoundPhase.Hiding)
        {
            var localSeeker = IsLocalPlayer(seeker);
            if (localSeeker || Time.unscaledTime < roleRevealEndsAt)
            {
                var previousColor = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, localSeeker ? 0.99f : 0.9f);
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = previousColor;

                DrawCenteredText(Screen.height * 0.3f,
                    localSeeker ? "YOUR ROLE: SEEKER" : "YOUR ROLE: HIDER", 38,
                    localSeeker ? new Color(1f, 0.4f, 0.2f) : new Color(0.35f, 1f, 0.5f));
                DrawCenteredText(Screen.height * 0.43f, FormatTime(SecondsRemaining), 58,
                    new Color(1f, 0.82f, 0.15f));
                DrawCenteredText(Screen.height * 0.56f,
                    localSeeker
                        ? "Screen locked until hiding time ends"
                        : "RUN AND HIDE — cars and planes are allowed",
                    22, new Color(0.8f, 0.88f, 1f));
                if (localSeeker) return;
                if (Time.unscaledTime < roleRevealEndsAt) return;
            }
        }

        var width = 430f;
        var height = phase == RoundPhase.Finished ? 150f : 118f;
        var rect = new Rect((Screen.width - width) * 0.5f, 22f, width, height);
        var oldColor = GUI.color;
        GUI.color = new Color(0.03f, 0.04f, 0.06f, 0.9f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = oldColor;

        if (phase == RoundPhase.Finished)
        {
            DrawCenteredText(rect.y + 22f, resultText, 34, new Color(1f, 0.82f, 0.15f));
            DrawCenteredText(rect.y + 78f, Status.Value, 18, Color.white);
            return;
        }

        var heading = phase == RoundPhase.Hiding ? "HIDING" : "SEEKING";
        DrawCenteredText(rect.y + 8f, $"{heading}  •  {FormatTime(SecondsRemaining)}", 28,
            new Color(1f, 0.82f, 0.15f));
        DrawCenteredText(rect.y + 48f,
            phase == RoundPhase.Hiding
                ? $"Seeker: {PlayerName(seeker)}  •  cars and planes allowed"
                : $"{RemainingHiders} of {Hiders.Count} hider{Plural(Hiders.Count)} remaining",
            20, Color.white);
        if (Time.unscaledTime < lastFoundTextEndsAt)
            DrawCenteredText(rect.y + 79f, lastFoundText, 17, new Color(0.45f, 1f, 0.55f));

        if (phase == RoundPhase.Seeking && Time.unscaledTime < timesUpMessageEndsAt)
        {
            var alertRect = new Rect((Screen.width - 560f) * 0.5f, Screen.height * 0.38f, 560f, 110f);
            oldColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.92f);
            GUI.DrawTexture(alertRect, Texture2D.whiteTexture);
            GUI.color = oldColor;
            DrawCenteredText(alertRect.y + 22f, "TIME'S UP!", 54, new Color(1f, 0.3f, 0.15f));
        }
    }

    internal static void Shutdown() => StopRoundInternal(false);

    private static void BeginSeeking()
    {
        ReleaseSeeker();
        phase = RoundPhase.Seeking;
        phaseEndsAt = Time.unscaledTime + Mathf.Clamp(SeekingTime.Value, 60f, 900f);
        timesUpMessageEndsAt = Time.unscaledTime + 3.5f;
        tagsEnabledAt = timesUpMessageEndsAt;
        Status.Value = $"Seeking started. Find {RemainingHiders} hider{Plural(RemainingHiders)} within " +
                       $"{FormatTime(SecondsRemaining)}.";
        Plugin.Log?.LogInfo("Hide & Seek seeking phase started.");
    }

    private static void FreezeSeeker()
    {
        var character = seeker ? seeker.GetPlayerCharacter() : null;
        if (!character) return;
        seekerRootAnchor = character.transform.position;
        var body = character.GetComponentInChildren<PlayerBody>(true)?.GetRigidbody();
        seekerBodyAnchor = body ? body.position : seekerRootAnchor;

        seeker.GetPlayerControllerInteractor()?.ForceRequestExit();
        seekerInput = IsLocalPlayer(seeker) ? seeker.GetPlayerControllerInputManager() : null;
        if (seekerInput)
        {
            seekerInput.DisablePlayerTransformInput(FreezeHandle);
            seekerInput.DisableInteratorInput(FreezeHandle);
        }
        HoldSeekerAtAnchor(character);
    }

    private static void HoldSeekerAtAnchor(PlayerCharacter character)
    {
        character.transform.position = seekerRootAnchor;
        var body = character.GetComponentInChildren<PlayerBody>(true)?.GetRigidbody();
        if (!body) return;
        body.position = seekerBodyAnchor;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }

    private static void ReleaseSeeker()
    {
        if (seekerInput)
        {
            seekerInput.EnablePlayerTransformInput(FreezeHandle);
            seekerInput.EnableInteratorInput(FreezeHandle);
        }
        seekerInput = null;
    }

    private static void CheckForTags(PlayerCharacter seekerCharacter)
    {
        var seekerPosition = PlayerPosition(seeker, seekerCharacter.transform.position);
        var tagDistance = Mathf.Clamp(TagDistance.Value, 1f, 8f);
        var tagDistanceSquared = tagDistance * tagDistance;

        foreach (var hider in Hiders)
        {
            if (!hider || FoundHiders.Contains(hider)) continue;
            var character = hider.GetPlayerCharacter();
            if (!character) continue;
            var hiderPosition = PlayerPosition(hider, character.transform.position);
            if ((hiderPosition - seekerPosition).sqrMagnitude > tagDistanceSquared) continue;

            FoundHiders.Add(hider);
            character.SetCharacterNameVisible(true);
            lastFoundText = $"FOUND: {PlayerName(hider)}";
            lastFoundTextEndsAt = Time.unscaledTime + 4f;
            Status.Value = $"{lastFoundText}. {RemainingHiders} hider{Plural(RemainingHiders)} remaining.";
            Plugin.Log?.LogInfo(Status.Value);
        }
    }

    private static Vector3 PlayerPosition(PlayerController player, Vector3 fallback)
    {
        var entered = player?.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
        var vehicle = entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
        return vehicle ? vehicle.transform.position : fallback;
    }

    private static void PruneDisconnectedHiders()
    {
        for (var index = Hiders.Count - 1; index >= 0; index--)
        {
            var hider = Hiders[index];
            if (hider) continue;
            FoundHiders.Remove(hider);
            Hiders.RemoveAt(index);
        }
    }

    private static void FinishRound(string headline, string detail, bool seekerWon)
    {
        RestoreHiderNames();
        ReleaseSeeker();
        RestoreRoundNavigationUi();
        if (PropSpawnManager.IsServer)
        {
            AwardWinners(seekerWon);
            detail += seekerWon
                ? $" {PlayerName(seeker)} received $1,000."
                : " Every hider received $100.";
        }
        phase = RoundPhase.Finished;
        resultText = headline;
        resultEndsAt = Time.unscaledTime + 8f;
        Status.Value = detail;
        Plugin.Log?.LogInfo($"Hide & Seek finished: {headline} {detail}");
    }

    private static void AwardWinners(bool seekerWon)
    {
        if (seekerWon) RewardPlayer(seeker, SeekerReward);
        else
            foreach (var hider in Hiders)
                RewardPlayer(hider, HiderReward);

        try
        {
            if (SaveGameManager.InstanceExists) SaveGameManager.InstanceRaw.ForceSaveNextFrame();
        }
        catch (Exception exception)
        {
            Plugin.Log?.LogWarning($"Hide & Seek reward save failed: {exception.Message}");
        }
    }

    private static void RewardPlayer(PlayerController player, int amount)
    {
        if (!player) return;
        var employment = player.GetComponent<PlayerControllerEmployment>() ??
                         player.GetComponentInChildren<PlayerControllerEmployment>(true);
        if (employment) employment.UpdateMoney(amount);
    }

    private static void StopRoundInternal(bool announce)
    {
        RestoreHiderNames();
        ReleaseSeeker();
        RestoreSeekerMarker();
        RestoreRoundNavigationUi();
        Hiders.Clear();
        FoundHiders.Clear();
        seeker = null;
        seekerOriginalName = "";
        resultText = "";
        lastFoundText = "";
        timesUpMessageEndsAt = 0f;
        tagsEnabledAt = 0f;
        phase = RoundPhase.Idle;
        if (announce) Status.Value = "Hide & Seek round stopped and player settings restored.";
    }

    private static void RestoreHiderNames()
    {
        foreach (var entry in PreviousNameVisibility)
            if (entry.Key) entry.Key.SetCharacterNameVisible(entry.Value);
        PreviousNameVisibility.Clear();
    }

    private static void RestoreSeekerMarker()
    {
        if (ownsSeekerMarker && PropSpawnManager.IsServer && seeker)
            seeker.SetServerPlayerName(string.IsNullOrWhiteSpace(seekerOriginalName)
                ? PlayerName(seeker)
                : seekerOriginalName);
        ownsSeekerMarker = false;
    }

    private static void HideRoundNavigationUi()
    {
        foreach (var minimap in UnityEngine.Object.FindObjectsOfType<UIGameplayMinimap>())
            HideNavigationObject(minimap ? minimap.gameObject : null);
        foreach (var iconCanvas in UnityEngine.Object.FindObjectsOfType<UIPlayerBasedGameplayWorldIconCanvas>())
            HideNavigationObject(iconCanvas ? iconCanvas.gameObject : null);
    }

    private static void HideNavigationObject(GameObject navigationObject)
    {
        if (!navigationObject || HiddenNavigationObjects.ContainsKey(navigationObject)) return;
        HiddenNavigationObjects[navigationObject] = navigationObject.activeSelf;
        navigationObject.SetActive(false);
    }

    private static void RestoreRoundNavigationUi()
    {
        foreach (var entry in HiddenNavigationObjects)
            if (entry.Key) entry.Key.SetActive(entry.Value);
        HiddenNavigationObjects.Clear();
    }

    private static bool IsNameVisible(PlayerCharacter character)
    {
        // The public getter is not exposed, so remember the normal free-roam default.
        return true;
    }

    private static string PlayerName(PlayerController player)
    {
        if (!player) return "Player";
        var name = player.GetPlayerName();
        if (!string.IsNullOrWhiteSpace(name) &&
            name.StartsWith(SeekerMarker, StringComparison.Ordinal))
            name = name.Substring(SeekerMarker.Length);
        return string.IsNullOrWhiteSpace(name) ? "Player" : name;
    }

    private static bool HasSeekerMarker(PlayerController player)
        => player && (player.GetPlayerName() ?? "").StartsWith(SeekerMarker, StringComparison.Ordinal);

    private static bool IsLocalPlayer(PlayerController player)
        => player && GameInstance.InstanceExists &&
           GameInstance.Instance.GetLocalPlayerControllers().Contains(player);

    private static int RemainingHiders => Mathf.Max(0, Hiders.Count - FoundHiders.Count);
    private static float SecondsRemaining => Mathf.Max(0f, phaseEndsAt - Time.unscaledTime);
    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string FormatTime(float seconds)
    {
        var total = Mathf.Max(0, Mathf.CeilToInt(seconds));
        return $"{total / 60}:{total % 60:00}";
    }

    private static void DrawCenteredText(float y, string text, int fontSize, Color color)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.Bold,
            fontSize = fontSize,
            wordWrap = true,
            normal = { textColor = color }
        };
        GUI.Label(new Rect(20f, y, Screen.width - 40f, 54f), text ?? "", style);
    }
}

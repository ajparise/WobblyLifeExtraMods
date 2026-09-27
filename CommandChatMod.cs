using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Host-only local command console with natural phrases and aliases for Extra Mods features.</summary>
public sealed class CommandChatMod : BaseMod
{
    private const int WindowId = 0x434D44;
    private const int MaximumMessages = 90;
    private static readonly Ref<string> Status = new("Press Open Command Chat or F9. Host/offline only.");
    private static readonly Dictionary<string, CommandInfo> Commands =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<CommandInfo> HelpEntries = new();
    private static readonly List<ChatMessage> Messages = new();
    private static readonly Regex NumberRegex = new(@"-?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static Rect windowRect;
    private static Vector2 messageScroll;
    private static string input = "";
    private static bool initialized;
    private static bool visible;
    private static bool cursorStateSaved;
    private static bool previousCursorVisible;
    private static CursorLockMode previousCursorLock;
    private static bool scrollToBottom;
    private static float playerSpeed;
    private static bool playerFrozen;
    private static Vector3 originalGravity;
    private static float originalTimeScale;
    private static bool gravityChanged;
    private static bool timeScaleChanged;

    public override string Name => "Host Command Chat";

    public override string Description =>
        "A host-only local command window that understands natural money and speed requests plus 100+ Extra Mods commands.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    protected override void OnStaticInit() => EnsureInitialized();

    public override Container BuildPanel(string id)
    {
        EnsureInitialized();
        return new Container(id,
            new TextWrapped("CommandChatHelp",
                "This is a command console, not player-to-player chat. Only the offline player or lobby host can run " +
                "commands. Try: make my speed 50, can I have 1000 dollars, set my balance to 5000, spawn Wobbly Jet, " +
                "equip wind cannon, or help. F9 opens and closes the window. Use help 2 for more command pages or " +
                "help guns, help money, help spawning, and help systems."),
            base.BuildPanel(id),
            new HStack("CommandChatActions",
                ActionMenu(new Button("Open Command Chat", OpenChat), nameof(OpenChat)),
                ActionMenu(new Button("Show command help", ShowHelpFromMenu), nameof(ShowHelpFromMenu)),
                ActionMenu(new Button("Reset command effects", ResetCommandEffects), nameof(ResetCommandEffects))
            ).WithContentWidth(),
            new TextWrapped("CommandChatStatus", "").WithText(Status));
    }

    public override void Update()
    {
        EnsureInitialized();
        if (Input.GetKeyDown(KeyCode.F9))
        {
            if (visible) CloseChat();
            else OpenChat();
        }
        if (visible)
        {
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
        ApplyPlayerEffects();
    }

    [ModAction(ShowInUI = false)]
    public static void OpenChat()
    {
        EnsureInitialized();
        if (!visible)
        {
            previousCursorVisible = Cursor.visible;
            previousCursorLock = Cursor.lockState;
            cursorStateSaved = true;
        }
        visible = true;
        if (windowRect.width < 1f)
            windowRect = new Rect(24f, Mathf.Max(24f, Screen.height - 590f), 720f, 540f);
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        Status.Value = CanRunCommands()
            ? $"Command Chat opened with {Commands.Count:N0} commands."
            : "Command Chat opened read-only: only the offline player or lobby host can run commands.";
        scrollToBottom = true;
    }

    [ModAction(ShowInUI = false)]
    public static void ShowHelpFromMenu()
    {
        OpenChat();
        ShowHelp("1");
    }

    [ModAction(ShowInUI = false)]
    public static void ResetCommandEffects()
    {
        playerSpeed = 0f;
        playerFrozen = false;
        if (gravityChanged) Physics.gravity = originalGravity;
        if (timeScaleChanged) Time.timeScale = originalTimeScale;
        gravityChanged = false;
        timeScaleChanged = false;
        Status.Value = "Command speed, freeze, gravity, and time-scale effects reset.";
        AddMessage("Command Chat", "Reset the temporary command effects.", false);
    }

    internal static void DrawChat()
    {
        if (!visible) return;
        windowRect.x = Mathf.Clamp(windowRect.x, 0f, Mathf.Max(0f, Screen.width - 100f));
        windowRect.y = Mathf.Clamp(windowRect.y, 0f, Mathf.Max(0f, Screen.height - 35f));
        windowRect = GUI.Window(WindowId, windowRect, DrawWindow, "Host Command Chat");
    }

    internal static void Shutdown()
    {
        ResetCommandEffects();
        visible = false;
    }

    private static void DrawWindow(int id)
    {
        GUILayout.BeginVertical();
        GUILayout.BeginHorizontal();
        GUILayout.Label(CanRunCommands() ? "HOST ACCESS" : "HOST ONLY — COMMANDS LOCKED", HeaderStyle());
        GUILayout.Label($"{Commands.Count:N0} commands", GUILayout.Width(115f));
        if (GUILayout.Button("Help", GUILayout.Width(60f))) ShowHelp("1");
        if (GUILayout.Button("Clear", GUILayout.Width(60f))) Messages.Clear();
        if (GUILayout.Button("Close", GUILayout.Width(60f))) CloseChat();
        GUILayout.EndHorizontal();

        messageScroll = GUILayout.BeginScrollView(messageScroll, GUI.skin.box, GUILayout.Height(420f));
        foreach (var message in Messages)
        {
            var style = new GUIStyle(GUI.skin.label) { wordWrap = true };
            var previous = GUI.color;
            GUI.color = message.IsHost ? new Color(0.55f, 0.9f, 1f) : new Color(1f, 0.9f, 0.35f);
            GUILayout.Label($"{message.Sender}: {message.Text}", style);
            GUI.color = previous;
        }
        if (scrollToBottom && Event.current.type == EventType.Repaint)
        {
            messageScroll.y = float.MaxValue;
            scrollToBottom = false;
        }
        GUILayout.EndScrollView();

        GUILayout.BeginHorizontal();
        GUI.SetNextControlName("CommandChatInput");
        input = GUILayout.TextField(input ?? "", 180, GUILayout.Height(30f));
        var send = GUILayout.Button("SEND", GUILayout.Width(82f), GUILayout.Height(30f));
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return &&
            GUI.GetNameOfFocusedControl() == "CommandChatInput")
        {
            send = true;
            Event.current.Use();
        }
        if (send) SubmitInput();
        GUILayout.EndHorizontal();
        GUILayout.Label("Examples: make my speed 50  •  can I have 1000 dollars  •  spawn Wobbly Jet  •  help guns");
        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, windowRect.width - 220f, 24f));
    }

    private static GUIStyle HeaderStyle()
        => new(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15 };

    private static void SubmitInput()
    {
        var submitted = (input ?? "").Trim();
        if (string.IsNullOrWhiteSpace(submitted)) return;
        input = "";
        AddMessage("Host", submitted, true);

        if (!CanRunCommands())
        {
            Reply("No — only the offline player or lobby host can run Command Chat commands.");
            return;
        }
        Execute(submitted);
    }

    private static void Execute(string submitted)
    {
        var command = Normalize(submitted);
        if (command.StartsWith("help") || command.StartsWith("commands"))
        {
            var split = command.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            ShowHelp(split.Length > 1 ? split[1] : "1");
            return;
        }
        if (command is "clear" or "clear chat" or "clear command chat")
        {
            Messages.Clear();
            Reply("Chat cleared.");
            return;
        }
        if (command is "close" or "close chat" or "close command chat")
        {
            Reply("Closing Command Chat.");
            CloseChat();
            return;
        }
        if (TryMoneyCommand(command) || TryPlayerCommand(command) || TryVehicleCommand(command) ||
            TryWorldCommand(command)) return;

        if (Commands.TryGetValue(command, out var info))
        {
            try
            {
                info.Action();
                Reply($"Done — {info.Description}");
                Status.Value = $"Ran command: {info.Phrase}";
            }
            catch (Exception exception)
            {
                Reply($"That command failed: {exception.Message}");
                Plugin.Log?.LogWarning($"Command Chat '{info.Phrase}' failed: {exception}");
            }
            return;
        }

        var lastWord = command.Split(' ').LastOrDefault() ?? command;
        var suggestions = HelpEntries.Where(entry => entry.Phrase.Contains(lastWord))
            .Select(entry => entry.Phrase).Distinct().Take(5).ToArray();
        Reply(suggestions.Length == 0
            ? "I do not know that command. Type help, help 2, or help followed by a word such as guns or spawning."
            : $"I do not know that exact command. Try: {string.Join(", ", suggestions)}");
    }

    private static bool TryMoneyCommand(string command)
    {
        var mentionsMoney = command.Contains("money") || command.Contains("cash") ||
                            command.Contains("dollar") || command.Contains("balance");
        var asksForNumber = (command.Contains("give me") || command.Contains("can i have")) &&
                            NumberRegex.IsMatch(command);
        if (!mentionsMoney && !asksForNumber) return false;

        if (command.Contains("money bag")) return false;
        var employment = LocalEmployment();
        if (!employment)
        {
            Reply("Enter a world before changing money.");
            return true;
        }
        if (command is "balance" or "money" or "my balance" or "how much money do i have")
        {
            Reply($"Your current balance is ${employment.GetLocalMoney():N0}.");
            return true;
        }
        if (!TryNumber(command, out var amount))
        {
            Reply("Tell me an amount, for example: can I have 1000 dollars.");
            return true;
        }
        var value = Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(amount)), 1, 1000000);
        if (command.Contains("set") && command.Contains("balance") || command.Contains("set my money") ||
            command.Contains("set money"))
        {
            var current = employment.GetLocalMoney();
            employment.UpdateMoney(value - current);
            QueueSave();
            Reply($"Yes — your balance is now ${employment.GetLocalMoney():N0}.");
            return true;
        }
        if (command.Contains("remove") || command.Contains("take") || command.Contains("subtract")) value = -value;
        employment.UpdateMoney(value);
        QueueSave();
        Reply(value >= 0
            ? $"Yes — I added ${value:N0}. Your balance is now ${employment.GetLocalMoney():N0}."
            : $"Done — I removed ${-value:N0}. Your balance is now ${employment.GetLocalMoney():N0}.");
        return true;
    }

    private static bool TryPlayerCommand(string command)
    {
        if (command is "reset speed" or "normal speed" or "turn off speed")
        {
            playerSpeed = 0f;
            Reply("Yes — your movement speed is back to normal.");
            return true;
        }
        if (command.Contains("speed") && !command.Contains("car") && !command.Contains("vehicle") &&
            !command.StartsWith("game speed") && !command.StartsWith("time scale") &&
            TryNumber(command, out var speed))
        {
            playerSpeed = Mathf.Clamp(Mathf.Abs(speed), 1f, 200f);
            Reply($"Yes — your speed is now {playerSpeed:0.#}.");
            return true;
        }
        if (command is "freeze me" or "freeze player")
        {
            playerFrozen = true;
            Reply("You are frozen. Type unfreeze me to move again.");
            return true;
        }
        if (command is "unfreeze me" or "unfreeze player")
        {
            playerFrozen = false;
            Reply("You can move again.");
            return true;
        }
        if ((command.StartsWith("jump") || command.StartsWith("launch me")) &&
            TryGetPlayerBody(out _, out var body))
        {
            TryNumber(command, out var power);
            power = power <= 0f ? 14f : Mathf.Clamp(power, 1f, 150f);
            body.velocity = new Vector3(body.velocity.x, Mathf.Max(body.velocity.y, power), body.velocity.z);
            Reply($"Launched you upward at {power:0.#} speed.");
            return true;
        }
        if (command is "stop me" or "stop player")
        {
            if (TryGetPlayerBody(out _, out var stoppedBody)) stoppedBody.velocity = Vector3.zero;
            Reply("Stopped your movement.");
            return true;
        }
        if (command.StartsWith("teleport up") || command.StartsWith("move me up"))
        {
            TryNumber(command, out var distance);
            TeleportPlayer(Vector3.up * Mathf.Clamp(distance <= 0f ? 10f : distance, 1f, 500f), "up");
            return true;
        }
        if (command.StartsWith("teleport forward") || command.StartsWith("move me forward"))
        {
            TryNumber(command, out var distance);
            var camera = Camera.main;
            var forward = camera ? Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized : Vector3.forward;
            TeleportPlayer(forward * Mathf.Clamp(distance <= 0f ? 10f : distance, 1f, 500f), "forward");
            return true;
        }
        return false;
    }

    private static bool TryVehicleCommand(string command)
    {
        var mentionsVehicle = command.Contains("car") || command.Contains("vehicle");
        var changesMovement = command.Contains("speed") || command.Contains("stop") || command.Contains("launch");
        if (!mentionsVehicle || !changesMovement) return false;
        var vehicle = CurrentVehicle();
        if (!vehicle)
        {
            Reply("Enter and drive a vehicle before using that command.");
            return true;
        }
        var body = vehicle.GetVehicleMovementBase()?.GetRigidbody() ?? vehicle.GetComponentInChildren<Rigidbody>(true);
        if (!body)
        {
            Reply("I could not find the current vehicle's physics body.");
            return true;
        }
        if (command.Contains("stop"))
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            Reply("Stopped your vehicle.");
            return true;
        }
        if (command.Contains("speed") && TryNumber(command, out var speed))
        {
            speed = Mathf.Clamp(Mathf.Abs(speed), 0f, 300f);
            body.velocity = vehicle.transform.forward * speed;
            Reply($"Set your vehicle speed to {speed:0.#}.");
            return true;
        }
        if (command.Contains("launch"))
        {
            TryNumber(command, out var force);
            force = force <= 0f ? 35f : Mathf.Clamp(force, 1f, 150f);
            body.AddForce(Vector3.up * force, ForceMode.VelocityChange);
            Reply($"Launched your vehicle with force {force:0.#}.");
            return true;
        }
        return false;
    }

    private static bool TryWorldCommand(string command)
    {
        if (command is "reset gravity" or "normal gravity")
        {
            if (gravityChanged) Physics.gravity = originalGravity;
            gravityChanged = false;
            Reply("Gravity returned to normal.");
            return true;
        }
        if (command.StartsWith("gravity") && TryNumber(command, out var gravity))
        {
            if (!gravityChanged) originalGravity = Physics.gravity;
            gravityChanged = true;
            Physics.gravity = new Vector3(0f, -Mathf.Clamp(Mathf.Abs(gravity), 0f, 100f), 0f);
            Reply($"Gravity strength is now {Mathf.Abs(Physics.gravity.y):0.#}.");
            return true;
        }
        if (command is "reset time" or "normal time" or "normal time scale")
        {
            if (timeScaleChanged) Time.timeScale = originalTimeScale;
            timeScaleChanged = false;
            Reply("Game time returned to normal.");
            return true;
        }
        if ((command.StartsWith("time scale") || command.StartsWith("game speed")) && TryNumber(command, out var scale))
        {
            if (!timeScaleChanged) originalTimeScale = Time.timeScale;
            timeScaleChanged = true;
            Time.timeScale = Mathf.Clamp(scale, 0.1f, 3f);
            Reply($"Game time scale is now {Time.timeScale:0.##}.");
            return true;
        }
        return false;
    }

    private static void RegisterCommands()
    {
        RegisterWeapon("wind cannon", WindCannonMod.Equip, WindCannonMod.Fire, WindCannonMod.Unequip);
        RegisterWeapon("prop gun", PropSpawnerGunMod.Equip, PropSpawnerGunMod.Fire, PropSpawnerGunMod.Unequip);
        RegisterWeapon("rocket launcher", RocketLauncherMod.Equip, RocketLauncherMod.Fire, RocketLauncherMod.Unequip);
        RegisterWeapon("paintball gun", PaintballGunMod.Equip, PaintballGunMod.Fire, PaintballGunMod.Unequip);
        RegisterWeapon("machine gun", HeavyAutomaticGunMod.Equip, HeavyAutomaticGunMod.Fire, HeavyAutomaticGunMod.Unequip);
        RegisterWeapon("laser eyes", LaserEyesMod.Equip, LaserEyesMod.Fire, LaserEyesMod.Unequip);
        RegisterWeapon("lava gun", LavaGunMod.Equip, LavaGunMod.Fire, LavaGunMod.Unequip);
        RegisterWeapon("chaos wand", ChaosWandMod.Equip, ChaosWandMod.Fire, ChaosWandMod.Unequip);
        RegisterWeapon("lightning gun", LightningGunMod.Equip, LightningGunMod.Fire, LightningGunMod.Unequip);
        RegisterWeapon("wobbly head gun", WobblyHeadHomingGunMod.Equip, WobblyHeadHomingGunMod.Fire, WobblyHeadHomingGunMod.Unequip);
        RegisterWeapon("banana launcher", BananaPeelLauncherMod.Equip, BananaPeelLauncherMod.Fire, BananaPeelLauncherMod.Unequip);
        RegisterWeapon("tornado gun", TornadoGunMod.Equip, TornadoGunMod.Fire, TornadoGunMod.Unequip);
        RegisterWeapon("firework minigun", FireworkMinigunMod.Equip, FireworkMinigunMod.Fire, FireworkMinigunMod.Unequip);
        RegisterWeapon("jelly gun", JellyGunMod.Equip, JellyGunMod.Fire, JellyGunMod.Unequip);
        RegisterWeapon("tunnel drill", TemporaryTunnelDrillMod.Equip, TemporaryTunnelDrillMod.Drill, TemporaryTunnelDrillMod.Unequip);
        RegisterWeapon("power sword", PowerSwordMod.Equip, PowerSwordMod.Swing, PowerSwordMod.Unequip);
        RegisterWeapon("bubble blaster", BubbleBlasterMod.Equip, BubbleBlasterMod.Fire, BubbleBlasterMod.Unequip);
        RegisterWeapon("tsunami gun", TsunamiGunMod.Equip, TsunamiGunMod.Fire, TsunamiGunMod.Unequip);
        RegisterWeapon("meteor cannon", MeteorShowerCannonMod.Equip, MeteorShowerCannonMod.Fire, MeteorShowerCannonMod.Unequip);

        Register("equip grappling hook", "guns", "equipped the grappling hook", GrapplingHookMod.Equip);
        Register("release grappling hook", "guns", "released the grappling hook", GrapplingHookMod.Release);
        Register("unequip grappling hook", "guns", "put away the grappling hook", GrapplingHookMod.Unequip);
        Register("equip shrink ray", "guns", "equipped the shrink ray", ShrinkRayMod.Equip);
        Register("toggle shrink ray", "guns", "toggled shrink and grow mode", ShrinkRayMod.ToggleMode);
        Register("restore sizes", "cleanup", "restored resized objects", ShrinkRayMod.RestoreAll);
        Register("equip portal gun", "guns", "equipped the portal gun", PortalGunMod.Equip);
        Register("place blue portal", "guns", "placed the blue portal", PortalGunMod.PlaceBlue);
        Register("place orange portal", "guns", "placed the orange portal", PortalGunMod.PlaceOrange);
        Register("clear portals", "cleanup", "cleared the portals", PortalGunMod.ClearPortals);
        Register("equip camouflage", "powers", "equipped camouflage mode", CamouflagePropHuntMod.Equip);
        Register("use searched disguise", "powers", "used the searched disguise", CamouflagePropHuntMod.UseSearchedDisguise);
        Register("remove disguise", "cleanup", "removed the disguise", CamouflagePropHuntMod.RemoveDisguise);
        Register("toggle disguise freeze", "powers", "toggled disguise freezing", CamouflagePropHuntMod.ToggleFreeze);
        Register("equip minecraft builder", "building", "equipped Minecraft building mode", MinecraftBuildingMod.Equip);
        Register("place block", "building", "placed a Minecraft block", MinecraftBuildingMod.PlaceBlock);
        Register("mine block", "building", "mined a Minecraft block", MinecraftBuildingMod.MineBlock);
        Register("clear blocks", "cleanup", "cleared Minecraft blocks", MinecraftBuildingMod.RemoveAllBlocks);
        Register("equip moses staff", "powers", "equipped the Moses Staff", MosesStaffMod.Equip);
        Register("part water", "powers", "parted the water", MosesStaffMod.PartWater);
        Register("close water paths", "cleanup", "closed the water paths", MosesStaffMod.ClosePaths);
        Register("reload machine gun", "guns", "reloaded the machine gun", HeavyAutomaticGunMod.Reload);
        Register("pop all bubbles", "cleanup", "popped all bubbles", BubbleBlasterMod.PopAll);
        Register("clear tornadoes", "cleanup", "cleared tornadoes", TornadoGunMod.ClearTornadoes);
        Register("clear fireworks", "cleanup", "cleared firework rockets", FireworkMinigunMod.ClearRockets);
        Register("clear jelly men", "cleanup", "cleared Jelly Men", JellyGunMod.ClearJellyMen);
        Register("clear tunnels", "cleanup", "cleared temporary tunnels", TemporaryTunnelDrillMod.ClearTunnels);
        Register("clear waves", "cleanup", "cleared tsunami waves", TsunamiGunMod.ClearWaves);
        Register("clear meteors", "cleanup", "cleared meteor storms", MeteorShowerCannonMod.ClearStorms);

        Register("enable npcs", "systems", "enabled wandering NPCs", StreetNpcPopulationMod.Enable);
        Register("disable npcs", "systems", "disabled wandering NPCs", StreetNpcPopulationMod.Disable);
        Register("spawn npc", "spawning", "spawned one wandering NPC", StreetNpcPopulationMod.SpawnOneNow);
        Register("enable police", "systems", "enabled police chases", PoliceChaseMod.Enable);
        Register("disable police", "systems", "disabled police chases", PoliceChaseMod.Disable);
        Register("clear wanted", "cleanup", "cleared the wanted level", PoliceChaseMod.ClearWanted);
        Register("start police chase", "systems", "started a police chase test", PoliceChaseMod.TestWanted);
        Register("enable realistic crashes", "systems", "enabled realistic car crashes", RealisticCarCrashMod.Enable);
        Register("disable realistic crashes", "systems", "disabled realistic car crashes", RealisticCarCrashMod.Disable);
        Register("enable realistic planes", "systems", "enabled realistic plane flight", RealisticPlaneFlightMod.Enable);
        Register("disable realistic planes", "systems", "disabled realistic plane flight", RealisticPlaneFlightMod.Disable);
        Register("enable fishing power", "systems", "enabled the every-fish reel", EveryFishReelMod.Enable);
        Register("disable fishing power", "systems", "disabled the every-fish reel", EveryFishReelMod.Disable);
        Register("enable mines", "systems", "enabled proximity mine dropping", ProximityMineDropperMod.Enable);
        Register("disable mines", "systems", "disabled proximity mine dropping", ProximityMineDropperMod.Disable);
        Register("clear mines", "cleanup", "removed all proximity mines", ProximityMineDropperMod.RemoveAllMines);
        Register("enable crazy cars", "systems", "enabled crazy car upgrades", PowerClothesCrazyCarsMod.EnableCars);
        Register("disable crazy cars", "systems", "disabled crazy car upgrades", PowerClothesCrazyCarsMod.DisableCars);
        Register("fire car missile", "powers", "fired the crazy car missile", PowerClothesCrazyCarsMod.FireMissile);

        Register("spawn wobbly jet", "spawning", "spawned the Wobbly Jet", BuildingJetSpawnerMod.SpawnWobblyJet);
        Register("spawn selected aircraft", "spawning", "spawned the selected aircraft", BuildingJetSpawnerMod.SpawnSelectedAircraft);
        Register("spawn building", "spawning", "spawned the selected building", BuildingJetSpawnerMod.SpawnBuilding);
        Register("undo building", "cleanup", "removed the last spawned building", BuildingJetSpawnerMod.UndoLastBuilding);
        Register("clear buildings", "cleanup", "cleared spawned buildings", BuildingJetSpawnerMod.ClearSpawnedBuildings);
        Register("spawn vehicle", "spawning", "spawned the selected vehicle", VehicleAircraftSpawnerMod.SpawnVehicle);
        Register("spawn aircraft", "spawning", "spawned the selected aircraft", VehicleAircraftSpawnerMod.SpawnAircraft);
        Register("spawn ufo", "spawning", "spawned a UFO", VehicleAircraftSpawnerMod.SpawnUfo);
        Register("spawn egg ufo", "spawning", "spawned an Egg UFO", VehicleAircraftSpawnerMod.SpawnEggUfo);
        Register("spawn rocket wing car", "spawning", "spawned the Rocket Wing Car", VehicleAircraftSpawnerMod.SpawnRocketWingCar);
        Register("spawn lambo", "spawning", "spawned the custom sports car", CustomLamboMod.SpawnLambo);
        Register("spawn artifacts", "spawning", "started spawning every artifact", ArtifactSpawnerMod.SpawnEveryArtifact);
        Register("spawn money bag", "money", "spawned the configured money bag", MoneyBagSpawnerMod.SpawnMoneyBag);
        Register("spawn prop", "spawning", "spawned the selected universal prefab", UniversalSpawnerMod.SpawnSelected);
        Register("refresh props", "spawning", "refreshed the prop catalog", PropSpawnerGunMod.RefreshCatalog);
        Register("refresh vehicles", "spawning", "refreshed the vehicle catalog", VehicleAircraftSpawnerMod.RefreshCatalog);
        Register("refresh buildings", "spawning", "refreshed the building catalog", BuildingJetSpawnerMod.RefreshCatalog);

        Register("open wobblytube", "systems", "opened WobblyTube Studio", () =>
        {
            CloseChat();
            WobblyTubeMod.OpenStudio();
        });
        Register("start recording", "systems", "toggled WobblyTube recording", () =>
        {
            CloseChat();
            WobblyTubeMod.ToggleRecording();
        });
        Register("open video folder", "systems", "opened the WobblyTube video folder", WobblyTubeMod.OpenVideosFolder);
        Register("reset command effects", "systems", "reset temporary command effects", ResetCommandEffects);
    }

    private static void RegisterWeapon(string name, Action equip, Action fire, Action unequip)
    {
        Register($"equip {name}", "guns", $"equipped the {name}", equip);
        Register($"give me {name}", "guns", $"equipped the {name}", equip);
        Register($"use {name}", "guns", $"equipped the {name}", equip);
        Register($"fire {name}", "guns", $"equipped and fired the {name}", () => { equip(); fire(); });
        Register($"shoot {name}", "guns", $"equipped and fired the {name}", () => { equip(); fire(); });
        Register($"unequip {name}", "guns", $"put away the {name}", unequip);
        Register($"put away {name}", "guns", $"put away the {name}", unequip);
    }

    private static void Register(string phrase, string category, string description, Action action)
    {
        var normalized = Normalize(phrase);
        if (Commands.ContainsKey(normalized)) return;
        var info = new CommandInfo(normalized, category, description, action);
        Commands.Add(normalized, info);
        HelpEntries.Add(info);
    }

    private static void ShowHelp(string query)
    {
        query = Normalize(query);
        var entries = HelpEntries.AsEnumerable();
        var page = 1;
        if (int.TryParse(query, out var requestedPage)) page = Mathf.Max(1, requestedPage);
        else if (!string.IsNullOrWhiteSpace(query))
            entries = entries.Where(entry => entry.Category.Contains(query) || entry.Phrase.Contains(query));

        var distinct = entries.GroupBy(entry => entry.Phrase).Select(group => group.First())
            .OrderBy(entry => entry.Category).ThenBy(entry => entry.Phrase).ToList();
        const int pageSize = 12;
        var pages = Mathf.Max(1, Mathf.CeilToInt(distinct.Count / (float)pageSize));
        page = Mathf.Clamp(page, 1, pages);
        var shown = distinct.Skip((page - 1) * pageSize).Take(pageSize)
            .Select(entry => $"• {entry.Phrase} — {entry.Description}").ToArray();
        Reply(shown.Length == 0
            ? $"No commands matched '{query}'."
            : $"Commands {page}/{pages} ({distinct.Count:N0} matches):\n{string.Join("\n", shown)}\nType help {Mathf.Min(pages, page + 1)} for the next page.");
    }

    private static void ApplyPlayerEffects()
    {
        if (!TryGetPlayerBody(out _, out var body)) return;
        if (playerFrozen)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            return;
        }
        if (playerSpeed <= 0f || Cursor.visible) return;
        var camera = Camera.main;
        var forward = camera ? Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized : body.transform.forward;
        var right = camera ? Vector3.ProjectOnPlane(camera.transform.right, Vector3.up).normalized : body.transform.right;
        var movement = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) movement += forward;
        if (Input.GetKey(KeyCode.S)) movement -= forward;
        if (Input.GetKey(KeyCode.D)) movement += right;
        if (Input.GetKey(KeyCode.A)) movement -= right;
        if (movement.sqrMagnitude > 0.01f)
            body.AddForce(movement.normalized * Mathf.Max(12f, playerSpeed * 4f), ForceMode.Acceleration);
        var horizontal = Vector3.ProjectOnPlane(body.velocity, Vector3.up);
        if (horizontal.sqrMagnitude > playerSpeed * playerSpeed)
            body.velocity = horizontal.normalized * playerSpeed + Vector3.Project(body.velocity, Vector3.up);
    }

    private static void TeleportPlayer(Vector3 offset, string direction)
    {
        if (!TryGetPlayerBody(out var character, out var body))
        {
            Reply("Enter a world before teleporting.");
            return;
        }
        character.transform.position += offset;
        body.position += offset;
        Reply($"Teleported you {direction} {offset.magnitude:0.#} units.");
    }

    private static bool TryGetPlayerBody(out PlayerCharacter character, out Rigidbody body)
    {
        character = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        var playerBody = character ? character.GetComponentInChildren<PlayerBody>(true) : null;
        body = playerBody ? playerBody.GetRigidbody() : null;
        return character && body;
    }

    private static PlayerControllerEmployment LocalEmployment()
    {
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        return controller ? controller.GetComponent<PlayerControllerEmployment>() ??
                            controller.GetComponentInChildren<PlayerControllerEmployment>(true) : null;
    }

    private static PlayerVehicle CurrentVehicle()
    {
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var entered = controller?.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
        var vehicle = entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
        return vehicle && vehicle.GetDriverPlayerController() == controller ? vehicle : null;
    }

    private static void QueueSave()
    {
        try
        {
            if (SaveGameManager.InstanceExists) SaveGameManager.InstanceRaw.ForceSaveNextFrame();
        }
        catch (Exception exception) { Plugin.Log?.LogWarning($"Command Chat save request failed: {exception.Message}"); }
    }

    private static bool CanRunCommands()
        => PropSpawnManager.IsServer && GameInstance.InstanceExists &&
           GameInstance.Instance.GetFirstLocalPlayerController();

    private static bool TryNumber(string value, out float number)
    {
        number = 0f;
        var match = NumberRegex.Match(value ?? "");
        return match.Success && float.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string Normalize(string value)
    {
        value = (value ?? "").Trim().TrimStart('/').ToLowerInvariant();
        value = Regex.Replace(value, @"[^a-z0-9\.\-]+", " ");
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static void EnsureInitialized()
    {
        if (initialized) return;
        originalGravity = Physics.gravity;
        originalTimeScale = Time.timeScale;
        RegisterCommands();
        AddMessage("Command Chat",
            $"Ready with {Commands.Count:N0} host-only commands. Ask for money or speed naturally, or type help.", false);
        initialized = true;
    }

    private static void Reply(string text)
    {
        AddMessage("Command Chat", text, false);
        Status.Value = text.Replace("\n", " ");
    }

    private static void AddMessage(string sender, string text, bool isHost)
    {
        Messages.Add(new ChatMessage(sender, text, isHost));
        while (Messages.Count > MaximumMessages) Messages.RemoveAt(0);
        scrollToBottom = true;
    }

    private static void CloseChat()
    {
        visible = false;
        if (!cursorStateSaved) return;
        Cursor.visible = previousCursorVisible;
        Cursor.lockState = previousCursorLock;
        cursorStateSaved = false;
    }

    private sealed class CommandInfo
    {
        internal readonly string Phrase;
        internal readonly string Category;
        internal readonly string Description;
        internal readonly Action Action;
        internal CommandInfo(string phrase, string category, string description, Action action)
        {
            Phrase = phrase;
            Category = category;
            Description = description;
            Action = action;
        }
    }

    private readonly struct ChatMessage
    {
        internal readonly string Sender;
        internal readonly string Text;
        internal readonly bool IsHost;
        internal ChatMessage(string sender, string text, bool isHost)
        {
            Sender = sender;
            Text = text;
            IsHost = isHost;
        }
    }
}

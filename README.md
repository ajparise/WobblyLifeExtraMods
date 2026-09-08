# Wobbly Life Extra Mods

An extension plugin for the installed **lstwoMODS Wobbly Life** mod menu. Version 1.5.0 adds these panels to the existing **Extra Mods** page:

- **Universal Spawner (Extra):** compact searchable access to scanned GameObject prefabs.
- **Wind Cannon:** an equippable, crosshair-aimed cone impulse that launches physics objects.
- **Prop Spawner Gun:** an equippable, searchable placement gun that spawns the selected prefab at the crosshair hit point.
- **Custom Money Bag:** spawns the game's real cashable money-bag prefab with an adjustable value.
- **Vehicle & Aircraft Spawner:** searchable networked vehicle and aircraft lists, including dedicated UFO and Egg UFO buttons.
- **Rocket Prop Launcher:** a rapid-fire gun for small searchable props, plus an impact-detonating TNT rocket mode.
- **Realistic Plane Flight:** full 360-degree backflips, front flips, and directional barrel rolls using normal plane steering.
- **Paintball Gun:** custom RGB paintballs with optional player aim assist, rapid fire, forced player respawns, and vehicle destruction.
- **Minecraft Building Mode:** session-based grid construction with nine block styles, placement, mining, block picking, and break fragments.
- **Heavy Automatic Gun:** a visible full-auto weapon with a 300-round magazine, infinite reserve ammo, gunshot audio, and animated or instant reloads.
- **Street NPC Population:** randomized civilian Wobblies that spawn on nearby walkable streets, wander, and recycle at a safe distance.
- **Police Chase Mode:** a five-star wanted system triggered by NPC collisions, weapon harassment, and speeding, with NavMesh police pursuit and arrests.
- **Grappling Hook:** a toggle-fired, unlimited-range grappling hook with a visible claw, automatic reeling, and temporary wall-impact noclip protection.
- **Power Clothes & Crazy Cars:** powered outfits and extreme vehicle abilities including speed, selective wall noclip, invisibility, missiles, and car jetpacks.
- **Shrink Ray:** switch between shrink and grow modes with Q, restore individual targets, and resize world objects including vehicles, NPCs, buildings, trees, and roads.
- **Every Artifact Spawner:** spawns one copy of all 69 genuine networked museum artifacts in organized rows near the player.
- **Realistic Car Crashes:** adds impact-speed damage, momentum loss, off-center spin, and severe-crash occupant ragdolls to road vehicles.

## Requirements

- Wobbly Life for Windows
- BepInEx 5
- lstwoMODS Core 2.1.1 or compatible
- lstwoMODS Wobbly Life 1.2.0 or compatible

The project defaults to this installation:

`C:\Program Files (x86)\Steam\steamapps\common\Wobbly Life`

Override it when needed:

```powershell
dotnet build -c Release -p:WobblyLifeDir="D:\SteamLibrary\steamapps\common\Wobbly Life"
```

## Install

Download `WobblyLifeExtraMods.dll` from the latest GitHub release, then copy it into:

`<Wobbly Life>\BepInEx\plugins\WobblyLifeExtraMods\`

Launch Wobbly Life, enter the main menu once so lstwoMODS can prepare its asset database, then press **F2** and open **Extra Mods → Universal Spawner (Extra)**.

For the wind cannon, open **Extra Mods → Wind Cannon**, choose **Equip**, close the menu, aim with the cyan crosshair, and left-click. Reopen the menu and choose **Unequip** when finished. Gust velocity, range, cone angle, cooldown, and wall blocking are adjustable. Gust velocity ignores object mass so vehicles move properly, while every rigidbody belonging to the firing player's character is excluded.

For the prop gun, open **Extra Mods → Prop Spawner Gun** and type in the prefab dropdown to search. Select a result, choose **Equip**, close F2, then aim with the green crosshair and hold left-click for rapid fire. The prop is placed on the targeted surface or at the configured maximum distance when no surface is hit. The crosshair defaults slightly right and down; both offsets are adjustable and the placement ray follows the marker exactly. Equipping either gun automatically unequips the other.

For a custom money bag, open **Extra Mods → Custom Money Bag**, set **Bag amount**, and click **Spawn custom money bag**. The host creates the genuine networked prefab three metres in front of the camera and synchronizes its cash-in value. Take it to the bank normally.

For vehicles, open **Extra Mods → Vehicle & Aircraft Spawner**. Search the vehicle or aircraft list by typing, then either spawn once or equip its rapid-fire gun. UFO and Egg UFO have dedicated spawn and gun buttons. Close F2, aim with the amber crosshair, and hold left-click. The rapid-fire interval and crosshair offsets are adjustable; the spawn ray follows the marker. Functional vehicle spawning is host/offline only.

For prop rockets, open **Extra Mods → Rocket Prop Launcher**, search for a small prop such as a fish, bomb, TNT, or food item, and choose **Equip**. Close F2 and hold left-click to launch rapidly along the red-orange crosshair. Enable **Contact bomb mode** to override the selected prop with networked TNT that detonates on its first world collision. Velocity, fire interval, muzzle distance, and crosshair offsets are adjustable. This launcher is host/offline only.

For aerobatics, open **Extra Mods → Realistic Plane Flight** and choose **Enable flight mode**, then enter and pilot a normal plane. Automatic first person activates when you enter the pilot seat and restores your previous camera setting after you exit. Aim strongly upward to commit to one full backflip, downward for a front flip, or left/right for one barrel roll. Recenter the controls before triggering another maneuver, preventing an held input from causing a constant spin. Activation angle, flip speed, roll speed, cross-axis freedom, and automatic first person are adjustable. This mode is intended for offline play or a lobby you host.

For paintballs, open **Extra Mods → Paintball Gun**, adjust the red, green, and blue sliders, and choose **Equip**. Close F2 and left-click to fire along the colored crosshair. Optional player aimbot selects the closest visible player within the configured angle and range. Rapid fire changes left-click from one shot per press to continuous fire. Impacts create an irregular surface splatter with radial streaks, satellite drops, and a brief burst of airborne paint; splatter size and lifetime are adjustable. On a host/offline game, enabled hit effects force another player through the native respawn flow and apply maximum synchronized destruction damage to a hit vehicle.

For building, open **Extra Mods → Minecraft Building Mode**, select a block type, and choose **Equip**. Close F2, then left-click to place a grid-snapped block, right-click to mine a block created by this mode, or middle-click to copy an aimed block's type. Rapid building and mining, block size, range, action intervals, break fragments, and crosshair offsets are adjustable. **Remove all placed blocks** clears the current build. Blocks exist locally for the current game session and are not saved or synchronized to other players.

For the heavy gun, open **Extra Mods → Heavy Automatic Gun** and choose **Equip**. The large gun attaches to the local Wobbly's right-hand grab anchor, requests the arm's pointing pose once, and keeps its barrel aligned with the crosshair without repeated pose/network updates. Close F2 and hold left-click for fully automatic fire; press **R** to reload before the 300-round magazine is empty. Reserve ammunition is infinite. With **Animated reload** enabled, firing locks while the gun lowers and the visible magazine drops and reseats over the configured duration. Disable it for an immediate 300-round refill. Fire rate, range, physics knockback, gunshot volume, shot effects, and crosshair offsets are adjustable. Shot effects use lightweight non-physics hit flashes to avoid simulation stutter during long bursts. Closing the world automatically unequips the weapon and removes its model before returning to the title screen.

For pedestrians, open **Extra Mods → Street NPC Population** after entering a save and choose **Enable population**. The offline player or lobby host creates persistent civilian Wobblies through the game's native network-prefab lifecycle. Their placement follows the gameplay camera's real world position because the game's logical PlayerCharacter root remains at the scene origin in some builds. They are placed in a close ring around the player, walk between randomized nearby destinations, and are removed/replaced after falling beyond the despawn radius. The mode uses native NavMesh where available and automatically falls back to street/ground raycasts with its own obstacle-aware walking controller. **Spawn one now** enables the population and places a grounded test NPC three metres directly ahead. Population count, spawn ring, walking speed, wander radius, and update interval are adjustable.

For police chases, open **Extra Mods → Police Chase Mode** and choose **Enable police chase**. Running into Wobblies at speed, striking NPCs with the paintball gun, heavy gun, or wind cannon, and driving above the configured speed limit adds wanted heat. The one-to-five-star level determines how many recognizable blue-uniformed officers spawn on reachable paths and pursue the player; higher levels also make them faster. After the escape delay, heat decays until the officers give up. Reaching the player triggers an arrest and optional respawn. Speed limit, officers per star, police speed, escape timing, spawn distance, and arrest distance are adjustable. Police are local/session-only and can be cleared or removed from the panel.

For realistic crashes, open **Extra Mods → Realistic Car Crashes** and choose **Enable realistic crashes**. Road vehicle impacts below the minimum threshold retain the game's normal behavior. Faster direct impacts apply proportional native vehicle damage and remove forward momentum, while off-center contact adds rotation based on the collision point. At the severe-crash threshold, occupants ragdoll and receive impact velocity. Minimum and severe speeds, damage, momentum loss, spin, and occupant ragdolls are adjustable. Synchronized vehicle damage requires an offline game or the lobby host.

Start in an offline or private host-controlled game. Local spawning is the safer default. Network spawning is rejected for catalog entries that lstwoMODS did not identify as network prefabs.

## Build from source

Install the .NET SDK, then build against your Wobbly Life installation:

```powershell
dotnet build WobblyLifeExtraMods.csproj -c Release -p:WobblyLifeDir="C:\Program Files (x86)\Steam\steamapps\common\Wobbly Life"
```

The compiled plugin is written to `bin/Release/net472/WobblyLifeExtraMods.dll`.

## Troubleshooting

Check `<Wobbly Life>\BepInEx\LogOutput.log` for `Wobbly Life Extra Mods 1.5.0 loaded`. If a searchable panel is empty, wait for the lstwoMODS asset scan to finish and click its refresh button.

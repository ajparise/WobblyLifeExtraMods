using System;
using System.Collections.Generic;
using System.Linq;
using HawkNetworking;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Spawns the game's actual space money bag with a configurable value.</summary>
public sealed class SpaceMoneyBagSpawnerMod : BaseMod
{
    private const string SpaceBagAddress = "SpaceMoneyBag";
    private static readonly Ref<string> Status = new("Enter space, choose an amount, and spawn a bag.");
    public override string Name => "Custom Space Money Bag";
    public override string Description => "Spawn the game's space money bag with a chosen amount.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 1f, Max = 1000000f, Label = "Space money amount",
        Description = "Value of each space money bag when cashed in.")]
    public static Ref<int> Amount = new(100);

    protected override void OnStaticInit() => BindData(Amount, "SpaceMoneyBagAmount", 100);
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("SpaceMoneyBagHelp", "Host or offline only. Enter the space world, choose an amount, and spawn the genuine space money bag in front of you. Pick it up and deposit it using the normal space-world money deposit. The spawner does not substitute an ordinary island money bag."),
        base.BuildPanel(id), ActionMenu(new Button("Spawn space money bag", Spawn).WithContentWidth(), nameof(Spawn)),
        new TextWrapped("SpaceMoneyBagStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)] public static void Spawn()
    {
        if (!PropSpawnManager.IsServer || !GameInstance.InstanceExists)
        { Status.Value = "Only the host or offline player can spawn a space money bag."; return; }
        var player = GameInstance.Instance.GetFirstLocalPlayerController();
        if (!IsSpacePlayer(player)) { Status.Value = "Travel to the space world first; space money uses a separate balance."; return; }
        var camera = Camera.main;
        if (!camera || !player.GetPlayerCharacter()) { Status.Value = "Player or camera not ready. Try again in space."; return; }
        var entry = AssetDatabase.Entries.FirstOrDefault(item => item.IsGameObject &&
            (string.Equals(item.LoadKey, SpaceBagAddress, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Address, SpaceBagAddress, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Name, SpaceBagAddress, StringComparison.OrdinalIgnoreCase)));
        var identity = new PropIdentity { Kind = PropSourceKind.Addressable, Address = entry?.LoadKey ?? SpaceBagAddress,
            NetworkAssetId = entry?.NetworkAssetId, Guid = entry?.Guid, AssetName = "SpaceMoneyBag", Networked = true };
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(identity.NetworkAssetId)) candidates.Add(identity.NetworkAssetId);
        candidates.AddRange(PropAddressResolver.Fallbacks(identity));
        candidates.Add(SpaceBagAddress);
        candidates = candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var value = Mathf.Clamp(Amount.Value, 1, 1000000);
        var position = camera.transform.position + camera.transform.forward * 3f;
        var rotation = Quaternion.Euler(0f, camera.transform.eulerAngles.y, 0f);
        Status.Value = $"Spawning a {value:N0} space money bag...";
        TrySpawn(candidates, 0, position, rotation, value, player, identity);
    }

    private static bool IsSpacePlayer(PlayerController player)
    {
        if (!player) return false;
        var employment = player.GetPlayerControllerEmployment();
        return employment && employment.GetType().Name == "SpacePlayerControllerEmployment";
    }

    private static void TrySpawn(IReadOnlyList<string> candidates, int index, Vector3 position, Quaternion rotation,
        int amount, PlayerController player, PropIdentity identity)
    {
        if (index >= candidates.Count) { Status.Value = "The genuine space money bag is not registered as a network prefab. Wait for the asset scan and retry."; return; }
        NetworkPrefab.SpawnNetworkPrefab(candidates[index], behaviour =>
        {
            if (!behaviour) { TrySpawn(candidates, index + 1, position, rotation, amount, player, identity); return; }
            if (!GameInstance.InstanceExists || !PropSpawnManager.IsServer || !IsSpacePlayer(player) ||
                GameInstance.Instance.GetFirstLocalPlayerController() != player)
            { RemoveSpawn(behaviour); Status.Value = "Space world changed before the bag spawned."; return; }
            var bag = behaviour.GetComponent<MoneyBag>() ?? behaviour.GetComponentInChildren<MoneyBag>(true);
            if (!bag || behaviour.networkObject == null || !behaviour.networkObject.IsServer())
            { RemoveSpawn(behaviour); Status.Value = "The space prefab did not contain a usable networked money bag."; return; }
            bag.SetMoney(amount);
            if (bag.GetMoneyRaw() != amount)
            { RemoveSpawn(behaviour); Status.Value = "The bag amount could not be set; the bag was removed."; return; }
            PropSpawnManager.Register(behaviour.gameObject, identity, null, null);
            Status.Value = $"Spawned a {amount:N0} space money bag. Pick it up and take it to the space deposit.";
            Plugin.Log?.LogInfo(Status.Value);
        }, position: position, rotation: rotation, owner: null, bUseChunkSystem: false, bSendTransform: true, bCheckChunk: false);
    }

    private static void RemoveSpawn(HawkNetworkBehaviour behaviour)
    {
        if (!behaviour) return;
        if (behaviour.networkObject != null && behaviour.networkObject.IsServer()) behaviour.networkObject.Destroy();
        else UnityEngine.Object.Destroy(behaviour.gameObject);
    }
}

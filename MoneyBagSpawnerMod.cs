using HawkNetworking;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Spawns the game's genuine cashable money bag with a chosen value.</summary>
public sealed class MoneyBagSpawnerMod : BaseMod
{
    private const string MoneyBagAddress = "Game/Prefabs/Props/MoneyBag.prefab";
    private const string MoneyBagNetworkId = "46352a69b4f986940bf8a1677107f68a";

    private static readonly Ref<string> Status = new("Choose an amount and spawn a bag.");

    public override string Name => "Custom Money Bag";

    public override string Description =>
        "Spawns a genuine cashable money bag and synchronizes the selected value through the game network.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 1f, Max = 1000000f, Label = "Bag amount",
        Description = "Money credited when this bag is cashed in.")]
    public static Ref<int> Amount = new(100);

    protected override void OnStaticInit()
    {
        BindData(Amount, "Amount", 100);
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("MoneyBagHelp",
                "Spawns the normal Wobbly Life money-bag prop a short distance in front of the camera. " +
                "Take it to the bank to receive the configured amount. Host/offline only."),
            base.BuildPanel(id),
            ActionMenu(new Button("Spawn custom money bag", SpawnMoneyBag).WithContentWidth(), nameof(SpawnMoneyBag)),
            new TextWrapped("MoneyBagStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnMoneyBag()
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the lobby host can spawn a functional money bag.";
            return;
        }

        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found. Enter a save before spawning a bag.";
            return;
        }

        var value = Mathf.Clamp(Amount.Value, 1, 1000000);
        var position = camera.transform.position + camera.transform.forward * 3f;
        var rotation = Quaternion.Euler(0f, camera.transform.eulerAngles.y, 0f);

        Status.Value = $"Spawning a ${value:N0} money bag...";

        NetworkPrefab.SpawnNetworkPrefab(
            MoneyBagNetworkId,
            behaviour => FinishSpawn(behaviour, value),
            position: position,
            rotation: rotation,
            owner: null,
            bUseChunkSystem: true,
            bSendTransform: true,
            bCheckChunk: true);
    }

    private static void FinishSpawn(HawkNetworkBehaviour behaviour, int value)
    {
        if (behaviour == null)
        {
            Status.Value = "Money bag spawn failed: the registered network prefab could not be created.";
            return;
        }

        var gameObject = behaviour.gameObject;
        var moneyBag = gameObject.GetComponent<MoneyBag>() ?? gameObject.GetComponentInChildren<MoneyBag>(true);

        if (!moneyBag)
        {
            Status.Value = "Money bag spawned, but its MoneyBag component was not found.";
            return;
        }

        moneyBag.SetMoney(value);

        var identity = new PropIdentity
        {
            Kind = PropSourceKind.Addressable,
            Address = MoneyBagAddress,
            NetworkAssetId = MoneyBagNetworkId,
            AssetName = "MoneyBag",
            Networked = true
        };

        PropSpawnManager.Register(gameObject, identity, null, null);
        Status.Value = $"Spawned a cashable ${moneyBag.GetMoney():N0} money bag.";
        Plugin.Log?.LogInfo(Status.Value);
    }
}

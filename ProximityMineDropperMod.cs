using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace WobblyLifeExtraMods;

/// <summary>A shop-bought chest item that lets its wearer drop proximity mines.</summary>
public sealed class ProximityMineDropperMod : BaseMod
{
    private const string ClothingGuidText = "914237c8-a57c-4c67-a8ca-4777d98f1241";
    private const string ClothingAssetGuid = "6f7df58f78ab4cd68fd597a65f38427d";
    private static readonly Guid ClothingGuid = new(ClothingGuidText);
    private static readonly Ref<bool> EnabledState = new();
    private static readonly Ref<string> Status = new("Mine Dropper is disabled.");
    private static readonly HashSet<ProximityMineBehaviour> Mines = new();
    private static readonly Collider[] TriggerBuffer = new Collider[128];
    private static readonly HashSet<PlayerVehicle> VehicleBuffer = new();
    private static readonly HashSet<RagdollController> WobblyBuffer = new();
    private static readonly AccessTools.FieldRef<ShopClothesTopCatalog, ShopClothingItem[]> TopItems =
        AccessTools.FieldRefAccess<ShopClothesTopCatalog, ShopClothingItem[]>("clothingPieces");

    private static AssetReference shopAssetReference;
    private static ClothingPiece clothingPrefab;
    private static ClothingAssetReference clothingReference;
    private static ClothingManager registeredManager;
    private static bool clothingLoadPending;
    private static float nextShopScan;
    private static float nextDrop;
    private static Material mineBaseMaterial;
    private static Material mineLightMaterial;

    public override string Name => "Proximity Mine Dropper";
    public override string Description =>
        "Adds a $100 Mine Dropper chest item to clothing stores; wear it and press Q to drop proximity mines.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 0.4f, Max = 3f, Label = "Mine trigger radius")]
    public static Ref<float> TriggerRadius = new(1.75f);

    [ModSetting(Order = 20, Min = 0.2f, Max = 3f, Label = "Arming delay")]
    public static Ref<float> ArmingDelay = new(0.75f);

    [ModSetting(Order = 30, Min = 15f, Max = 250f, Label = "Mine despawn distance")]
    public static Ref<float> DespawnDistance = new(80f);

    [ModSetting(Order = 40, Min = 20f, Max = 150f, Label = "Wobbly launch speed")]
    public static Ref<float> WobblyLaunchSpeed = new(70f);

    [ModSetting(Order = 50, Min = 1f, Max = 40f, Label = "Maximum active mines")]
    public static Ref<float> MaximumMines = new(20f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("MineDropperHelp",
                "Enable the mod, then buy the Mine Dropper chest item for $100 from any clothing shop and wear it. " +
                "Close F2 and press Q to drop a mine. It arms after a short safety delay, launches Wobblies skyward, " +
                "instantly destroys cars without creating a rusty replacement, and despawns when left behind. Host/offline only."),
            base.BuildPanel(id),
            new HStack("MineDropperActions",
                ActionMenu(new Button("Enable and add to shops", Enable), nameof(Enable)),
                ActionMenu(new Button("Disable and remove mines", Disable), nameof(Disable)),
                ActionMenu(new Button("Remove all mines", RemoveAllMines), nameof(RemoveAllMines))
            ).WithContentWidth(),
            new TextWrapped("MineDropperStatus", "").WithText(Status));
    }

    protected override void OnStaticInit()
    {
        shopAssetReference = new AssetReference(ClothingAssetGuid);
        var harmony = new Harmony(Plugin.Guid + ".proximity-mine-dropper");
        harmony.PatchAll(typeof(RuntimeClothingReferencePatch));
        harmony.PatchAll(typeof(ClothesShopPatch));
    }

    [ModAction(ShowInUI = false)]
    public static void Enable()
    {
        EnabledState.Value = true;
        nextShopScan = 0f;
        EnsureClothingRegistered();
        Status.Value = clothingReference == null
            ? "Mine Dropper enabled. Preparing the $100 chest item for every clothing shop..."
            : "Mine Dropper enabled. Buy and wear its $100 chest item, then press Q to drop a mine.";
    }

    [ModAction(ShowInUI = false)]
    public static void Disable()
    {
        EnabledState.Value = false;
        RemoveAllMines();
        Status.Value = "Mine Dropper disabled. The purchased chest item remains in your wardrobe.";
    }

    [ModAction(ShowInUI = false)]
    public static void RemoveAllMines()
    {
        foreach (var mine in Mines.ToArray())
            if (mine) UnityEngine.Object.Destroy(mine.gameObject);
        Mines.Clear();
        if (EnabledState.Value) Status.Value = "All active mines removed.";
    }

    public override void Update()
    {
        EnsureClothingRegistered();
        if (!EnabledState.Value) return;

        if (Time.unscaledTime >= nextShopScan)
        {
            nextShopScan = Time.unscaledTime + 2f;
            AddItemToEveryLoadedShop();
        }

        var controller = LocalPlayer();
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!character)
        {
            if (Mines.Count > 0) RemoveAllMines();
            return;
        }

        RestoreSavedClothingIfNeeded(controller, character);
        CleanupMineSet();
        if (Cursor.visible || LaserEyesMod.IsEquipped || CamouflagePropHuntMod.IsEquipped ||
            !Input.GetKeyDown(KeyCode.Q)) return;

        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can drop synchronized mines.";
            return;
        }

        var customize = character.GetPlayerCharacterCustomize();
        if (!customize || !customize.IsWearing(ClothingGuid))
        {
            Status.Value = "Buy and wear the $100 Mine Dropper chest item before pressing Q.";
            return;
        }

        if (Time.unscaledTime < nextDrop) return;
        nextDrop = Time.unscaledTime + 0.25f;
        DropMine(controller, character);
    }

    private static void EnsureClothingRegistered()
    {
        if (!UnitySingleton<ClothingManager>.InstanceExists) return;
        var manager = UnitySingleton<ClothingManager>.Instance;

        if (clothingPrefab)
        {
            if (registeredManager != manager || !manager.IsRegistered(ClothingGuid))
            {
                manager.Register(clothingPrefab);
                registeredManager = manager;
                clothingReference = manager.GetClothingReference(ClothingGuid);
            }
            return;
        }

        if (clothingLoadPending) return;
        var allClothing = manager.GetAllClothingReferences();
        var source = allClothing?.FirstOrDefault(reference => reference != null &&
                                                          reference.selectionType == ClothingSelectionType.Top &&
                                                          reference.clothingPrefab != null &&
                                                          reference.clothingPrefab.IsValid());
        if (source == null) return;

        clothingLoadPending = true;
        source.clothingPrefab.InstantiateAsync(created =>
        {
            clothingLoadPending = false;
            if (!created) return;
            var piece = created.GetComponent<ClothingPiece>();
            var guid = created.GetComponent<GUIDComponent>();
            if (!piece || !guid)
            {
                UnityEngine.Object.Destroy(created);
                return;
            }

            created.name = "Mine Dropper Vest";
            guid.SetGUID(ClothingGuid);
            piece.SetPrimaryColor(new Color(0.12f, 0.18f, 0.09f, 1f));
            created.transform.position = new Vector3(0f, -10000f, 0f);
            UnityEngine.Object.DontDestroyOnLoad(created);
            clothingPrefab = piece;

            if (UnitySingleton<ClothingManager>.InstanceExists)
            {
                registeredManager = UnitySingleton<ClothingManager>.Instance;
                registeredManager.Register(clothingPrefab);
                clothingReference = registeredManager.GetClothingReference(ClothingGuid);
                AddItemToEveryLoadedShop();
                if (EnabledState.Value)
                    Status.Value = "Mine Dropper chest item is available for $100 in every loaded clothing shop.";
            }
        }, null, true);
    }

    private static void AddItemToEveryLoadedShop()
    {
        if (!EnabledState.Value || clothingReference == null) return;
        foreach (var shop in Resources.FindObjectsOfTypeAll<ClothesShop>())
        {
            if (!shop) continue;
            AddItemToShop(shop);
        }
    }

    internal static void AddItemToShop(ClothesShop shop)
    {
        if (!EnabledState.Value || clothingReference == null || !shop) return;
        var topCatalog = shop.GetCatalog(ClothingSelectionType.Top) as ShopClothesTopCatalog;
        if (!topCatalog) return;
        var items = TopItems(topCatalog) ?? Array.Empty<ShopClothingItem>();
        if (items.Any(IsMineShopItem)) return;
        var updated = new ShopClothingItem[items.Length + 1];
        Array.Copy(items, updated, items.Length);
        updated[items.Length] = new ShopClothingItem
        {
            clothingPrefabReference = shopAssetReference,
            itemPrice = 100
        };
        TopItems(topCatalog) = updated;
    }

    private static bool IsMineShopItem(ShopClothingItem item) =>
        item?.clothingPrefabReference != null && item.clothingPrefabReference.AssetGUID == ClothingAssetGuid;

    private static void RestoreSavedClothingIfNeeded(PlayerController controller, PlayerCharacter character)
    {
        if (clothingReference == null || !controller || !character) return;
        var persistent = controller.GetPlayerPersistentData();
        var customize = character.GetPlayerCharacterCustomize();
        if (persistent == null || !customize || customize.IsWearing(ClothingGuid)) return;
        if (persistent.CurrentClothes.ClothingTop.clothingPrefabGUID == ClothingGuid)
            controller.ServerLoadSavedClothesOnActivePlayer(ClothingSelectionType.Top, true);
    }

    private static void DropMine(PlayerController controller, PlayerCharacter character)
    {
        CleanupMineSet();
        var maxMines = Mathf.Clamp(Mathf.RoundToInt(MaximumMines.Value), 1, 40);
        if (Mines.Count >= maxMines)
        {
            var oldest = Mines.Where(mine => mine).OrderBy(mine => mine.SpawnTime).FirstOrDefault();
            if (oldest) UnityEngine.Object.Destroy(oldest.gameObject);
        }

        var vehicle = CurrentVehicle(controller);
        var body = character.GetComponentInChildren<PlayerBody>(true)?.GetRigidbody();
        var anchor = vehicle ? vehicle.transform.position : body ? body.worldCenterOfMass : character.transform.position;
        var forward = vehicle ? vehicle.transform.forward : Camera.main
            ? Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up).normalized
            : character.transform.forward;
        if (forward.sqrMagnitude < 0.1f) forward = character.transform.forward;
        var candidate = anchor - forward * (vehicle ? 2.5f : 1.1f) + Vector3.up * 1.5f;
        var position = FindGround(candidate, character.transform, vehicle ? vehicle.transform : null);

        var mineObject = CreateMineVisual(position);
        var behaviour = mineObject.AddComponent<ProximityMineBehaviour>();
        behaviour.Configure(character, vehicle);
        Mines.Add(behaviour);
        Status.Value = $"Mine dropped and arming. Active mines: {Mines.Count}/{maxMines}.";
    }

    private static Vector3 FindGround(Vector3 origin, Transform character, Transform vehicle)
    {
        var hits = Physics.RaycastAll(origin + Vector3.up * 2f, Vector3.down, 8f, ~0,
            QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider) continue;
            if (character && hit.transform.IsChildOf(character)) continue;
            if (vehicle && hit.transform.IsChildOf(vehicle)) continue;
            return hit.point + hit.normal * 0.08f;
        }
        return origin - Vector3.up * 1.4f;
    }

    private static GameObject CreateMineVisual(Vector3 position)
    {
        EnsureMineMaterials();
        var root = new GameObject("Mine Dropper Proximity Mine");
        root.transform.position = position;

        var baseDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        baseDisc.name = "Mine Body";
        baseDisc.transform.SetParent(root.transform, false);
        baseDisc.transform.localScale = new Vector3(0.72f, 0.09f, 0.72f);
        DisableCollider(baseDisc);
        baseDisc.GetComponent<Renderer>().sharedMaterial = mineBaseMaterial;

        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "Warning Ring";
        ring.transform.SetParent(root.transform, false);
        ring.transform.localPosition = Vector3.up * 0.11f;
        ring.transform.localScale = new Vector3(0.48f, 0.035f, 0.48f);
        DisableCollider(ring);
        ring.GetComponent<Renderer>().sharedMaterial = mineLightMaterial;

        var light = root.AddComponent<Light>();
        light.color = new Color(1f, 0.06f, 0.02f);
        light.range = 2.5f;
        light.intensity = 0.25f;
        return root;
    }

    private static void DisableCollider(GameObject obj)
    {
        var collider = obj.GetComponent<Collider>();
        if (collider) collider.enabled = false;
    }

    private static void EnsureMineMaterials()
    {
        if (!mineBaseMaterial)
        {
            mineBaseMaterial = new Material(Shader.Find("Sprites/Default"));
            mineBaseMaterial.color = new Color(0.055f, 0.065f, 0.055f, 1f);
        }
        if (!mineLightMaterial)
        {
            mineLightMaterial = new Material(Shader.Find("Sprites/Default"));
            mineLightMaterial.color = new Color(0.95f, 0.035f, 0.01f, 1f);
        }
    }

    internal static bool ShouldDespawnMine(Vector3 position)
    {
        var controller = LocalPlayer();
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!EnabledState.Value || !character) return true;
        var playerBody = character.GetComponentInChildren<PlayerBody>(true)?.GetRigidbody();
        var playerPosition = playerBody ? playerBody.worldCenterOfMass : character.transform.position;
        var distance = Mathf.Max(15f, DespawnDistance.Value);
        return (position - playerPosition).sqrMagnitude > distance * distance;
    }

    internal static void CheckMineTrigger(ProximityMineBehaviour mine)
    {
        VehicleBuffer.Clear();
        WobblyBuffer.Clear();
        var count = Physics.OverlapSphereNonAlloc(mine.transform.position, Mathf.Clamp(TriggerRadius.Value, 0.4f, 3f),
            TriggerBuffer, ~0, QueryTriggerInteraction.Collide);
        var ownerStillNear = false;
        for (var i = 0; i < count; i++)
        {
            var collider = TriggerBuffer[i];
            TriggerBuffer[i] = null;
            if (!collider || collider.transform.IsChildOf(mine.transform)) continue;
            var vehicle = collider.GetComponentInParent<PlayerVehicle>();
            if (!mine.OwnerCleared && vehicle && vehicle == mine.OwnerVehicle) ownerStillNear = true;
            if (vehicle && (mine.OwnerCleared || vehicle != mine.OwnerVehicle)) VehicleBuffer.Add(vehicle);
            var ragdoll = collider.GetComponentInParent<RagdollController>();
            if (!mine.OwnerCleared && ragdoll && ragdoll == mine.OwnerRagdoll) ownerStillNear = true;
            if (ragdoll && (mine.OwnerCleared || ragdoll != mine.OwnerRagdoll)) WobblyBuffer.Add(ragdoll);
        }
        if (!mine.OwnerCleared && !ownerStillNear) mine.OwnerCleared = true;
        if (VehicleBuffer.Count == 0 && WobblyBuffer.Count == 0) return;
        Detonate(mine, VehicleBuffer.ToArray(), WobblyBuffer.ToArray());
    }

    private static void Detonate(ProximityMineBehaviour mine, PlayerVehicle[] vehicles, RagdollController[] wobblies)
    {
        if (!mine || mine.HasDetonated) return;
        mine.HasDetonated = true;
        var position = mine.transform.position;

        ApplyBlastForce(position, mine);

        foreach (var ragdoll in wobblies)
        {
            if (!ragdoll) continue;
            ragdoll.Ragdoll();
            var body = ragdoll.GetComponentInChildren<PlayerBody>(true);
            if (!body) continue;
            var away = Vector3.ProjectOnPlane(body.transform.position - position, Vector3.up).normalized;
            body.SetRagdollVelocity(Vector3.up * Mathf.Max(20f, WobblyLaunchSpeed.Value) + away * 8f);
        }

        foreach (var vehicle in vehicles)
        {
            if (!vehicle) continue;
            var destructable = vehicle.GetComponent<PlayerVehicleDestructable>();
            if (destructable) destructable.ServerDamage(short.MaxValue, false, true);
            Plugin.RunCoroutine(RemoveDestroyedVehicle(vehicle));
        }

        CreateExplosionFlash(position);
        Status.Value = vehicles.Length > 0
            ? $"Mine detonated: {vehicles.Length} car(s) instantly destroyed; wreck removed."
            : $"Mine detonated: {wobblies.Length} Wobbly/Wobblies launched skyward.";
        UnityEngine.Object.Destroy(mine.gameObject);
    }

    private static IEnumerator RemoveDestroyedVehicle(PlayerVehicle vehicle)
    {
        yield return new WaitForSeconds(0.18f);
        if (vehicle) vehicle.DestroyGameObject();
    }

    private static void ApplyBlastForce(Vector3 position, ProximityMineBehaviour mine)
    {
        foreach (var collider in Physics.OverlapSphere(position, 5f, ~0, QueryTriggerInteraction.Ignore))
        {
            var body = collider.attachedRigidbody;
            if (!body || body.isKinematic || body.transform.IsChildOf(mine.transform)) continue;
            body.AddExplosionForce(900f, position, 5f, 2f, ForceMode.Impulse);
        }
    }

    private static void CreateExplosionFlash(Vector3 position)
    {
        var flash = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        flash.name = "Mine Explosion Flash";
        flash.transform.position = position + Vector3.up * 0.3f;
        flash.transform.localScale = Vector3.one * 0.4f;
        DisableCollider(flash);
        var material = new Material(Shader.Find("Sprites/Default"));
        material.color = new Color(1f, 0.2f, 0.015f, 0.9f);
        flash.GetComponent<Renderer>().material = material;
        Plugin.RunCoroutine(AnimateExplosionFlash(flash, material));
    }

    private static IEnumerator AnimateExplosionFlash(GameObject flash, Material material)
    {
        var elapsed = 0f;
        while (flash && elapsed < 0.38f)
        {
            elapsed += Time.deltaTime;
            var progress = Mathf.Clamp01(elapsed / 0.38f);
            flash.transform.localScale = Vector3.one * Mathf.Lerp(0.4f, 7f, progress);
            material.color = new Color(1f, Mathf.Lerp(0.25f, 0.02f, progress), 0.01f, 1f - progress);
            yield return null;
        }
        if (flash) UnityEngine.Object.Destroy(flash);
        if (material) UnityEngine.Object.Destroy(material);
    }

    private static PlayerController LocalPlayer() =>
        GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;

    private static PlayerVehicle CurrentVehicle(PlayerController controller)
    {
        var entered = controller?.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
        return entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
    }

    private static void CleanupMineSet() => Mines.RemoveWhere(mine => !mine);

    internal static void UnregisterMine(ProximityMineBehaviour mine)
    {
        if (mine != null) Mines.Remove(mine);
    }

    [HarmonyPatch(typeof(ClothingManager), nameof(ClothingManager.GetClothingReference), typeof(AssetReference))]
    private static class RuntimeClothingReferencePatch
    {
        private static void Postfix(AssetReference clothingPrefab, ref ClothingAssetReference __result)
        {
            if (__result == null && clothingPrefab != null && clothingPrefab.AssetGUID == ClothingAssetGuid)
                __result = clothingReference;
        }
    }

    [HarmonyPatch(typeof(ClothesShop), nameof(ClothesShop.StartShopping))]
    private static class ClothesShopPatch
    {
        private static void Prefix(ClothesShop __instance) => AddItemToShop(__instance);
    }
}

internal sealed class ProximityMineBehaviour : MonoBehaviour
{
    private float armedAt;
    private Light warningLight;
    internal float SpawnTime { get; private set; }
    internal bool HasDetonated { get; set; }
    internal bool OwnerCleared { get; set; }
    internal RagdollController OwnerRagdoll { get; private set; }
    internal PlayerVehicle OwnerVehicle { get; private set; }

    internal void Configure(PlayerCharacter owner, PlayerVehicle ownerVehicle)
    {
        SpawnTime = Time.unscaledTime;
        armedAt = SpawnTime + Mathf.Max(0.2f, ProximityMineDropperMod.ArmingDelay.Value);
        warningLight = GetComponent<Light>();
        OwnerRagdoll = owner ? owner.GetRagdollController() : null;
        OwnerVehicle = ownerVehicle;
    }

    private void Update()
    {
        if (ProximityMineDropperMod.ShouldDespawnMine(transform.position))
        {
            Destroy(gameObject);
            return;
        }
        if (warningLight)
        {
            var armed = Time.unscaledTime >= armedAt;
            warningLight.intensity = armed
                ? 1.1f + Mathf.Sin(Time.unscaledTime * 13f) * 0.55f
                : 0.2f;
        }
    }

    private void FixedUpdate()
    {
        if (!HasDetonated && Time.unscaledTime >= armedAt)
            ProximityMineDropperMod.CheckMineTrigger(this);
    }

    private void OnDestroy() => ProximityMineDropperMod.UnregisterMine(this);
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace WobblyLifeExtraMods;

/// <summary>Prop-hunt camouflage copied from an aimed world object or a searchable prefab.</summary>
public sealed class CamouflagePropHuntMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Camouflage mode is unequipped.");
    private static readonly Ref<string[]> SearchItems = new(Array.Empty<string>());
    private static readonly Ref<int> SearchIndex = new();
    private static readonly Dictionary<Renderer, bool> HiddenTargetRenderers = new();
    private static readonly Dictionary<Collider, bool> HiddenTargetColliders = new();
    private static readonly Dictionary<Renderer, bool> PlayerRenderers = new();
    private static readonly List<Mesh> RuntimeMeshes = new();
    private static List<AssetDatabase.AssetEntry> entries = new();
    private static GameObject activeVisual;
    private static GameObject activeTarget;
    private static GameObject addressableSource;
    private static PlayerCharacter disguisedCharacter;
    private static Rigidbody disguisedBody;
    private static CamouflageFreezeAnchor freezeAnchor;
    private static PlayerControllerInputManager lockedInputManager;
    private static readonly object MovementLockHandle = new();
    private static bool networkBodyHidden;
    private static Vector3 visualPositionOffset;
    private static Quaternion visualRotationOffset = Quaternion.identity;
    private static bool loadPending;

    public override string Name => "Camouflage / Prop Hunt";
    public override string Description =>
        "Copy an aimed object or searchable prefab, hide inside its appearance, and replace/restore aimed world objects.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    internal static bool IsEquipped => EquippedState.Value;

    [ModSetting(Order = 10, Min = 5f, Max = 250f, Label = "Aim range")]
    public static Ref<float> AimRange = new(80f);

    [ModSetting(Order = 20, Min = 0.2f, Max = 3f, Label = "Disguise scale")]
    public static Ref<float> DisguiseScale = new(1f);

    [ModSetting(Order = 30, Min = 10f, Max = 250f, Label = "Maximum copied renderers")]
    public static Ref<float> MaximumRenderers = new(100f);

    [ModSetting(Order = 40, Label = "Hide replaced object's colliders")]
    public static Ref<bool> HideTargetColliders = new(true);

    [ModSetting(Order = 50, Min = -300f, Max = 300f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(0f);

    [ModSetting(Order = 60, Min = -300f, Max = 300f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(0f);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += RefreshSearch;
        if (AssetDatabase.IsInitialized) RefreshSearch();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("CamouflageHelp",
                "Equip and close F2. Aim at an object and press Q to copy its appearance, temporarily replace/hide it, " +
                "and teleport into its position. Movement is completely locked while disguised so you stay hidden. " +
                "Press R to remove the disguise, restore movement, and restore the replaced object. " +
                "You can also search below and apply a prefab disguise without teleporting."),
            new SearchableCombo("Search disguise", Array.Empty<string>())
                .WithItems(SearchItems)
                .WithSelectedIndex(SearchIndex),
            base.BuildPanel(id),
            new HStack("CamouflageActions",
                ActionMenu(new Button("Equip camouflage", Equip), nameof(Equip)),
                ActionMenu(new Button("Use searched disguise", UseSearchedDisguise), nameof(UseSearchedDisguise)),
                ActionMenu(new Button("Remove disguise", RemoveDisguise), nameof(RemoveDisguise))
            ).WithContentWidth(),
            new HStack("CamouflageUtilityActions",
                ActionMenu(new Button("Lock hiding position", ToggleFreeze), nameof(ToggleFreeze)),
                ActionMenu(new Button("Unequip and restore", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Refresh search list", RefreshSearch), nameof(RefreshSearch))
            ).WithContentWidth(),
            new TextWrapped("CamouflageStatus", "").WithText(Status));
    }

    [ModAction(ShowInUI = false)]
    public static void Equip()
    {
        WindCannonMod.Unequip();
        PropSpawnerGunMod.Unequip();
        VehicleAircraftSpawnerMod.Unequip();
        RocketLauncherMod.Unequip();
        PaintballGunMod.Unequip();
        MinecraftBuildingMod.Unequip();
        HeavyAutomaticGunMod.Unequip();
        GrapplingHookMod.Unequip();
        ShrinkRayMod.Unequip();
        LaserEyesMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        LightningGunMod.Unequip();
        WobblyHeadHomingGunMod.Unequip();
        BananaPeelLauncherMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Camouflage equipped. Aim and press Q; movement locks until you press R to restore.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        ClearDisguise();
        EquippedState.Value = false;
        Status.Value = "Camouflage unequipped; your Wobbly and replaced object were restored.";
    }

    [ModAction(ShowInUI = false)]
    public static void RemoveDisguise()
    {
        ClearDisguise();
        Status.Value = EquippedState.Value
            ? "Disguise removed and replaced object restored. Aim and press Q to copy another."
            : "Disguise removed.";
    }

    [ModAction(ShowInUI = false)]
    public static void ToggleFreeze()
    {
        if (!activeVisual || !disguisedBody)
        {
            Status.Value = "Choose a disguise before using hiding freeze.";
            return;
        }
        if (!freezeAnchor) freezeAnchor = disguisedBody.gameObject.AddComponent<CamouflageFreezeAnchor>();
        freezeAnchor.SetFrozen(true);
        Status.Value = "Movement is locked while hiding. Press R to restore your Wobbly and move again.";
    }

    [ModAction(ShowInUI = false)]
    public static void RefreshSearch()
    {
        var previousKey = SearchIndex.Value >= 0 && SearchIndex.Value < entries.Count
            ? entries[SearchIndex.Value].LoadKey
            : null;
        entries = AssetDatabase.Entries
            .Where(entry => entry.IsGameObject && !string.IsNullOrEmpty(entry.LoadKey))
            .OrderBy(FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.LoadKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SearchItems.Value = entries.Select(entry =>
        {
            var name = FriendlyName(entry);
            return string.Equals(name, entry.LoadKey, StringComparison.OrdinalIgnoreCase)
                ? name : $"{name}  —  {entry.LoadKey}";
        }).ToArray();
        var restored = previousKey == null ? -1 : entries.FindIndex(entry => entry.LoadKey == previousKey);
        SearchIndex.Value = restored >= 0 ? restored : 0;
        Status.Value = entries.Count > 0
            ? $"Camouflage search ready: {entries.Count:N0} prefabs."
            : "The prefab catalog is not ready. Enter a save, wait for scanning, and refresh.";
    }

    [ModAction(ShowInUI = false)]
    public static void UseSearchedDisguise()
    {
        if (!EquippedState.Value)
        {
            Status.Value = "Equip camouflage first.";
            return;
        }
        if (loadPending) return;
        if (SearchIndex.Value < 0 || SearchIndex.Value >= entries.Count)
        {
            Status.Value = "Choose a valid searchable disguise.";
            return;
        }
        Plugin.RunCoroutine(LoadSearchedDisguise(entries[SearchIndex.Value]));
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;
        var character = LocalCharacter();
        if (!character)
        {
            Unequip();
            return;
        }

        UpdateVisualFollow();
        if (Cursor.visible) return;
        if (Input.GetKeyDown(KeyCode.Q)) CopyAimedObject(character);
        if (Input.GetKeyDown(KeyCode.R)) RemoveDisguise();
    }

    private static void CopyAimedObject(PlayerCharacter character)
    {
        var camera = Camera.main;
        if (!camera)
        {
            Status.Value = "No gameplay camera found.";
            return;
        }
        var screenPoint = new Vector3(Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value, 0f);
        var ray = camera.ScreenPointToRay(screenPoint);
        var hits = Physics.RaycastAll(ray, Mathf.Max(5f, AimRange.Value), ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.transform.IsChildOf(character.transform)) continue;
            var target = ResolveTarget(hit.collider);
            if (!target || target == activeVisual || target.transform.IsChildOf(character.transform)) continue;
            if (!TryGetVisualBounds(target, out var bounds, out var rendererCount)) continue;
            if (rendererCount > Mathf.Clamp(Mathf.RoundToInt(MaximumRenderers.Value), 10, 250))
            {
                Status.Value = $"That object has {rendererCount} renderers. Raise the renderer limit or aim at a smaller part.";
                return;
            }
            if (Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) > 100f)
            {
                Status.Value = "That target is a protected map-sized object. Aim at a smaller object or building piece.";
                return;
            }

            var largestDimension = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            var destination = largestDimension > 3.5f
                ? hit.point + hit.normal.normalized * 0.9f + Vector3.up * 0.1f
                : target.transform.position;
            BeginDisguise(character, target, true, bounds, FriendlyObjectName(target), true, destination);
            return;
        }
        Status.Value = "No copyable object was found under the crosshair.";
    }

    private static IEnumerator LoadSearchedDisguise(AssetDatabase.AssetEntry entry)
    {
        loadPending = true;
        var candidates = new[] { entry.LoadKey, entry.Address, entry.Guid }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray();
        foreach (var key in candidates)
        {
            var handle = Addressables.InstantiateAsync(key, new Vector3(0f, -10000f, 0f), Quaternion.identity);
            yield return handle;
            if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result)
            {
                var source = handle.Result;
                if (TryGetVisualBounds(source, out var bounds, out var rendererCount) &&
                    rendererCount <= Mathf.Clamp(Mathf.RoundToInt(MaximumRenderers.Value), 10, 250))
                {
                    var character = LocalCharacter();
                    if (character)
                    {
                        ClearDisguise();
                        addressableSource = source;
                        BeginDisguise(character, source, false, bounds, FriendlyName(entry), false);
                        source.SetActive(false);
                        loadPending = false;
                        yield break;
                    }
                }
                Addressables.ReleaseInstance(source);
            }
            else if (handle.IsValid()) Addressables.Release(handle);
        }
        loadPending = false;
        Status.Value = $"Could not load a visual disguise from {FriendlyName(entry)}.";
    }

    private static void BeginDisguise(PlayerCharacter character, GameObject source, bool teleport,
        Bounds sourceBounds, string label, bool clearFirst = true, Vector3? teleportDestination = null)
    {
        if (clearFirst) ClearDisguise();
        var bodyComponent = character.GetComponentInChildren<PlayerBody>(true);
        var body = bodyComponent ? bodyComponent.GetRigidbody() : null;
        if (!body)
        {
            Status.Value = "The local Wobbly body is not ready.";
            return;
        }

        var visual = BuildVisualCopy(source, sourceBounds);
        if (!visual)
        {
            Status.Value = $"{label} does not contain a supported visible mesh.";
            return;
        }

        disguisedCharacter = character;
        disguisedBody = body;
        activeVisual = visual;
        activeTarget = teleport ? source : null;
        freezeAnchor = body.gameObject.AddComponent<CamouflageFreezeAnchor>();
        SaveAndHidePlayer(character);

        if (teleport)
        {
            SaveAndHideTarget(source);
            character.GetPlayerController()?.GetPlayerControllerInteractor()?.ForceRequestExit();
            var destination = teleportDestination ?? source.transform.position;
            character.SetPlayerPosition(destination);
            activeVisual.transform.position = destination;
            activeVisual.transform.rotation = source.transform.rotation;
        }
        else
        {
            activeVisual.transform.position = body.position;
            activeVisual.transform.rotation = Quaternion.Euler(0f, body.rotation.eulerAngles.y, 0f);
        }

        visualPositionOffset = Quaternion.Inverse(body.rotation) * (activeVisual.transform.position - body.position);
        visualRotationOffset = Quaternion.Inverse(body.rotation) * activeVisual.transform.rotation;
        freezeAnchor.SetFrozen(true);
        lockedInputManager = character.GetPlayerController()?.GetPlayerControllerInputManager();
        lockedInputManager?.DisablePlayerTransformInput(MovementLockHandle);
        Status.Value = teleport
            ? $"Replaced {label} and teleported into its camouflage. You cannot move until R restores it."
            : $"Camouflaged as searched prefab {label}. You cannot move until R restores your Wobbly.";
    }

    private static GameObject BuildVisualCopy(GameObject source, Bounds sourceBounds)
    {
        var root = new GameObject("ExtraMods Camouflage Visual");
        root.transform.position = source.transform.position;
        root.transform.rotation = source.transform.rotation;
        root.transform.localScale = Vector3.one * Mathf.Clamp(DisguiseScale.Value, 0.2f, 3f);
        var copied = 0;
        var limit = Mathf.Clamp(Mathf.RoundToInt(MaximumRenderers.Value), 10, 250);

        foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer || !renderer.enabled || copied >= limit || IsEffectRenderer(renderer)) continue;
            var visualPart = new GameObject(renderer.name + " Camouflage");
            visualPart.transform.SetParent(root.transform, false);
            CopyRelativeTransform(source.transform, renderer.transform, visualPart.transform);

            if (renderer is SkinnedMeshRenderer skinned)
            {
                var mesh = new Mesh { name = renderer.name + " Camouflage Baked Mesh" };
                skinned.BakeMesh(mesh);
                RuntimeMeshes.Add(mesh);
                visualPart.AddComponent<MeshFilter>().sharedMesh = mesh;
                visualPart.AddComponent<MeshRenderer>().sharedMaterials = skinned.sharedMaterials;
                copied++;
            }
            else if (renderer is MeshRenderer meshRenderer)
            {
                var filter = renderer.GetComponent<MeshFilter>();
                if (!filter || !filter.sharedMesh) { UnityEngine.Object.Destroy(visualPart); continue; }
                visualPart.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                visualPart.AddComponent<MeshRenderer>().sharedMaterials = meshRenderer.sharedMaterials;
                copied++;
            }
            else if (renderer is SpriteRenderer spriteRenderer)
            {
                var sprite = visualPart.AddComponent<SpriteRenderer>();
                sprite.sprite = spriteRenderer.sprite;
                sprite.color = spriteRenderer.color;
                sprite.sharedMaterial = spriteRenderer.sharedMaterial;
                sprite.sortingLayerID = spriteRenderer.sortingLayerID;
                sprite.sortingOrder = spriteRenderer.sortingOrder;
                copied++;
            }
            else UnityEngine.Object.Destroy(visualPart);
        }

        if (copied == 0)
        {
            UnityEngine.Object.Destroy(root);
            return null;
        }
        return root;
    }

    private static void CopyRelativeTransform(Transform sourceRoot, Transform source, Transform destination)
    {
        destination.localPosition = sourceRoot.InverseTransformPoint(source.position);
        destination.localRotation = Quaternion.Inverse(sourceRoot.rotation) * source.rotation;
        var rootScale = sourceRoot.lossyScale;
        var scale = source.lossyScale;
        destination.localScale = new Vector3(
            SafeDivide(scale.x, rootScale.x), SafeDivide(scale.y, rootScale.y), SafeDivide(scale.z, rootScale.z));
    }

    private static float SafeDivide(float value, float divisor) => Mathf.Abs(divisor) < 0.0001f ? value : value / divisor;

    private static void SaveAndHidePlayer(PlayerCharacter character)
    {
        PlayerRenderers.Clear();
        networkBodyHidden = false;
        if (lstwoMODS_WobblyLife.PropSpawner.PropSpawnManager.IsServer)
        {
            // ServerSetVisible sends a buffered hide to every other client. Reactivating locally immediately
            // keeps this client's camera and the R restore input alive without exposing the Wobbly remotely.
            VanishComponent.SetVisible(character.gameObject, false);
            VanishComponent.SetVisibleLocal(character.gameObject, true);
            networkBodyHidden = true;
        }
        character.SetCharacterNameVisible(false);
        foreach (var renderer in character.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer || renderer.transform.IsChildOf(activeVisual.transform)) continue;
            PlayerRenderers[renderer] = renderer.enabled;
            renderer.enabled = false;
        }
    }

    private static void SaveAndHideTarget(GameObject target)
    {
        HiddenTargetRenderers.Clear();
        HiddenTargetColliders.Clear();
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer) continue;
            HiddenTargetRenderers[renderer] = renderer.enabled;
            renderer.enabled = false;
        }
        if (!HideTargetColliders.Value) return;
        foreach (var collider in target.GetComponentsInChildren<Collider>(true))
        {
            if (!collider) continue;
            HiddenTargetColliders[collider] = collider.enabled;
            collider.enabled = false;
        }
    }

    private static void UpdateVisualFollow()
    {
        if (!activeVisual || !disguisedBody) return;
        activeVisual.transform.position = disguisedBody.position + disguisedBody.rotation * visualPositionOffset;
        activeVisual.transform.rotation = disguisedBody.rotation * visualRotationOffset;
    }

    private static void ClearDisguise()
    {
        lockedInputManager?.EnablePlayerTransformInput(MovementLockHandle);
        lockedInputManager = null;
        if (freezeAnchor) freezeAnchor.SetFrozen(false);
        if (freezeAnchor) UnityEngine.Object.Destroy(freezeAnchor);
        freezeAnchor = null;
        if (disguisedCharacter)
        {
            if (networkBodyHidden)
            {
                VanishComponent.SetVisible(disguisedCharacter.gameObject, true);
                VanishComponent.SetVisibleLocal(disguisedCharacter.gameObject, true);
            }
            disguisedCharacter.SetCharacterNameVisible(true);
        }
        networkBodyHidden = false;
        foreach (var saved in PlayerRenderers) if (saved.Key) saved.Key.enabled = saved.Value;
        foreach (var saved in HiddenTargetRenderers) if (saved.Key) saved.Key.enabled = saved.Value;
        foreach (var saved in HiddenTargetColliders) if (saved.Key) saved.Key.enabled = saved.Value;
        PlayerRenderers.Clear();
        HiddenTargetRenderers.Clear();
        HiddenTargetColliders.Clear();
        if (activeVisual) UnityEngine.Object.Destroy(activeVisual);
        activeVisual = null;
        activeTarget = null;
        disguisedCharacter = null;
        disguisedBody = null;
        foreach (var mesh in RuntimeMeshes) if (mesh) UnityEngine.Object.Destroy(mesh);
        RuntimeMeshes.Clear();
        if (addressableSource)
        {
            Addressables.ReleaseInstance(addressableSource);
            addressableSource = null;
        }
    }

    private static GameObject ResolveTarget(Collider collider)
    {
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        if (vehicle) return vehicle.gameObject;
        var character = collider.GetComponentInParent<PlayerCharacter>();
        if (character) return character.gameObject;
        var dynamicObject = collider.GetComponentInParent<DynamicObject>();
        if (dynamicObject) return dynamicObject.gameObject;
        var destructible = collider.GetComponentInParent<DestructibleStaticProp>();
        if (destructible) return destructible.gameObject;
        var staticObject = collider.GetComponentInParent<StaticObject>();
        if (staticObject) return staticObject.gameObject;
        var renderer = collider.GetComponentInParent<Renderer>();
        return renderer ? renderer.gameObject : collider.gameObject;
    }

    private static bool TryGetVisualBounds(GameObject target, out Bounds bounds, out int rendererCount)
    {
        bounds = default;
        rendererCount = 0;
        var initialized = false;
        foreach (var renderer in target.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer || IsEffectRenderer(renderer)) continue;
            rendererCount++;
            if (!initialized) { bounds = renderer.bounds; initialized = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        return initialized;
    }

    private static bool IsEffectRenderer(Renderer renderer)
    {
        var type = renderer.GetType().Name;
        return type == "ParticleSystemRenderer" || type == "TrailRenderer" || type == "LineRenderer";
    }

    private static PlayerCharacter LocalCharacter() => GameInstance.InstanceExists
        ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
        : null;

    private static string FriendlyName(AssetDatabase.AssetEntry entry)
    {
        if (entry == null) return "unknown prefab";
        return FriendlyObjectName(!string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : entry.LoadKey);
    }

    private static string FriendlyObjectName(GameObject value) => value ? FriendlyObjectName(value.name) : "object";

    private static string FriendlyObjectName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "object";
        var slash = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (slash >= 0) value = value.Substring(slash + 1);
        if (value.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 7);
        return value.Replace("(Clone)", string.Empty).Replace('_', ' ').Trim();
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var previous = GUI.color;
        GUI.color = activeVisual ? new Color(0.3f, 1f, 0.45f, 0.98f) : new Color(0.2f, 0.85f, 1f, 0.98f);
        GUI.DrawTexture(new Rect(x - 15f, y - 1f, 10f, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 5f, y - 1f, 10f, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1f, y - 15f, 2f, 10f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1f, y + 5f, 2f, 10f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 3f, y - 3f, 6f, 6f), Texture2D.whiteTexture);
        GUI.Box(new Rect(Screen.width * 0.5f - 145f, Screen.height - 86f, 290f, 30f),
            activeVisual ? "CAMOUFLAGE: MOVEMENT LOCKED   RESTORE [R]"
                : "COPY / REPLACE TARGET [Q]");
        GUI.color = previous;
    }
}

/// <summary>Locks a hiding player without changing the constraint flags used by Wobbly movement.</summary>
internal sealed class CamouflageFreezeAnchor : MonoBehaviour
{
    private Rigidbody body;
    private bool frozen;
    private Vector3 position;
    private Quaternion rotation;

    internal void SetFrozen(bool value)
    {
        if (!body) body = GetComponent<Rigidbody>();
        frozen = value && body;
        if (!frozen) return;
        position = body.position;
        rotation = body.rotation;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
    }

    private void FixedUpdate()
    {
        if (!frozen || !body) return;
        body.velocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.position = position;
        body.rotation = rotation;
    }
}

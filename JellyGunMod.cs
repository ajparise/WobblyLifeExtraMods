using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FMODUnity;
using HarmonyLib;
using lstwoMODS.WobblyLife.SharedObjects;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AI;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>Marks a target and summons a jelly-styled native NPC to chase and consume it.</summary>
public sealed class JellyGunMod : BaseMod
{
    private const string CivilianAddress = "NPC Granny";
    private const string EatingEffectAddress =
        "Assets/Content/Game/Prefabs/Particles/Main Menu/Eating Jelly.prefab";
    private const string SplashEffectAddress =
        "Assets/Content/Game/Prefabs/Particles/Jelly Man Basement/Green jelly Splash.prefab";
    private const string NativeShotEvent = "event:/Objects/Objects_PaperCannon";
    private static readonly Vector3 HandOffset = new(0f, 0.18f, 0.12f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Jelly Gun is unequipped.");
    private static readonly List<JellyHunter> ActiveJellyMen = new();
    private static string civilianNetworkId;
    private static GameObject gunRoot;
    private static Transform muzzle;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextFireTime;
    private static float recoilUntil;

    public override string Name => "Jelly Gun";
    public override string Description =>
        "Mark a target and summon a green Jelly Man who chases it down and eats it.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 10f, Max = 300f, Label = "Gun range")]
    public static Ref<float> Range = new(120f);
    [ModSetting(Order = 20, Min = 1f, Max = 15f, Label = "Jelly Man speed")]
    public static Ref<float> JellySpeed = new(5.5f);
    [ModSetting(Order = 30, Min = 0.5f, Max = 4f, Label = "Eating distance")]
    public static Ref<float> EatingDistance = new(1.4f);
    [ModSetting(Order = 40, Min = 0.1f, Max = 2f, Label = "Eating time")]
    public static Ref<float> EatingTime = new(0.75f);
    [ModSetting(Order = 50, Min = 0.15f, Max = 2f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.55f);
    [ModSetting(Order = 60, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();
    [ModSetting(Order = 70, Min = 1f, Max = 15f, Label = "Maximum Jelly Men")]
    public static Ref<int> MaximumJellyMen = new(6);

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += ResolveCivilianNetworkPrefab;
        if (AssetDatabase.IsInitialized) ResolveCivilianNetworkPrefab();
    }

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("JellyGunHelp",
                "Equip and close F2, aim at a Wobbly, vehicle, or movable physics prop, and left-click. A green Jelly Man " +
                "spawns nearby, chases the marked target, and eats it using the game's native Eating Jelly effect. Buildings, " +
                "roads, terrain, oversized mechanisms, and the firing player are protected. Host/offline only."),
            base.BuildPanel(id),
            new HStack("JellyGunActions",
                ActionMenu(new Button("Equip Jelly Gun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip Jelly Gun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Clear Jelly Men", ClearJellyMen), nameof(ClearJellyMen)),
                ActionMenu(new Button("Refresh Jelly Man", ResolveCivilianNetworkPrefab), nameof(ResolveCivilianNetworkPrefab))
            ).WithContentWidth(),
            new TextWrapped("JellyGunStatus", "").WithText(Status));
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
        CamouflagePropHuntMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        LightningGunMod.Unequip();
        WobblyHeadHomingGunMod.Unequip();
        BananaPeelLauncherMod.Unequip();
        TornadoGunMod.Unequip();
        FireworkMinigunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip();
        MosesStaffMod.Unequip();
        PowerSwordMod.Unequip();
        EquippedState.Value = true;
        EnsureGunModel();
        Status.Value = "Jelly Gun equipped. Aim at something movable and left-click.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (gunRoot) Object.Destroy(gunRoot);
        gunRoot = null;
        muzzle = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Jelly Gun unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearJellyMen()
    {
        for (var i = ActiveJellyMen.Count - 1; i >= 0; i--)
            if (ActiveJellyMen[i]) Object.Destroy(ActiveJellyMen[i].gameObject);
        ActiveJellyMen.Clear();
        Status.Value = "All summoned Jelly Men were removed.";
    }

    [ModAction(ShowInUI = false)]
    public static void ResolveCivilianNetworkPrefab()
    {
        var entry = AssetDatabase.Entries.FirstOrDefault(item => item.IsGameObject &&
            (string.Equals(item.LoadKey, CivilianAddress, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Name, CivilianAddress, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(item.NetworkAssetId));
        civilianNetworkId = entry?.NetworkAssetId;
        Status.Value = string.IsNullOrEmpty(civilianNetworkId)
            ? "Jelly Man body is waiting for the native NPC catalog. Enter a save and refresh."
            : "Jelly Man body is ready.";
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;
        if (!HasLocalPlayer())
        {
            Unequip();
            ClearJellyMen();
            return;
        }
        EnsureGunModel();
        UpdateGunModel();
        if (Cursor.visible) return;
        if (RapidFire.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can summon a Jelly Man.";
            return;
        }
        if (string.IsNullOrEmpty(civilianNetworkId)) ResolveCivilianNetworkPrefab();
        if (string.IsNullOrEmpty(civilianNetworkId)) return;
        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var shooter = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the Jelly Gun.";
            return;
        }

        nextFireTime = Time.unscaledTime + Mathf.Max(0.15f, FireInterval.Value);
        recoilUntil = Time.unscaledTime + 0.09f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        if (!TryFindTarget(ray, shooter, out var target, out var hitPoint))
        {
            Status.Value = "The Jelly Gun needs a movable prop, vehicle, or another Wobbly.";
            return;
        }
        SpawnEffect(SplashEffectAddress, hitPoint, 2.5f);
        SpawnJellyMan(target, shooter, camera.transform.position);
        PlayShot(muzzle ? muzzle.position : ray.origin);
    }

    private static bool TryFindTarget(Ray ray, PlayerCharacter shooter, out JellyTarget target, out Vector3 hitPoint)
    {
        target = null;
        hitPoint = default;
        var hits = Physics.RaycastAll(ray, Mathf.Max(10f, Range.Value), ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        foreach (var hit in hits)
        {
            var collider = hit.collider;
            if (!collider || collider.transform.IsChildOf(shooter.transform)) continue;
            var vehicle = collider.GetComponentInParent<PlayerVehicle>();
            var character = vehicle ? null : collider.GetComponentInParent<PlayerCharacter>();
            if (character == shooter) continue;
            var dynamicObject = vehicle || character ? null : collider.GetComponentInParent<DynamicObject>();
            var body = vehicle
                ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true) ?? collider.attachedRigidbody
                : character
                    ? character.GetComponentInChildren<PlayerBody>(true)?.GetRigidbody() ?? collider.attachedRigidbody
                    : dynamicObject ? dynamicObject.GetRigidbody() : collider.attachedRigidbody;
            if (!vehicle && !character && (!body || body.isKinematic))
            {
                // A static surface blocks the shot and is deliberately never deleted.
                return false;
            }
            var bounds = collider.bounds;
            if (!vehicle && !character && Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) > 35f)
                return false;
            var key = vehicle ? vehicle.gameObject : character ? character.gameObject :
                dynamicObject ? dynamicObject.gameObject : body.gameObject;
            target = new JellyTarget(key, collider, vehicle, character, dynamicObject, body);
            hitPoint = hit.point;
            return true;
        }
        return false;
    }

    private static void SpawnJellyMan(JellyTarget target, PlayerCharacter shooter, Vector3 playerPosition)
    {
        CleanupJellySet();
        var limit = Mathf.Clamp(MaximumJellyMen.Value, 1, 15);
        while (ActiveJellyMen.Count >= limit)
        {
            var oldest = ActiveJellyMen[0];
            ActiveJellyMen.RemoveAt(0);
            if (oldest) Object.Destroy(oldest.gameObject);
        }
        var targetPoint = target.CurrentPosition;
        var away = Vector3.ProjectOnPlane(playerPosition - targetPoint, Vector3.up).normalized;
        if (away.sqrMagnitude < 0.1f) away = UnityEngine.Random.onUnitSphere;
        var candidate = targetPoint + Vector3.ProjectOnPlane(away, Vector3.up).normalized * 7f;
        var spawnPoint = StreetNpcPopulationMod.TrySampleWalkableGround(candidate, 10f, out var grounded)
            ? grounded : candidate;
        var keys = new[] { civilianNetworkId, CivilianAddress };
        TrySpawnNetworkJelly(keys, 0, spawnPoint, target, shooter);
        Status.Value = $"Calling a Jelly Man to eat {target.Label}...";
    }

    private static void TrySpawnNetworkJelly(IReadOnlyList<string> keys, int index, Vector3 position,
        JellyTarget target, PlayerCharacter shooter)
    {
        if (index >= keys.Count)
        {
            Status.Value = "The Jelly Man could not enter this world.";
            return;
        }
        NetworkPrefab.SpawnNetworkPrefab(keys[index], behaviour =>
        {
            if (behaviour == null)
            {
                TrySpawnNetworkJelly(keys, index + 1, position, target, shooter);
                return;
            }
            var jelly = behaviour.gameObject;
            if (!target.IsAlive)
            {
                Object.Destroy(jelly);
                Status.Value = "The marked target disappeared before the Jelly Man arrived.";
                return;
            }
            jelly.name = "ExtraMods Jelly Man";
            StreetNpcPopulationMod.PrepareSpawnedNpc(jelly, position);
            ApplyJellyAppearance(jelly);
            var agent = jelly.GetComponent<NavMeshAgent>() ?? jelly.AddComponent<NavMeshAgent>();
            agent.radius = 0.4f;
            agent.height = 1.75f;
            agent.speed = Mathf.Clamp(JellySpeed.Value, 1f, 15f);
            agent.angularSpeed = 420f;
            agent.acceleration = 24f;
            agent.stoppingDistance = Mathf.Clamp(EatingDistance.Value * 0.7f, 0.35f, 2f);
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
            if (!agent.isOnNavMesh) agent.enabled = false;
            var hunter = jelly.AddComponent<JellyHunter>();
            hunter.Initialize(target, shooter, agent, jelly.GetComponent<PlayerNPCController>());
            ActiveJellyMen.Add(hunter);
            SpawnEffect(SplashEffectAddress, position + Vector3.up, 2.5f);
            Status.Value = $"Jelly Man is chasing {target.Label}!";
        }, position: position, rotation: Quaternion.LookRotation(target.CurrentPosition - position, Vector3.up),
           owner: null, bUseChunkSystem: false, bSendTransform: true, bCheckChunk: false);
    }

    private static void ApplyJellyAppearance(GameObject jelly)
    {
        var green = new Color(0.2f, 0.95f, 0.28f, 1f);
        foreach (var renderer in jelly.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer) continue;
            foreach (var material in renderer.materials)
                if (material && material.HasProperty("_Color")) material.color = Color.Lerp(material.color, green, 0.72f);
        }
        var light = jelly.AddComponent<Light>();
        light.color = green;
        light.range = 3.5f;
        light.intensity = 0.7f;
    }

    internal static void BeginEating(JellyHunter hunter, JellyTarget target, PlayerCharacter shooter)
    {
        if (!hunter || !target.IsAlive) return;
        Plugin.RunCoroutine(EatRoutine(hunter, target, shooter));
    }

    private static IEnumerator EatRoutine(JellyHunter hunter, JellyTarget target, PlayerCharacter shooter)
    {
        var point = target.CurrentPosition;
        SpawnEffect(EatingEffectAddress, point, 3f);
        Status.Value = $"Jelly Man is eating {target.Label}...";
        yield return new WaitForSeconds(Mathf.Clamp(EatingTime.Value, 0.1f, 2f));
        if (target.IsAlive)
        {
            EatTarget(target, shooter);
            SpawnEffect(SplashEffectAddress, point, 2.5f);
            Status.Value = $"Jelly Man ate {target.Label}!";
        }
        yield return new WaitForSeconds(0.8f);
        if (hunter) Object.Destroy(hunter.gameObject);
    }

    private static void EatTarget(JellyTarget target, PlayerCharacter shooter)
    {
        if (target.Character)
        {
            if (target.Character == shooter) return;
            if (target.Character.GetComponentInParent<PlayerNPCController>())
                PoliceChaseMod.ReportNpcHarassment("Jelly Man ate an NPC");
            var controller = target.Character.GetPlayerController();
            if (controller) controller.ServerDestoryPlayerCharacter(true, false);
            else Object.Destroy(target.Character.gameObject);
            return;
        }
        if (target.Vehicle)
        {
            target.Vehicle.DestroyGameObject();
            return;
        }
        if (target.DynamicObject)
        {
            target.DynamicObject.DestroyGameObject();
            return;
        }
        if (target.Body) Object.Destroy(target.Body.gameObject);
    }

    private static void SpawnEffect(string address, Vector3 position, float lifetime)
    {
        Plugin.RunCoroutine(SpawnEffectRoutine(address, position, lifetime));
    }

    private static IEnumerator SpawnEffectRoutine(string address, Vector3 position, float lifetime)
    {
        var handle = Addressables.InstantiateAsync(address, position, Quaternion.identity);
        yield return handle;
        if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result)
            handle.Result.AddComponent<JellyEffectCleanup>().Configure(lifetime);
        else
        {
            if (handle.IsValid()) Addressables.Release(handle);
            Plugin.Log?.LogWarning($"Could not load native jelly effect: {address}");
        }
    }

    internal static void Unregister(JellyHunter hunter) => ActiveJellyMen.Remove(hunter);

    private static void CleanupJellySet()
    {
        for (var i = ActiveJellyMen.Count - 1; i >= 0; i--)
            if (!ActiveJellyMen[i]) ActiveJellyMen.RemoveAt(i);
    }

    private static void EnsureGunModel()
    {
        var hand = ResolveRightHand();
        if (!hand) return;
        if (activeHand != hand)
        {
            if (activeHand && handPoseRequested) activeHand.ResetPointing();
            activeHand = hand;
            handPoseRequested = false;
        }
        if (!handPoseRequested)
        {
            activeHand.SetPointing(true, true);
            handPoseRequested = true;
        }
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        if (gunRoot)
        {
            if (gunRoot.transform.parent != anchor) gunRoot.transform.SetParent(anchor, true);
            return;
        }
        gunRoot = new GameObject("ExtraMods Jelly Gun");
        gunRoot.transform.SetParent(anchor, false);
        CreateGunPart("Jelly Gun Body", PrimitiveType.Capsule, new Vector3(0f, 0f, 0.35f),
            new Vector3(90f, 0f, 0f), new Vector3(0.3f, 0.56f, 0.3f), new Color(0.15f, 0.8f, 0.2f));
        CreateGunPart("Jelly Tank", PrimitiveType.Sphere, new Vector3(0f, 0.18f, 0.05f), Vector3.zero,
            new Vector3(0.52f, 0.42f, 0.52f), new Color(0.35f, 1f, 0.4f));
        CreateGunPart("Jelly Grip", PrimitiveType.Cube, new Vector3(0f, -0.31f, 0.02f),
            new Vector3(-15f, 0f, 0f), new Vector3(0.17f, 0.43f, 0.2f), new Color(0.08f, 0.25f, 0.1f));
        CreateGunPart("Jelly Muzzle", PrimitiveType.Cylinder, new Vector3(0f, 0f, 1.05f),
            new Vector3(90f, 0f, 0f), new Vector3(0.24f, 0.24f, 0.24f), new Color(0.55f, 1f, 0.55f));
        muzzle = new GameObject("Jelly Gun Aim Muzzle").transform;
        muzzle.SetParent(gunRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, 0f, 1.35f);
    }

    private static void UpdateGunModel()
    {
        if (!gunRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var position = anchor.position + rotation * HandOffset;
        if (Time.unscaledTime < recoilUntil) position -= ray.direction * 0.1f;
        gunRoot.transform.position = Vector3.Lerp(gunRoot.transform.position, position, 0.68f);
        gunRoot.transform.rotation = Quaternion.Slerp(gunRoot.transform.rotation, rotation, 0.68f);
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!HasLocalPlayer() || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController().GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static bool HasLocalPlayer() => GameInstance.InstanceExists &&
        GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();

    private static void CreateGunPart(string name, PrimitiveType primitive, Vector3 position,
        Vector3 rotation, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.Destroy(collider);
        part.transform.SetParent(gunRoot.transform, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }

    private static void PlayShot(Vector3 position)
    {
        try
        {
            var shot = RuntimeManager.CreateInstance(NativeShotEvent);
            shot.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            shot.setVolume(0.55f);
            shot.start();
            shot.release();
        }
        catch (Exception exception) { Plugin.Log?.LogWarning($"Jelly Gun sound failed: {exception.Message}"); }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = new Color(0.25f, 1f, 0.3f, 1f);
        GUI.DrawTexture(new Rect(x - 19f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 7f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 19f, 4f, 12f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 7f, 4f, 12f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 4f, y - 4f, 8f, 8f), Texture2D.whiteTexture);
        GUI.color = old;
    }

    internal sealed class JellyTarget
    {
        internal readonly GameObject Key;
        internal readonly Collider Collider;
        internal readonly PlayerVehicle Vehicle;
        internal readonly PlayerCharacter Character;
        internal readonly DynamicObject DynamicObject;
        internal readonly Rigidbody Body;
        internal JellyTarget(GameObject key, Collider collider, PlayerVehicle vehicle, PlayerCharacter character,
            DynamicObject dynamicObject, Rigidbody body)
        {
            Key = key; Collider = collider; Vehicle = vehicle; Character = character;
            DynamicObject = dynamicObject; Body = body;
        }
        internal bool IsAlive => Key;
        internal Vector3 CurrentPosition => Collider ? Collider.bounds.center : Body ? Body.worldCenterOfMass :
            Key ? Key.transform.position : Vector3.zero;
        internal string Label => CleanName(Key ? Key.name : "target");
        private static string CleanName(string value) =>
            string.IsNullOrWhiteSpace(value) ? "target" : value.Replace("(Clone)", string.Empty).Trim();
    }
}

internal sealed class JellyHunter : MonoBehaviour
{
    private JellyGunMod.JellyTarget target;
    private PlayerCharacter shooter;
    private NavMeshAgent agent;
    private PlayerNPCController npc;
    private bool walking;
    private bool eating;
    private float nextPathUpdate;

    internal void Initialize(JellyGunMod.JellyTarget markedTarget, PlayerCharacter owner,
        NavMeshAgent navAgent, PlayerNPCController controller)
    {
        target = markedTarget;
        shooter = owner;
        agent = navAgent;
        npc = controller;
    }

    private void Update()
    {
        if (eating) return;
        if (target == null || !target.IsAlive)
        {
            Destroy(gameObject);
            return;
        }
        var destination = target.CurrentPosition;
        var flat = Vector3.ProjectOnPlane(destination - transform.position, Vector3.up);
        if (flat.magnitude <= Mathf.Clamp(JellyGunMod.EatingDistance.Value, 0.5f, 4f))
        {
            eating = true;
            SetWalking(false);
            if (agent && agent.enabled && agent.isOnNavMesh) agent.ResetPath();
            JellyGunMod.BeginEating(this, target, shooter);
            return;
        }

        var usingAgent = agent && agent.enabled && agent.isOnNavMesh;
        if (usingAgent)
        {
            agent.speed = Mathf.Clamp(JellyGunMod.JellySpeed.Value, 1f, 15f);
            if (Time.time >= nextPathUpdate)
            {
                nextPathUpdate = Time.time + 0.18f;
                agent.SetDestination(destination);
            }
            SetWalking(agent.velocity.sqrMagnitude > 0.03f || agent.pathPending);
            return;
        }

        var direction = flat.normalized;
        if (Physics.SphereCast(transform.position + Vector3.up * 0.8f, 0.3f, direction, out var obstacle,
                0.7f, ~0, QueryTriggerInteraction.Ignore) && !obstacle.transform.IsChildOf(transform) &&
            obstacle.collider && obstacle.collider.gameObject != target.Key)
            direction = Quaternion.Euler(0f, 55f, 0f) * direction;
        var next = transform.position + direction * Mathf.Clamp(JellyGunMod.JellySpeed.Value, 1f, 15f) * Time.deltaTime;
        if (StreetNpcPopulationMod.TrySampleWalkableGround(next, 2f, out var ground)) next.y = ground.y;
        transform.position = next;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction, Vector3.up),
            Time.deltaTime * 9f);
        SetWalking(true);
    }

    private void SetWalking(bool value)
    {
        if (walking == value) return;
        walking = value;
        if (npc) npc.SetAnimation(new NPCAnimation { bWalking = value }, false, true);
    }

    private void OnDestroy() => JellyGunMod.Unregister(this);
}

internal sealed class JellyEffectCleanup : MonoBehaviour
{
    private float releaseAt;
    internal void Configure(float duration) => releaseAt = Time.time + duration;
    private void Update()
    {
        if (Time.time >= releaseAt) Addressables.ReleaseInstance(gameObject);
    }
}

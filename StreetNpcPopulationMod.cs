using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HawkNetworking;
using lstwoMODS.WobblyLife.SharedObjects;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using UnityEngine.AI;

namespace WobblyLifeExtraMods;

/// <summary>Maintains a bounded local population of randomized Wobbly pedestrians.</summary>
public sealed class StreetNpcPopulationMod : BaseMod
{
    // NPC World RNG is a short-lived scene helper. NPC Granny is a complete,
    // persistent character with the networking, rigidbody and navigation setup.
    private const string CivilianAddress = "NPC Granny";
    private static readonly Ref<bool> EnabledState = new();
    private static readonly Ref<string> Status = new("Street NPC population is disabled.");
    private static readonly HashSet<StreetNpcWander> Npcs = new();
    private static readonly List<StreetNpcWander> Cleanup = new();
    private static float nextPopulationTick;
    private static int spawnGeneration;
    private static bool spawnPending;
    private static string civilianNetworkId;

    public override string Name => "Street NPC Population";

    public override string Description =>
        "Spawns randomized civilian Wobblies on nearby walkable streets and lets them wander around the player.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    protected override void OnStaticInit()
    {
        AssetDatabase.OnReady += ResolveCivilianNetworkPrefab;
        if (AssetDatabase.IsInitialized) ResolveCivilianNetworkPrefab();
    }

    [ModSetting(Order = 10, Min = 1f, Max = 40f, Label = "NPC count")]
    public static Ref<float> NpcCount = new(12f);

    [ModSetting(Order = 20, Min = 5f, Max = 60f, Label = "Minimum spawn distance")]
    public static Ref<float> MinimumSpawnDistance = new(5f);

    [ModSetting(Order = 30, Min = 10f, Max = 120f, Label = "Maximum spawn distance")]
    public static Ref<float> MaximumSpawnDistance = new(18f);

    [ModSetting(Order = 40, Min = 20f, Max = 250f, Label = "Despawn distance")]
    public static Ref<float> DespawnDistance = new(85f);

    [ModSetting(Order = 50, Min = 0.5f, Max = 8f, Label = "Walking speed")]
    public static Ref<float> WalkingSpeed = new(2.1f);

    [ModSetting(Order = 60, Min = 3f, Max = 45f, Label = "Wander step radius")]
    public static Ref<float> WanderRadius = new(15f);

    [ModSetting(Order = 70, Min = 0.2f, Max = 5f, Label = "Population update interval")]
    public static Ref<float> PopulationInterval = new(0.65f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("StreetNpcHelp",
                "Enable after entering a save. Randomly dressed civilian Wobblies appear on reachable walkable areas " +
                "near you, wander between nearby destinations, and are recycled when too far away. Local/session only."),
            base.BuildPanel(id),
            new HStack("StreetNpcActions",
                ActionMenu(new Button("Enable population", Enable), nameof(Enable)),
                ActionMenu(new Button("Disable and remove NPCs", Disable), nameof(Disable)),
                ActionMenu(new Button("Spawn one now", SpawnOneNow), nameof(SpawnOneNow))
            ).WithContentWidth(),
            new TextWrapped("StreetNpcStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void Enable()
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Street NPCs must be spawned by the offline player or lobby host.";
            return;
        }

        EnabledState.Value = true;
        nextPopulationTick = 0f;
        Status.Value = "Street NPC population enabled. Finding nearby walkable streets...";
    }

    [ModAction(ShowInUI = false)]
    public static void Disable()
    {
        EnabledState.Value = false;
        spawnGeneration++;
        spawnPending = false;
        RemoveAllNpcs();
        Status.Value = "Street NPC population disabled and all spawned NPCs removed.";
    }

    [ModAction(ShowInUI = false)]
    public static void SpawnOneNow()
    {
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can spawn street NPCs.";
            return;
        }

        if (!TryGetPlayerPosition(out var playerPosition))
        {
            Status.Value = "Enter a save before spawning street NPCs.";
            return;
        }

        var camera = Camera.main;
        var forward = camera
            ? Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized
            : Vector3.forward;
        if (forward.sqrMagnitude < 0.1f) forward = Vector3.forward;

        var candidate = playerPosition + forward * 3f;
        var spawnPoint = TrySampleWalkableGround(candidate, 8f, out var groundedPoint)
            ? groundedPoint
            : candidate + Vector3.down * 1.25f;
        EnabledState.Value = true;
        RequestSpawnAt(spawnPoint);
    }

    public override void Update()
    {
        if (!EnabledState.Value) return;

        if (!TryGetPlayerPosition(out var playerPosition))
        {
            if (Npcs.Count > 0) RemoveAllNpcs();
            return;
        }

        CleanupNpcSet();
        RecycleDistantNpcs(playerPosition);
        TrimPopulation(playerPosition);

        if (Time.unscaledTime < nextPopulationTick) return;
        nextPopulationTick = Time.unscaledTime + Mathf.Max(0.2f, PopulationInterval.Value);

        var targetCount = Mathf.Clamp(Mathf.RoundToInt(NpcCount.Value), 1, 40);
        if (Npcs.Count < targetCount && !spawnPending)
            RequestSpawn(playerPosition);

        Status.Value = $"Street population active: {Npcs.Count} / {targetCount} NPCs.";
    }

    private static void RequestSpawn(Vector3 playerPosition)
    {
        if (spawnPending) return;

        if (!TryFindNavMeshPoint(playerPosition, out var spawnPoint))
        {
            Status.Value = "No safe ground point was found near the player.";
            return;
        }

        RequestSpawnAt(spawnPoint);
    }

    private static void RequestSpawnAt(Vector3 spawnPoint)
    {
        if (spawnPending) return;
        if (string.IsNullOrEmpty(civilianNetworkId))
        {
            ResolveCivilianNetworkPrefab();
            if (string.IsNullOrEmpty(civilianNetworkId))
            {
                Status.Value = $"{CivilianAddress} is not registered as a network prefab in this game build.";
                return;
            }
        }

        spawnPending = true;
        SpawnNetworkNpc(new[] { civilianNetworkId, CivilianAddress }, 0, spawnPoint, spawnGeneration);
    }

    private static void SpawnNetworkNpc(
        IReadOnlyList<string> candidates,
        int index,
        Vector3 position,
        int generation)
    {
        if (index >= candidates.Count)
        {
            spawnPending = false;
            Status.Value = "The persistent civilian network prefab could not be spawned.";
            return;
        }

        var rotation = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
        NetworkPrefab.SpawnNetworkPrefab(
            candidates[index],
            behaviour =>
            {
                if (behaviour == null)
                {
                    SpawnNetworkNpc(candidates, index + 1, position, generation);
                    return;
                }

                spawnPending = false;
                var npcObject = behaviour.gameObject;
                if (!EnabledState.Value || generation != spawnGeneration)
                {
                    UnityEngine.Object.Destroy(npcObject);
                    return;
                }

                npcObject.name = "ExtraMods Street NPC";
                PrepareSpawnedNpc(npcObject, position);
                var agent = npcObject.GetComponent<NavMeshAgent>() ?? npcObject.AddComponent<NavMeshAgent>();
                agent.radius = 0.35f;
                agent.height = 1.75f;
                agent.speed = Mathf.Clamp(WalkingSpeed.Value, 0.5f, 8f);
                agent.angularSpeed = 280f;
                agent.acceleration = 10f;
                agent.stoppingDistance = 0.25f;
                agent.autoBraking = true;
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

                var collisionRagdoll = npcObject.GetComponent<NpcCollisionRagdoll>() ??
                                       npcObject.AddComponent<NpcCollisionRagdoll>();
                collisionRagdoll.Configure(agent);
                var wander = npcObject.AddComponent<StreetNpcWander>();
                wander.Configure(agent, npcObject.GetComponent<PlayerNPCController>());
                Npcs.Add(wander);
                Status.Value = $"Spawned networked street NPC at {position.x:0.0}, {position.y:0.0}, {position.z:0.0}. Population: {Npcs.Count}.";
            },
            position: position,
            rotation: rotation,
            owner: null,
            bUseChunkSystem: false,
            bSendTransform: true,
            bCheckChunk: false);
    }

    private static void ResolveCivilianNetworkPrefab()
    {
        var entry = AssetDatabase.Entries.FirstOrDefault(item =>
            item.IsGameObject &&
            (string.Equals(item.LoadKey, CivilianAddress, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(item.Name, CivilianAddress, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrEmpty(item.NetworkAssetId));

        civilianNetworkId = entry?.NetworkAssetId;
        Plugin.Log?.LogInfo(string.IsNullOrEmpty(civilianNetworkId)
            ? $"{CivilianAddress} has no registered network prefab ID."
            : $"Resolved {CivilianAddress} network prefab ID: {civilianNetworkId}.");
    }

    private static bool TryFindNavMeshPoint(Vector3 playerPosition, out Vector3 point)
    {
        var min = Mathf.Max(3f, MinimumSpawnDistance.Value);
        var max = Mathf.Max(min + 1f, MaximumSpawnDistance.Value);

        for (var attempt = 0; attempt < 18; attempt++)
        {
            var angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            var distance = UnityEngine.Random.Range(min, max);
            var candidate = playerPosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * distance;
            if (TrySampleWalkableGround(candidate, 12f, out point))
            {
                return true;
            }
        }

        point = default;
        return false;
    }

    internal static bool TryFindWanderDestination(Vector3 origin, out Vector3 point)
    {
        var radius = Mathf.Clamp(WanderRadius.Value, 3f, 45f);
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var offset = UnityEngine.Random.insideUnitSphere * radius;
            offset.y = 0f;
            if (TrySampleWalkableGround(origin + offset, 6f, out point))
            {
                return true;
            }
        }

        point = default;
        return false;
    }

    internal static bool TrySampleWalkableGround(Vector3 candidate, float navRadius, out Vector3 point)
    {
        if (NavMesh.SamplePosition(candidate, out var navHit, navRadius, NavMesh.AllAreas))
        {
            point = navHit.position;
            return true;
        }

        // Most Island streets are ordinary colliders rather than baked NavMesh in some builds.
        // Falling back to a steepness-filtered ground ray makes population work there too.
        var origin = candidate + Vector3.up * 60f;
        var hits = Physics.RaycastAll(origin, Vector3.down, 140f, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        foreach (var hit in hits)
        {
            if (!hit.collider || hit.normal.y < 0.55f) continue;
            if (hit.collider.GetComponentInParent<PlayerCharacter>() ||
                hit.collider.GetComponentInParent<PlayerNPCController>() ||
                hit.collider.GetComponentInParent<PlayerVehicle>() ||
                hit.collider.GetComponentInParent<DynamicObject>() ||
                hit.collider.GetComponentInParent<Water>())
                continue;

            point = hit.point + Vector3.up * 0.04f;
            return true;
        }

        point = default;
        return false;
    }

    internal static void PrepareSpawnedNpc(GameObject npcObject, Vector3 position)
    {
        npcObject.SetActive(true);
        npcObject.transform.position = position;

        foreach (var behaviour in npcObject.GetComponents<MonoBehaviour>())
        {
            if (!behaviour) continue;
            var typeName = behaviour.GetType().Name;
            if (typeName == "ToolChunkSorter" || typeName == "StaticObject" || typeName == "VanishComponent")
                behaviour.enabled = false;
        }

        var visibleRenderers = 0;
        foreach (var renderer in npcObject.GetComponentsInChildren<Renderer>(true))
        {
            if (!renderer) continue;
            renderer.enabled = true;
            renderer.forceRenderingOff = false;
            visibleRenderers++;
        }

        Plugin.Log?.LogInfo($"Prepared runtime NPC '{npcObject.name}' at {position}; enabled {visibleRenderers} renderers.");
    }

    internal static float CurrentWalkingSpeed => Mathf.Clamp(WalkingSpeed.Value, 0.5f, 8f);

    private static bool TryGetPlayerPosition(out Vector3 position)
    {
        position = default;
        if (!GameInstance.InstanceExists) return false;

        var controller = GameInstance.Instance.GetFirstLocalPlayerController();
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!character) return false;

        // PlayerCharacter's root is kept at the scene origin in this game build; its
        // animated/networked body is offset elsewhere. The gameplay camera follows the
        // real player body and therefore provides the correct world-space anchor.
        var camera = Camera.main;
        position = camera ? camera.transform.position : character.transform.position;
        return true;
    }

    private static void RecycleDistantNpcs(Vector3 playerPosition)
    {
        var maxDistance = Mathf.Max(MaximumSpawnDistance.Value + 5f, DespawnDistance.Value);
        var maxDistanceSqr = maxDistance * maxDistance;
        Cleanup.Clear();
        foreach (var npc in Npcs)
        {
            if (!npc || (npc.transform.position - playerPosition).sqrMagnitude > maxDistanceSqr)
                Cleanup.Add(npc);
        }

        foreach (var npc in Cleanup) ReleaseNpc(npc);
    }

    private static void TrimPopulation(Vector3 playerPosition)
    {
        var targetCount = Mathf.Clamp(Mathf.RoundToInt(NpcCount.Value), 1, 40);
        while (Npcs.Count > targetCount)
        {
            var furthest = Npcs.Where(npc => npc)
                .OrderByDescending(npc => (npc.transform.position - playerPosition).sqrMagnitude)
                .FirstOrDefault();
            if (!furthest) break;
            ReleaseNpc(furthest);
        }
    }

    private static void CleanupNpcSet()
    {
        Cleanup.Clear();
        foreach (var npc in Npcs)
        {
            if (!npc) Cleanup.Add(npc);
        }
        foreach (var npc in Cleanup) Npcs.Remove(npc);
    }

    private static void ReleaseNpc(StreetNpcWander npc)
    {
        Npcs.Remove(npc);
        if (npc) UnityEngine.Object.Destroy(npc.gameObject);
    }

    private static void RemoveAllNpcs()
    {
        Cleanup.Clear();
        Cleanup.AddRange(Npcs);
        foreach (var npc in Cleanup) ReleaseNpc(npc);
        Npcs.Clear();
    }
}

internal sealed class StreetNpcWander : MonoBehaviour
{
    private NavMeshAgent agent;
    private PlayerNPCController npcController;
    private bool walkingAnimation;
    private float nextDestinationTime;
    private float nextAnimationCheck;
    private Vector3 manualDestination;
    private bool manualHasDestination;
    private float createdAt;
    private NpcCollisionRagdoll collisionRagdoll;

    internal void Configure(NavMeshAgent navAgent, PlayerNPCController controller)
    {
        agent = navAgent;
        npcController = controller;
        collisionRagdoll = GetComponent<NpcCollisionRagdoll>();
        createdAt = Time.unscaledTime;
        if (agent && !agent.isOnNavMesh) agent.enabled = false;
        nextDestinationTime = Time.time + UnityEngine.Random.Range(0.2f, 1.2f);
    }

    private void OnDestroy()
    {
        Plugin.Log?.LogWarning($"Street NPC object was destroyed after {Time.unscaledTime - createdAt:0.00}s at {transform.position}.");
    }

    private void Update()
    {
        if (collisionRagdoll && collisionRagdoll.IsRagdolling) return;

        var usingNavMesh = agent && agent.enabled && agent.isOnNavMesh;
        if (usingNavMesh)
        {
            agent.speed = StreetNpcPopulationMod.CurrentWalkingSpeed;
            if (Time.time >= nextDestinationTime &&
                (!agent.hasPath || (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.15f)))
            {
                if (StreetNpcPopulationMod.TryFindWanderDestination(transform.position, out var destination))
                    agent.SetDestination(destination);
                nextDestinationTime = Time.time + UnityEngine.Random.Range(2.5f, 6f);
            }
        }
        else
        {
            UpdateManualWalking();
        }

        if (Time.time < nextAnimationCheck) return;
        nextAnimationCheck = Time.time + 0.2f;
        var shouldWalk = usingNavMesh ? agent.velocity.sqrMagnitude > 0.04f : manualHasDestination;
        if (shouldWalk == walkingAnimation) return;

        walkingAnimation = shouldWalk;
        if (npcController)
        {
            npcController.SetAnimation(new NPCAnimation { bWalking = shouldWalk }, false, true);
        }
    }

    private void UpdateManualWalking()
    {
        if (!manualHasDestination || (transform.position - manualDestination).sqrMagnitude < 0.4f)
        {
            manualHasDestination = StreetNpcPopulationMod.TryFindWanderDestination(
                transform.position, out manualDestination);
            nextDestinationTime = Time.time + UnityEngine.Random.Range(2.5f, 6f);
        }

        if (!manualHasDestination || Time.time < nextDestinationTime - 5.8f) return;

        var offset = manualDestination - transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude < 0.04f)
        {
            manualHasDestination = false;
            return;
        }

        var direction = offset.normalized;
        if (Physics.SphereCast(transform.position + Vector3.up * 0.75f, 0.28f, direction,
                out var obstacle, 0.75f, ~0, QueryTriggerInteraction.Ignore) &&
            !obstacle.transform.IsChildOf(transform))
        {
            manualHasDestination = false;
            return;
        }

        var next = Vector3.MoveTowards(transform.position, manualDestination,
            StreetNpcPopulationMod.CurrentWalkingSpeed * Time.deltaTime);
        if (StreetNpcPopulationMod.TrySampleWalkableGround(next, 1.5f, out var grounded))
            next.y = grounded.y;
        transform.position = next;
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(direction, Vector3.up), Time.deltaTime * 7f);
    }
}

internal sealed class NpcCollisionRagdoll : MonoBehaviour
{
    private const float MinimumImpactSpeed = 2.5f;
    private const float RecoveryDelay = 3.5f;
    private RagdollController ragdollController;
    private PlayerBody playerBody;
    private NavMeshAgent agent;
    private float nextImpactTime;
    private float wakeupTime;

    internal bool IsRagdolling => ragdollController && ragdollController.IsActiveRagdoll();

    internal void Configure(NavMeshAgent navAgent)
    {
        agent = navAgent;
        ragdollController = GetComponentInChildren<RagdollController>(true);
        playerBody = GetComponentInChildren<PlayerBody>(true);
        if (!ragdollController)
            Plugin.Log?.LogWarning($"Spawned NPC '{name}' has no RagdollController.");
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!ragdollController || Time.unscaledTime < nextImpactTime ||
            collision.relativeVelocity.magnitude < MinimumImpactSpeed)
            return;

        nextImpactTime = Time.unscaledTime + 0.5f;
        wakeupTime = Time.unscaledTime + RecoveryDelay;
        ragdollController.Ragdoll();

        if (playerBody)
        {
            var away = collision.contactCount > 0
                ? (transform.position - collision.GetContact(0).point).normalized
                : -collision.relativeVelocity.normalized;
            var speed = Mathf.Clamp(collision.relativeVelocity.magnitude, 3f, 18f);
            playerBody.SetRagdollVelocity(away * speed + Vector3.up * Mathf.Min(4f, speed * 0.3f));
        }
    }

    private void Update()
    {
        if (!ragdollController || !ragdollController.IsActiveRagdoll() ||
            Time.unscaledTime < wakeupTime)
            return;

        ragdollController.Wakeup(true);
        if (agent && !agent.enabled && NavMesh.SamplePosition(transform.position, out var hit, 2f, NavMesh.AllAreas))
        {
            transform.position = hit.position;
            agent.enabled = true;
        }
    }
}

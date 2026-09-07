using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HawkNetworking;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AI;
using UnityEngine.ResourceManagement.AsyncOperations;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>Local wanted system driven by NPC incidents and vehicle speed.</summary>
public sealed class PoliceChaseMod : BaseMod
{
    private const string PoliceNpcNetworkId = "4e8367643eea0db429f820cf8036354d";
    private static readonly Ref<bool> EnabledState = new();
    private static readonly Ref<string> Status = new("Police chase mode is disabled.");
    private static readonly Ref<string> WantedDisplay = new("Wanted: ☆☆☆☆☆");
    private static readonly HashSet<PoliceNpcChase> Officers = new();
    private static readonly List<PoliceNpcChase> Cleanup = new();
    private static float heat;
    private static float lastIncidentTime;
    private static float nextSpeedCheck;
    private static float nextNpcScan;
    private static float nextOfficerSpawn;
    private static bool spawnPending;
    private static int spawnGeneration;
    private static bool arresting;

    public override string Name => "Police Chase Mode";

    public override string Description =>
        "Gain wanted heat by hitting or disturbing NPCs and speeding in vehicles; police then pursue the local player.";

    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 20f, Max = 250f, Label = "Vehicle speed limit (km/h)")]
    public static Ref<float> SpeedLimit = new(75f);

    [ModSetting(Order = 20, Min = 1f, Max = 8f, Label = "Police per wanted level")]
    public static Ref<float> OfficersPerLevel = new(2f);

    [ModSetting(Order = 30, Min = 2f, Max = 15f, Label = "Police running speed")]
    public static Ref<float> PoliceSpeed = new(5.5f);

    [ModSetting(Order = 40, Min = 5f, Max = 60f, Label = "Escape delay")]
    public static Ref<float> EscapeDelay = new(18f);

    [ModSetting(Order = 50, Min = 0.5f, Max = 10f, Label = "Wanted decay per second")]
    public static Ref<float> HeatDecay = new(2.5f);

    [ModSetting(Order = 60, Min = 15f, Max = 100f, Label = "Police spawn distance")]
    public static Ref<float> PoliceSpawnDistance = new(32f);

    [ModSetting(Order = 70, Min = 1f, Max = 5f, Label = "Arrest distance")]
    public static Ref<float> ArrestDistance = new(1.7f);

    [ModSetting(Order = 80, Label = "Respawn player when caught")]
    public static Ref<bool> RespawnWhenCaught = new(true);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("PoliceChaseHelp",
                "Hit or shove Wobblies/NPCs, use weapons on them, or exceed the speed limit in a driven vehicle to " +
                "gain wanted heat. Police spawn on reachable paths and pursue you until you escape or are caught."),
            base.BuildPanel(id),
            new HStack("PoliceChaseActions",
                ActionMenu(new Button("Enable police chase", Enable), nameof(Enable)),
                ActionMenu(new Button("Disable and remove police", Disable), nameof(Disable)),
                ActionMenu(new Button("Clear wanted level", ClearWanted), nameof(ClearWanted)),
                ActionMenu(new Button("Test wanted level", TestWanted), nameof(TestWanted))
            ).WithContentWidth(),
            new TextWrapped("PoliceWantedDisplay", "").WithText(WantedDisplay),
            new TextWrapped("PoliceChaseStatus", "").WithText(Status)
        );
    }

    [ModAction(ShowInUI = false)]
    public static void Enable()
    {
        EnabledState.Value = true;
        heat = 0f;
        lastIncidentTime = Time.unscaledTime;
        arresting = false;
        UpdateWantedDisplay();
        Status.Value = "Police chase enabled. Obey the speed limit and leave NPCs alone.";
    }

    [ModAction(ShowInUI = false)]
    public static void Disable()
    {
        EnabledState.Value = false;
        heat = 0f;
        arresting = false;
        spawnGeneration++;
        spawnPending = false;
        RemoveAllOfficers();
        RemoveIncidentSensors();
        UpdateWantedDisplay();
        Status.Value = "Police chase disabled and all extra police removed.";
    }

    [ModAction(ShowInUI = false)]
    public static void ClearWanted()
    {
        heat = 0f;
        arresting = false;
        RemoveAllOfficers();
        UpdateWantedDisplay();
        Status.Value = "Wanted level cleared.";
    }

    [ModAction(ShowInUI = false)]
    public static void TestWanted()
    {
        AddHeat(25f, "test incident");
    }

    internal static void ReportNpcHarassment(string reason)
    {
        if (!EnabledState.Value) return;
        AddHeat(22f, reason);
    }

    internal static void ReportNpcCollision(float relativeSpeed)
    {
        if (!EnabledState.Value || relativeSpeed < 3.5f) return;
        AddHeat(Mathf.Clamp(relativeSpeed * 2.2f, 12f, 35f), "hit a Wobbly");
    }

    public override void Update()
    {
        if (!EnabledState.Value) return;

        var player = LocalPlayer();
        var character = player ? player.GetPlayerCharacter() : null;
        if (!character)
        {
            if (Officers.Count > 0) RemoveAllOfficers();
            return;
        }

        CleanupOfficerSet();
        CheckSpeeding(player);
        ScanNpcSensors();

        if (heat > 0f && Time.unscaledTime - lastIncidentTime >= Mathf.Max(1f, EscapeDelay.Value))
        {
            heat = Mathf.Max(0f, heat - Mathf.Max(0.1f, HeatDecay.Value) * Time.unscaledDeltaTime);
            if (heat <= 0f)
            {
                RemoveAllOfficers();
                Status.Value = "You escaped. Wanted level cleared.";
            }
            UpdateWantedDisplay();
        }

        var camera = Camera.main;
        MaintainPolice(camera ? camera.transform.position : character.transform.position);
    }

    private static void CheckSpeeding(PlayerController player)
    {
        if (Time.unscaledTime < nextSpeedCheck) return;
        nextSpeedCheck = Time.unscaledTime + 0.5f;

        var entered = player.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject();
        var vehicle = entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
        if (!vehicle || vehicle.GetDriverPlayerController() != player) return;

        var movement = vehicle.GetVehicleMovementBase();
        var body = movement ? movement.GetRigidbody() : vehicle.GetComponent<Rigidbody>();
        if (!body) return;

        var speedKmh = body.velocity.magnitude * 3.6f;
        if (speedKmh > Mathf.Max(5f, SpeedLimit.Value))
            AddHeat(Mathf.Clamp((speedKmh - SpeedLimit.Value) * 0.08f + 4f, 4f, 15f), $"speeding at {speedKmh:0} km/h");
    }

    private static void ScanNpcSensors()
    {
        if (Time.unscaledTime < nextNpcScan) return;
        nextNpcScan = Time.unscaledTime + 2f;

        foreach (var npc in Object.FindObjectsOfType<PlayerNPCController>())
        {
            if (!npc || npc.GetComponent<PoliceNpcChase>() || npc.GetComponent<PoliceIncidentSensor>()) continue;
            npc.gameObject.AddComponent<PoliceIncidentSensor>();
        }
    }

    private static void MaintainPolice(Vector3 playerPosition)
    {
        var level = WantedLevel;
        var target = level * Mathf.Clamp(Mathf.RoundToInt(OfficersPerLevel.Value), 1, 8);
        target = Mathf.Clamp(target, 0, 20);

        while (Officers.Count > target)
        {
            var officer = Officers.Where(item => item)
                .OrderByDescending(item => (item.transform.position - playerPosition).sqrMagnitude)
                .FirstOrDefault();
            if (!officer) break;
            ReleaseOfficer(officer);
        }

        if (target > Officers.Count && !spawnPending && Time.unscaledTime >= nextOfficerSpawn)
        {
            nextOfficerSpawn = Time.unscaledTime + 0.8f;
            RequestOfficerSpawn(playerPosition);
        }
    }

    private static void RequestOfficerSpawn(Vector3 playerPosition)
    {
        if (!TryFindSpawnPoint(playerPosition, out var position)) return;
        spawnPending = true;
        SpawnOfficer(position, spawnGeneration);
    }

    private static void SpawnOfficer(Vector3 position, int generation)
    {
        NetworkPrefab.SpawnNetworkPrefab(PoliceNpcNetworkId, behaviour =>
        {
        spawnPending = false;
        if (behaviour == null) { Status.Value = "Police NPC failed to spawn."; return; }
        var policeObject = behaviour.gameObject;
        if (!EnabledState.Value || generation != spawnGeneration || WantedLevel == 0)
        { Object.Destroy(policeObject); return; }
        policeObject.name = "ExtraMods Police Officer";
        StreetNpcPopulationMod.PrepareSpawnedNpc(policeObject, position);
        var agent = policeObject.GetComponent<NavMeshAgent>() ?? policeObject.AddComponent<NavMeshAgent>();
        agent.radius = 0.36f;
        agent.height = 1.75f;
        agent.speed = CurrentPoliceSpeed;
        agent.angularSpeed = 360f;
        agent.acceleration = 18f;
                agent.stoppingDistance = Mathf.Max(0.7f, ArrestDistance.Value * 0.7f);
                agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;

                var collisionRagdoll = policeObject.GetComponent<NpcCollisionRagdoll>() ??
                                       policeObject.AddComponent<NpcCollisionRagdoll>();
                collisionRagdoll.Configure(agent);
                var chase = policeObject.AddComponent<PoliceNpcChase>();
        chase.Configure(agent, policeObject.GetComponent<PlayerNPCController>());
        Officers.Add(chase);
        Status.Value = $"Police dispatched: {Officers.Count} officers pursuing.";
        }, position: position,
           rotation: Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f),
           owner: null, bUseChunkSystem: false, bSendTransform: true, bCheckChunk: false);
    }

    private static bool TryFindSpawnPoint(Vector3 playerPosition, out Vector3 position)
    {
        var distance = Mathf.Clamp(PoliceSpawnDistance.Value, 15f, 100f);
        for (var attempt = 0; attempt < 18; attempt++)
        {
            var angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            var radius = UnityEngine.Random.Range(distance * 0.75f, distance * 1.2f);
            var candidate = playerPosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
            if (StreetNpcPopulationMod.TrySampleWalkableGround(candidate, 14f, out position))
            {
                return true;
            }
        }

        position = default;
        return false;
    }

    private static void AddHeat(float amount, string reason)
    {
        if (!EnabledState.Value) return;
        heat = Mathf.Clamp(heat + amount, 0f, 100f);
        lastIncidentTime = Time.unscaledTime;
        UpdateWantedDisplay();
        Status.Value = $"Police alerted: {reason}. Wanted level {WantedLevel}.";
    }

    internal static float CurrentPoliceSpeed =>
        Mathf.Clamp(PoliceSpeed.Value + Mathf.Max(0, WantedLevel - 1) * 0.65f, 2f, 18f);

    internal static Transform PlayerTarget
    {
        get
        {
            var camera = Camera.main;
            if (camera) return camera.transform;
            var player = LocalPlayer();
            var character = player ? player.GetPlayerCharacter() : null;
            return character ? character.transform : null;
        }
    }

    internal static void OfficerReachedPlayer()
    {
        if (!EnabledState.Value || arresting || WantedLevel == 0) return;
        arresting = true;
        var player = LocalPlayer();
        Status.Value = "Caught by police.";

        if (RespawnWhenCaught.Value && player)
            Plugin.RunCoroutine(ArrestAndRespawn(player));
        else
            ClearWanted();
    }

    private static IEnumerator ArrestAndRespawn(PlayerController player)
    {
        player.ServerDestoryPlayerCharacter(true, false);
        yield return new WaitForSeconds(0.5f);
        if (player) player.ClientRequestRespawn(-1);
        yield return new WaitForSeconds(0.5f);
        ClearWanted();
    }

    private static int WantedLevel => heat <= 0.01f ? 0 : Mathf.Clamp(Mathf.CeilToInt(heat / 20f), 1, 5);

    private static void UpdateWantedDisplay()
    {
        var level = WantedLevel;
        WantedDisplay.Value = $"Wanted: {new string('★', level)}{new string('☆', 5 - level)}  Heat {heat:0}/100";
    }

    private static PlayerController LocalPlayer() =>
        GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;

    private static void CleanupOfficerSet()
    {
        Cleanup.Clear();
        foreach (var officer in Officers)
            if (!officer) Cleanup.Add(officer);
        foreach (var officer in Cleanup) Officers.Remove(officer);
    }

    private static void ReleaseOfficer(PoliceNpcChase officer)
    {
        Officers.Remove(officer);
        if (officer) Object.Destroy(officer.gameObject);
    }

    private static void RemoveAllOfficers()
    {
        Cleanup.Clear();
        Cleanup.AddRange(Officers);
        foreach (var officer in Cleanup) ReleaseOfficer(officer);
        Officers.Clear();
    }

    private static void RemoveIncidentSensors()
    {
        foreach (var sensor in Object.FindObjectsOfType<PoliceIncidentSensor>())
            if (sensor) Object.Destroy(sensor);
    }
}

internal sealed class PoliceIncidentSensor : MonoBehaviour
{
    private float nextReport;

    private void OnCollisionEnter(Collision collision)
    {
        if (Time.unscaledTime < nextReport) return;

        var local = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        if (!local) return;
        var character = collision.collider.GetComponentInParent<PlayerCharacter>();
        var vehicle = collision.collider.GetComponentInParent<PlayerVehicle>();
        var causedByPlayer = character && character == local.GetPlayerCharacter();
        causedByPlayer |= vehicle && vehicle.GetDriverPlayerController() == local;
        causedByPlayer |= collision.gameObject.name.StartsWith("ExtraMods", StringComparison.Ordinal);
        if (!causedByPlayer) return;

        nextReport = Time.unscaledTime + 2f;
        PoliceChaseMod.ReportNpcCollision(collision.relativeVelocity.magnitude);
    }
}

internal sealed class PoliceNpcChase : MonoBehaviour
{
    private NavMeshAgent agent;
    private PlayerNPCController controller;
    private bool walking;
    private float nextPathUpdate;
    private float nextAnimationUpdate;
    private GameObject uniformRoot;
    private NpcCollisionRagdoll collisionRagdoll;

    internal void Configure(NavMeshAgent navAgent, PlayerNPCController npcController)
    {
        agent = navAgent;
        controller = npcController;
        collisionRagdoll = GetComponent<NpcCollisionRagdoll>();
        if (agent && !agent.isOnNavMesh) agent.enabled = false;
        CreatePoliceVisuals();
    }

    private void Update()
    {
        if (collisionRagdoll && collisionRagdoll.IsRagdolling) return;

        var target = PoliceChaseMod.PlayerTarget;
        if (!target) return;

        var usingNavMesh = agent && agent.enabled && agent.isOnNavMesh;
        if (usingNavMesh)
        {
            agent.speed = PoliceChaseMod.CurrentPoliceSpeed;
            if (Time.time >= nextPathUpdate)
            {
                nextPathUpdate = Time.time + 0.2f;
                if (NavMesh.SamplePosition(target.position, out var hit, 5f, NavMesh.AllAreas))
                    agent.SetDestination(hit.position);
            }
        }
        else
        {
            UpdateManualChase(target);
        }

        if ((transform.position - target.position).sqrMagnitude <=
            PoliceChaseMod.ArrestDistance.Value * PoliceChaseMod.ArrestDistance.Value)
            PoliceChaseMod.OfficerReachedPlayer();

        if (Time.time < nextAnimationUpdate) return;
        nextAnimationUpdate = Time.time + 0.2f;
        var shouldWalk = usingNavMesh ? agent.velocity.sqrMagnitude > 0.04f : true;
        if (shouldWalk == walking) return;
        walking = shouldWalk;
        if (controller) controller.SetAnimation(new NPCAnimation { bWalking = walking }, false, true);
    }

    private void UpdateManualChase(Transform target)
    {
        var offset = target.position - transform.position;
        offset.y = 0f;
        if (offset.sqrMagnitude < 0.04f) return;

        var direction = offset.normalized;
        if (Physics.SphereCast(transform.position + Vector3.up * 0.75f, 0.28f, direction,
                out var obstacle, 0.7f, ~0, QueryTriggerInteraction.Ignore) &&
            !obstacle.transform.IsChildOf(transform) && !obstacle.transform.IsChildOf(target))
        {
            direction = Quaternion.AngleAxis(UnityEngine.Random.value > 0.5f ? 65f : -65f, Vector3.up) * direction;
        }

        var next = transform.position + direction * PoliceChaseMod.CurrentPoliceSpeed * Time.deltaTime;
        if (StreetNpcPopulationMod.TrySampleWalkableGround(next, 1.5f, out var grounded))
            next.y = grounded.y;
        transform.position = next;
        transform.rotation = Quaternion.Slerp(transform.rotation,
            Quaternion.LookRotation(direction, Vector3.up), Time.deltaTime * 9f);
    }

    private void CreatePoliceVisuals()
    {
        uniformRoot = new GameObject("Police Uniform Marker");
        uniformRoot.transform.SetParent(transform, false);
        CreatePart("Blue Police Vest", PrimitiveType.Cube, new Vector3(0f, 1.02f, 0f),
            new Vector3(0.62f, 0.5f, 0.34f), new Color(0.04f, 0.15f, 0.48f));
        CreatePart("Police Cap", PrimitiveType.Cylinder, new Vector3(0f, 1.72f, 0f),
            new Vector3(0.38f, 0.08f, 0.38f), new Color(0.03f, 0.08f, 0.24f));
        CreatePart("Red Beacon", PrimitiveType.Cube, new Vector3(-0.13f, 1.82f, 0f),
            new Vector3(0.16f, 0.08f, 0.16f), new Color(1f, 0.05f, 0.03f));
        CreatePart("Blue Beacon", PrimitiveType.Cube, new Vector3(0.13f, 1.82f, 0f),
            new Vector3(0.16f, 0.08f, 0.16f), new Color(0.05f, 0.25f, 1f));
    }

    private void CreatePart(string partName, PrimitiveType primitive, Vector3 position, Vector3 scale, Color color)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = partName;
        var collider = part.GetComponent<Collider>();
        if (collider) collider.enabled = false;
        Object.Destroy(collider);
        part.transform.SetParent(uniformRoot.transform, false);
        part.transform.localPosition = position;
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer) renderer.material.color = color;
    }
}

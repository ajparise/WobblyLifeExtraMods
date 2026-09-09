using System;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

/// <summary>Places a linked pair of physical portals that preserve redirected momentum.</summary>
public sealed class PortalGunMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Portal Gun is unequipped.");
    private static PortalSurface bluePortal;
    private static PortalSurface orangePortal;

    public override string Name => "Portal Gun";
    public override string Description =>
        "Place linked blue and orange portals that teleport Wobblies, vehicles, and physics props while preserving momentum.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 10f, Max = 1000f, Label = "Placement range")]
    public static Ref<float> Range = new(350f);

    [ModSetting(Order = 20, Min = 2f, Max = 10f, Label = "Portal width")]
    public static Ref<float> PortalWidth = new(5f);

    [ModSetting(Order = 30, Min = 3f, Max = 14f, Label = "Portal height")]
    public static Ref<float> PortalHeight = new(7f);

    [ModSetting(Order = 40, Label = "Preserve momentum")]
    public static Ref<bool> PreserveMomentum = new(true);

    [ModSetting(Order = 50, Min = 0f, Max = 25f, Label = "Exit boost")]
    public static Ref<float> ExitBoost = new(2.5f);

    [ModSetting(Order = 60, Min = 0.2f, Max = 3f, Label = "Retrigger delay")]
    public static Ref<float> RetriggerDelay = new(0.65f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("PortalGunHelp",
                "Equip and close F2. Left-click places the blue portal; right-click places the orange portal. " +
                "Walk, drive, or throw physics props through either side to emerge from the other. Momentum is redirected " +
                "to match the exit surface. Press R to clear both portals. Portal physics require offline play or the lobby host."),
            base.BuildPanel(id),
            new HStack("PortalGunActions",
                ActionMenu(new Button("Equip Portal Gun", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip Portal Gun", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Place blue", PlaceBlue), nameof(PlaceBlue)),
                ActionMenu(new Button("Place orange", PlaceOrange), nameof(PlaceOrange)),
                ActionMenu(new Button("Clear portals", ClearPortals), nameof(ClearPortals))
            ).WithContentWidth(),
            new TextWrapped("PortalGunStatus", "").WithText(Status));
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
        EquippedState.Value = true;
        Status.Value = "Portal Gun equipped. Left-click blue, right-click orange, R clears.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Status.Value = bluePortal || orangePortal
            ? "Portal Gun unequipped; placed portals remain active."
            : "Portal Gun unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void PlaceBlue() => PlacePortal(true);

    [ModAction(ShowInUI = false)]
    public static void PlaceOrange() => PlacePortal(false);

    [ModAction(ShowInUI = false)]
    public static void ClearPortals()
    {
        if (bluePortal) UnityEngine.Object.Destroy(bluePortal.gameObject);
        if (orangePortal) UnityEngine.Object.Destroy(orangePortal.gameObject);
        bluePortal = null;
        orangePortal = null;
        Status.Value = EquippedState.Value ? "Both portals cleared." : "Portal Gun is unequipped.";
    }

    public override void Update()
    {
        if (!GameInstance.InstanceExists || !GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter())
        {
            if (bluePortal || orangePortal) ClearPortals();
            if (EquippedState.Value) Unequip();
            return;
        }

        RefreshLinks();
        if (!EquippedState.Value || Cursor.visible) return;
        if (Input.GetMouseButtonDown(0)) PlaceBlue();
        if (Input.GetMouseButtonDown(1)) PlaceOrange();
        if (Input.GetKeyDown(KeyCode.R)) ClearPortals();
    }

    private static void PlacePortal(bool blue)
    {
        if (!EquippedState.Value)
        {
            Status.Value = "Equip the Portal Gun first.";
            return;
        }
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can place physics portals.";
            return;
        }

        var camera = Camera.main;
        var controller = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var character = controller ? controller.GetPlayerCharacter() : null;
        if (!camera || !character)
        {
            Status.Value = "Enter a save before placing portals.";
            return;
        }

        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var hits = Physics.RaycastAll(ray, Mathf.Max(10f, Range.Value), ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (left, right) => left.distance.CompareTo(right.distance));
        var hit = hits.FirstOrDefault(item => item.collider &&
            !item.transform.IsChildOf(character.transform) && !item.collider.GetComponentInParent<PortalSurface>());
        if (!hit.collider)
        {
            Status.Value = $"The {(blue ? "blue" : "orange")} portal needs a solid surface.";
            return;
        }

        var normal = hit.normal.normalized;
        var vertical = Vector3.ProjectOnPlane(Vector3.up, normal).normalized;
        if (vertical.sqrMagnitude < 0.01f)
            vertical = Vector3.ProjectOnPlane(camera.transform.forward, normal).normalized;
        if (vertical.sqrMagnitude < 0.01f)
            vertical = Vector3.Cross(normal, Vector3.right).normalized;

        var root = new GameObject(blue ? "ExtraMods Blue Portal" : "ExtraMods Orange Portal");
        root.transform.position = hit.point + normal * 0.08f;
        root.transform.rotation = Quaternion.LookRotation(vertical, normal);
        var surface = root.AddComponent<PortalSurface>();
        surface.Initialize(blue, Mathf.Clamp(PortalWidth.Value, 2f, 10f),
            Mathf.Clamp(PortalHeight.Value, 3f, 14f));

        if (blue)
        {
            if (bluePortal) UnityEngine.Object.Destroy(bluePortal.gameObject);
            bluePortal = surface;
        }
        else
        {
            if (orangePortal) UnityEngine.Object.Destroy(orangePortal.gameObject);
            orangePortal = surface;
        }

        RefreshLinks();
        Status.Value = $"{(blue ? "Blue" : "Orange")} portal placed" +
            (bluePortal && orangePortal ? "; the pair is active." : "; place the other portal to link it.");
    }

    private static void RefreshLinks()
    {
        if (bluePortal) bluePortal.LinkTo(orangePortal);
        if (orangePortal) orangePortal.LinkTo(bluePortal);
    }

    internal static void ReportTransit(bool blueEntrance, string targetName)
    {
        Status.Value = $"{CleanName(targetName)} traveled through the {(blueEntrance ? "blue" : "orange")} portal.";
    }

    private static string CleanName(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Object" : value.Replace("(Clone)", string.Empty).Trim();

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = new Color(0.05f, 0.55f, 1f, 1f);
        GUI.DrawTexture(new Rect(x - 18f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 18f, 4f, 12f), Texture2D.whiteTexture);
        GUI.color = new Color(1f, 0.38f, 0.02f, 1f);
        GUI.DrawTexture(new Rect(x + 6f, y - 2f, 12f, 4f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y + 6f, 4f, 12f), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class PortalSurface : MonoBehaviour
{
    private PortalSurface linked;
    private bool blue;
    private float width;
    private float height;
    private Transform face;

    internal Vector3 Normal => transform.up;

    internal void Initialize(bool isBlue, float portalWidth, float portalHeight)
    {
        blue = isBlue;
        width = portalWidth;
        height = portalHeight;

        var trigger = gameObject.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = new Vector3(width * 0.88f, 0.8f, height * 0.88f);
        trigger.center = new Vector3(0f, 0.25f, 0f);

        var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        disc.name = blue ? "Blue Portal Surface" : "Orange Portal Surface";
        UnityEngine.Object.Destroy(disc.GetComponent<Collider>());
        disc.transform.SetParent(transform, false);
        disc.transform.localPosition = Vector3.zero;
        disc.transform.localRotation = Quaternion.identity;
        disc.transform.localScale = new Vector3(width, 0.025f, height);
        SetColor(disc.GetComponent<Renderer>(), blue ? new Color(0.02f, 0.25f, 1f, 0.82f) :
            new Color(1f, 0.18f, 0.01f, 0.82f));
        face = disc.transform;

        for (var i = 0; i < 20; i++)
        {
            var angle = i / 20f * Mathf.PI * 2f;
            var segment = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            segment.name = "Portal Rim";
            UnityEngine.Object.Destroy(segment.GetComponent<Collider>());
            segment.transform.SetParent(transform, false);
            segment.transform.localPosition = new Vector3(Mathf.Cos(angle) * width * 0.5f, -0.025f,
                Mathf.Sin(angle) * height * 0.5f);
            segment.transform.localScale = Vector3.one * Mathf.Max(0.13f, width * 0.065f);
            SetColor(segment.GetComponent<Renderer>(), blue ? new Color(0.05f, 0.65f, 1f, 1f) :
                new Color(1f, 0.42f, 0.02f, 1f));
        }

        var light = gameObject.AddComponent<Light>();
        light.color = blue ? new Color(0.05f, 0.35f, 1f) : new Color(1f, 0.22f, 0.01f);
        light.range = Mathf.Max(width, height) * 1.3f;
        light.intensity = 2f;
    }

    internal void LinkTo(PortalSurface other) => linked = other;

    private void Update()
    {
        if (!face) return;
        var pulse = 0.96f + Mathf.Sin(Time.time * 4f) * 0.035f;
        face.localScale = new Vector3(width * pulse, 0.025f, height * pulse);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!linked || !other) return;
        var vehicle = other.GetComponentInParent<PlayerVehicle>();
        var character = vehicle ? null : other.GetComponentInParent<PlayerCharacter>();
        var body = vehicle
            ? vehicle.GetComponent<Rigidbody>() ?? vehicle.GetComponentInChildren<Rigidbody>(true) ?? other.attachedRigidbody
            : other.attachedRigidbody;
        var lockRoot = vehicle ? vehicle.gameObject : character ? character.gameObject : body ? body.gameObject : null;
        if (!lockRoot) return;

        var transitLock = lockRoot.GetComponent<PortalTransitLock>();
        if (transitLock && transitLock.IsLocked) return;
        if (!transitLock) transitLock = lockRoot.AddComponent<PortalTransitLock>();
        transitLock.LockFor(Mathf.Max(0.2f, PortalGunMod.RetriggerDelay.Value));

        if (vehicle && body)
            TeleportBody(body, linked, other.bounds);
        else if (character)
            TeleportCharacter(character, linked, other.bounds);
        else if (body && !body.isKinematic)
            TeleportBody(body, linked, other.bounds);
        else
            return;

        PortalGunMod.ReportTransit(blue, lockRoot.name);
    }

    private void TeleportBody(Rigidbody body, PortalSurface exit, Bounds bounds)
    {
        var rotationDelta = exit.transform.rotation * Quaternion.Euler(180f, 0f, 0f) *
            Quaternion.Inverse(transform.rotation);
        var velocity = body.velocity;
        var angularVelocity = body.angularVelocity;
        var clearance = Mathf.Clamp(bounds.extents.magnitude, 1.2f, 6f) + 0.4f;

        body.position = exit.transform.position + exit.Normal * clearance;
        body.rotation = rotationDelta * body.rotation;
        if (PortalGunMod.PreserveMomentum.Value)
        {
            body.velocity = EnsureOutward(rotationDelta * velocity, exit.Normal) +
                exit.Normal * PortalGunMod.ExitBoost.Value;
            body.angularVelocity = rotationDelta * angularVelocity;
        }
        else
        {
            body.velocity = exit.Normal * PortalGunMod.ExitBoost.Value;
            body.angularVelocity = Vector3.zero;
        }
        body.WakeUp();
    }

    private void TeleportCharacter(PlayerCharacter character, PortalSurface exit, Bounds bounds)
    {
        var playerBody = character.GetComponentInChildren<PlayerBody>(true);
        var body = playerBody ? playerBody.GetRigidbody() : otherBody(character);
        var rotationDelta = exit.transform.rotation * Quaternion.Euler(180f, 0f, 0f) *
            Quaternion.Inverse(transform.rotation);
        var velocity = body ? body.velocity : Vector3.zero;
        var clearance = Mathf.Clamp(bounds.extents.magnitude, 1.2f, 3f) + 0.5f;
        var destination = exit.transform.position + exit.Normal * clearance;

        character.GetPlayerController()?.GetPlayerControllerInteractor()?.ForceRequestExit();
        character.SetPlayerPosition(destination);
        if (body)
        {
            body.velocity = PortalGunMod.PreserveMomentum.Value
                ? EnsureOutward(rotationDelta * velocity, exit.Normal) + exit.Normal * PortalGunMod.ExitBoost.Value
                : exit.Normal * PortalGunMod.ExitBoost.Value;
            body.WakeUp();
        }
    }

    private static Rigidbody otherBody(PlayerCharacter character) =>
        character.GetComponentsInChildren<Rigidbody>(true).FirstOrDefault(item => item && !item.isKinematic);

    private static Vector3 EnsureOutward(Vector3 velocity, Vector3 normal)
    {
        var normalSpeed = Vector3.Dot(velocity, normal);
        if (normalSpeed < 0f) velocity -= normal * normalSpeed * 2f;
        return velocity;
    }

    private static void SetColor(Renderer renderer, Color color)
    {
        if (!renderer) return;
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader) renderer.material = new Material(shader) { color = color };
        else renderer.material.color = color;
    }
}

internal sealed class PortalTransitLock : MonoBehaviour
{
    private float lockedUntil;
    internal bool IsLocked => Time.time < lockedUntil;
    internal void LockFor(float duration) => lockedUntil = Mathf.Max(lockedUntil, Time.time + duration);

    private void Update()
    {
        if (!IsLocked) Destroy(this);
    }
}

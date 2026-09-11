using System;
using System.Collections.Generic;
using System.Linq;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class GrapplingHookMod : BaseMod
{
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Grappling hook is unequipped.");
    private static bool attached;
    private static Vector3 fixedAnchor;
    private static Transform anchorTransform;
    private static Vector3 anchorLocalPoint;
    private static float ropeLength;
    private static LineRenderer rope;
    private static Material ropeMaterial;
    private static GameObject claw;
    private static Material clawMaterial;
    private static RagdollController playerRagdoll;
    private static readonly Dictionary<Rigidbody, bool> SavedCollisionStates = new();
    private static readonly System.Collections.Generic.List<GrappleCollisionPhase> CollisionSensors = new();
    private static float noClipUntil;

    public override string Name => "Grappling Hook";
    public override string Description => "Fire a physics grappling hook, swing from surfaces, and reel yourself toward the anchor.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 5f, Max = 100f, Label = "Pull acceleration")]
    public static Ref<float> PullAcceleration = new(38f);

    [ModSetting(Order = 20, Min = 1f, Max = 40f, Label = "Reel speed")]
    public static Ref<float> ReelSpeed = new(14f);

    [ModSetting(Order = 30, Min = 5f, Max = 100f, Label = "Maximum grapple speed")]
    public static Ref<float> MaximumSpeed = new(42f);

    [ModSetting(Order = 40, Label = "Phase through walls on impact",
        Description = "Briefly disables player-body collisions only after hitting a building or steep mountain surface.")]
    public static Ref<bool> NoClipWhileGrappling = new(true);

    [ModSetting(Order = 50, Min = -300f, Max = 300f, Label = "Crosshair horizontal offset")]
    public static Ref<float> CrosshairOffsetX = new(0f);

    [ModSetting(Order = 60, Min = -300f, Max = 300f, Label = "Crosshair vertical offset")]
    public static Ref<float> CrosshairOffsetY = new(0f);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("GrapplingHookHelp",
                "Equip and close F2. Left-click once to attach and automatically reel in. " +
                "Right-click to release. Momentum is preserved so you can swing around corners."),
            base.BuildPanel(id),
            new HStack("GrapplingHookActions",
                ActionMenu(new Button("Equip", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Release hook", Release), nameof(Release))
            ).WithContentWidth(),
            new TextWrapped("GrapplingHookStatus", "").WithText(Status));
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
        ShrinkRayMod.Unequip();
        LavaGunMod.Unequip();
        ChaosWandMod.Unequip();
        PortalGunMod.Unequip();
        LightningGunMod.Unequip();
        WobblyHeadHomingGunMod.Unequip();
        BananaPeelLauncherMod.Unequip();
        TornadoGunMod.Unequip();
        FireworkMinigunMod.Unequip();
        EquippedState.Value = true;
        Status.Value = "Grappling hook equipped. Left-click to attach; right-click to release.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        Release();
        Status.Value = "Grappling hook unequipped.";
    }

    [ModAction(ShowInUI = false)]
    public static void Release()
    {
        attached = false;
        anchorTransform = null;
        noClipUntil = 0f;
        RestorePlayerCollisions();
        RemoveCollisionSensors();
        DestroyRope();
        if (EquippedState.Value) Status.Value = "Hook released.";
    }

    public override void Update()
    {
        if (!EquippedState.Value) return;
        if (!TryGetPlayerBody(out var body, out var character))
        {
            if (attached) Release();
            return;
        }

        if (!Cursor.visible && Input.GetMouseButtonDown(0)) Attach(character, body);
        if (!Cursor.visible && Input.GetMouseButtonDown(1)) Release();
        if (!attached) return;

        if (SavedCollisionStates.Count > 0 && Time.unscaledTime >= noClipUntil)
            RestorePlayerCollisions();

        var anchor = CurrentAnchor();
        var offset = anchor - body.worldCenterOfMass;
        var distance = offset.magnitude;
        if (distance <= 2.4f)
        {
            var directionToAnchor = distance > 0.05f ? offset / distance : Vector3.up;
            body.velocity = Vector3.ProjectOnPlane(body.velocity, directionToAnchor) * 0.7f;
            ProtectFromKnockout(character);
            Release();
            Status.Value = "Reached hook without knockout.";
            return;
        }

        if (Cursor.visible)
        {
            UpdateRope(body.worldCenterOfMass, anchor);
            return;
        }

        ropeLength = Mathf.Max(2.2f, ropeLength - ReelSpeed.Value * Time.deltaTime);

        var direction = offset / distance;
        var tension = Mathf.Max(0f, distance - ropeLength) * 6f;
        var approachScale = Mathf.InverseLerp(2.4f, 8f, distance);
        var activePull = PullAcceleration.Value * Mathf.Lerp(0.2f, 1f, approachScale);
        body.WakeUp();
        body.AddForce(direction * (activePull + tension), ForceMode.Acceleration);
        ProtectFromKnockout(character);

        var maxSpeed = Mathf.Max(5f, MaximumSpeed.Value);
        if (body.velocity.sqrMagnitude > maxSpeed * maxSpeed)
            body.velocity = body.velocity.normalized * maxSpeed;

        UpdateRope(body.worldCenterOfMass, anchor);
    }

    private static void Attach(PlayerCharacter character, Rigidbody body)
    {
        var camera = Camera.main;
        if (!camera) { Status.Value = "No gameplay camera found."; return; }

        var aim = new Vector3(Screen.width * 0.5f + CrosshairOffsetX.Value,
            Screen.height * 0.5f - CrosshairOffsetY.Value, 0f);
        var ray = camera.ScreenPointToRay(aim);
        var hits = Physics.RaycastAll(ray, Mathf.Infinity, ~0, QueryTriggerInteraction.Ignore);
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
        var hit = hits.FirstOrDefault(item => item.collider && !item.transform.IsChildOf(character.transform));
        if (!hit.collider) { Status.Value = "Hook missed: no surface along the aim line."; return; }

        fixedAnchor = hit.point;
        anchorTransform = hit.rigidbody ? hit.rigidbody.transform : null;
        if (anchorTransform) anchorLocalPoint = anchorTransform.InverseTransformPoint(hit.point);
        ropeLength = Mathf.Max(1.5f, Vector3.Distance(body.worldCenterOfMass, hit.point));
        attached = true;
        InstallCollisionSensors(character);
        EnsureRope();
        UpdateRope(body.worldCenterOfMass, hit.point);
        Status.Value = $"Hook attached {hit.distance:0.0} m away. Reeling automatically.";
    }

    private static Vector3 CurrentAnchor() => anchorTransform ? anchorTransform.TransformPoint(anchorLocalPoint) : fixedAnchor;

    private static bool TryGetPlayerBody(out Rigidbody body, out PlayerCharacter character)
    {
        body = null;
        character = null;
        if (!GameInstance.InstanceExists) return false;
        var controller = GameInstance.Instance.GetFirstLocalPlayerController();
        character = controller ? controller.GetPlayerCharacter() : null;
        if (!character) return false;
        var playerBody = character.GetComponentInChildren<PlayerBody>(true);
        playerRagdoll = character.GetComponentInChildren<RagdollController>(true);
        body = playerBody ? playerBody.GetRigidbody() : null;
        if (!body)
            body = character.GetComponentsInChildren<Rigidbody>(true).FirstOrDefault(item => item && !item.isKinematic);
        return body;
    }

    private static void ProtectFromKnockout(PlayerCharacter character)
    {
        if (!playerRagdoll)
            playerRagdoll = character.GetComponentInChildren<RagdollController>(true);
        if (!playerRagdoll) return;

        playerRagdoll.ResetKnockoutTime();
        if (playerRagdoll.IsKnockedout() || playerRagdoll.IsActiveRagdoll())
            playerRagdoll.Wakeup(true);
    }

    private static void EnablePlayerNoClip(PlayerCharacter character)
    {
        if (SavedCollisionStates.Count > 0) return;
        foreach (var playerBody in character.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!playerBody) continue;
            SavedCollisionStates[playerBody] = playerBody.detectCollisions;
            playerBody.detectCollisions = false;
        }
    }

    private static void RestorePlayerCollisions()
    {
        foreach (var saved in SavedCollisionStates)
        {
            if (saved.Key) saved.Key.detectCollisions = saved.Value;
        }
        SavedCollisionStates.Clear();
    }

    private static void InstallCollisionSensors(PlayerCharacter character)
    {
        RemoveCollisionSensors();
        foreach (var playerBody in character.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!playerBody) continue;
            var sensor = playerBody.GetComponent<GrappleCollisionPhase>() ??
                         playerBody.gameObject.AddComponent<GrappleCollisionPhase>();
            CollisionSensors.Add(sensor);
        }
    }

    private static void RemoveCollisionSensors()
    {
        foreach (var sensor in CollisionSensors)
            if (sensor) UnityEngine.Object.Destroy(sensor);
        CollisionSensors.Clear();
    }

    internal static void NotifyStaticWallImpact(GameObject sensorObject, Collision collision)
    {
        if (!attached || !NoClipWhileGrappling.Value || collision.contactCount == 0) return;
        var otherBody = collision.collider ? collision.collider.attachedRigidbody : null;
        if (otherBody && !otherBody.isKinematic) return;

        var steepSurface = false;
        for (var index = 0; index < collision.contactCount; index++)
        {
            if (Mathf.Abs(collision.GetContact(index).normal.y) < 0.72f)
            {
                steepSurface = true;
                break;
            }
        }
        if (!steepSurface) return;

        var character = sensorObject.GetComponentInParent<PlayerCharacter>();
        if (!character) return;
        EnablePlayerNoClip(character);
        noClipUntil = Mathf.Max(noClipUntil, Time.unscaledTime + 0.65f);
        Status.Value = "Wall impact detected: briefly phasing through the obstacle.";
    }

    private static void EnsureRope()
    {
        if (rope) return;
        var root = new GameObject("ExtraMods Grappling Rope");
        rope = root.AddComponent<LineRenderer>();
        rope.positionCount = 2;
        rope.useWorldSpace = true;
        rope.startWidth = 0.055f;
        rope.endWidth = 0.035f;
        rope.numCapVertices = 4;
        var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader)
        {
            ropeMaterial = new Material(shader);
            ropeMaterial.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            rope.material = ropeMaterial;
        }
        rope.startColor = Color.black;
        rope.endColor = new Color(0.15f, 0.15f, 0.15f);
        CreateClaw(root.transform);
    }

    private static void CreateClaw(Transform ropeRoot)
    {
        claw = new GameObject("Grappling Claw");
        claw.transform.SetParent(ropeRoot, false);
        var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
        if (shader)
        {
            clawMaterial = new Material(shader);
            clawMaterial.color = new Color(0.22f, 0.24f, 0.27f, 1f);
        }

        CreateClawPart("Claw Hub", PrimitiveType.Sphere, Vector3.zero,
            Quaternion.identity, new Vector3(0.24f, 0.24f, 0.24f));
        for (var index = 0; index < 3; index++)
        {
            var angle = index * 120f;
            var rotation = Quaternion.Euler(38f, angle, 0f);
            var radial = Quaternion.Euler(0f, angle, 0f) * Vector3.right;
            CreateClawPart($"Claw Prong {index + 1}", PrimitiveType.Cylinder,
                radial * 0.18f + Vector3.forward * 0.11f, rotation,
                new Vector3(0.065f, 0.24f, 0.065f));
            CreateClawPart($"Claw Tip {index + 1}", PrimitiveType.Sphere,
                radial * 0.31f + Vector3.forward * 0.25f, Quaternion.identity,
                new Vector3(0.11f, 0.11f, 0.11f));
        }
    }

    private static void CreateClawPart(string partName, PrimitiveType primitive, Vector3 localPosition,
        Quaternion localRotation, Vector3 localScale)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = partName;
        var collider = part.GetComponent<Collider>();
        if (collider) UnityEngine.Object.Destroy(collider);
        part.transform.SetParent(claw.transform, false);
        part.transform.localPosition = localPosition;
        part.transform.localRotation = localRotation;
        part.transform.localScale = localScale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer && clawMaterial) renderer.sharedMaterial = clawMaterial;
    }

    private static void UpdateRope(Vector3 start, Vector3 end)
    {
        if (!rope) return;
        rope.SetPosition(0, start);
        rope.SetPosition(1, end);
        if (claw)
        {
            claw.transform.position = end;
            var ropeDirection = start - end;
            if (ropeDirection.sqrMagnitude > 0.001f)
                claw.transform.rotation = Quaternion.LookRotation(ropeDirection.normalized, Vector3.up);
        }
    }

    private static void DestroyRope()
    {
        if (rope) UnityEngine.Object.Destroy(rope.gameObject);
        if (ropeMaterial) UnityEngine.Object.Destroy(ropeMaterial);
        if (clawMaterial) UnityEngine.Object.Destroy(clawMaterial);
        rope = null;
        ropeMaterial = null;
        claw = null;
        clawMaterial = null;
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f + CrosshairOffsetX.Value;
        var y = Screen.height * 0.5f + CrosshairOffsetY.Value;
        var old = GUI.color;
        GUI.color = attached ? new Color(1f, 0.35f, 0.15f) : new Color(1f, 0.85f, 0.15f);
        GUI.DrawTexture(new Rect(x - 12f, y - 1f, 8f, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x + 4f, y - 1f, 8f, 2f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1f, y - 12f, 2f, 8f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 1f, y + 4f, 2f, 8f), Texture2D.whiteTexture);
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        GUI.color = old;
    }
}

internal sealed class GrappleCollisionPhase : MonoBehaviour
{
    private void OnCollisionEnter(Collision collision)
    {
        GrapplingHookMod.NotifyStaticWallImpact(gameObject, collision);
    }
}

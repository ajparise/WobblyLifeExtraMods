using System;
using System.Collections.Generic;
using FMODUnity;
using HarmonyLib;
using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;
using Object = UnityEngine.Object;

namespace WobblyLifeExtraMods;

/// <summary>Fires bubbles that trap movable targets, float them, and pop with an upward launch.</summary>
public sealed class BubbleBlasterMod : BaseMod
{
    private const string NativeShotSound = "event:/Objects/Objects_PaperCannon";
    private static readonly Vector3 HandOffset = new(0f, 0.16f, 0.18f);
    private static readonly System.Reflection.FieldInfo RightHandField =
        AccessTools.Field(typeof(RagdollController), "rightHand");
    private static readonly Ref<bool> EquippedState = new();
    private static readonly Ref<string> Status = new("Bubble Blaster is unequipped.");
    private static readonly List<FloatingBubbleTrap> ActiveBubbles = new();
    private static GameObject blasterRoot;
    private static Transform muzzle;
    private static Transform bubbleTank;
    private static RagdollHandJoint activeHand;
    private static bool handPoseRequested;
    private static float nextFireTime;
    private static float recoilUntil;

    public override string Name => "Bubble Blaster";
    public override string Description =>
        "Trap Wobblies, vehicles, and movable props in floating bubbles that eventually pop and launch them.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 10f, Max = 120f, Label = "Bubble shot speed")]
    public static Ref<float> ShotSpeed = new(48f);
    [ModSetting(Order = 20, Min = 2f, Max = 20f, Label = "Bubble lifetime")]
    public static Ref<float> BubbleLifetime = new(7f);
    [ModSetting(Order = 30, Min = 1f, Max = 20f, Label = "Floating height")]
    public static Ref<float> FloatHeight = new(7f);
    [ModSetting(Order = 40, Min = 2f, Max = 60f, Label = "Pop launch power")]
    public static Ref<float> PopLaunchPower = new(18f);
    [ModSetting(Order = 50, Min = 0.05f, Max = 1.5f, Label = "Fire interval")]
    public static Ref<float> FireInterval = new(0.28f);
    [ModSetting(Order = 60, Label = "Rapid fire")]
    public static Ref<bool> RapidFire = new();
    [ModSetting(Order = 70, Min = 1f, Max = 20f, Label = "Maximum active bubbles")]
    public static Ref<int> MaximumBubbles = new(10);

    public override Container BuildPanel(string id)
    {
        return new Container(id,
            new TextWrapped("BubbleBlasterHelp",
                "Equip and close F2, then left-click to fire. A bubble that hits a Wobbly, car, or movable physics prop " +
                "wraps the whole target, turns off gravity temporarily, and floats it upward with a gentle wobble. When the " +
                "timer ends, the bubble pops and launches its target. The firing Wobbly is protected."),
            base.BuildPanel(id),
            new HStack("BubbleBlasterActions",
                ActionMenu(new Button("Equip Bubble Blaster", Equip), nameof(Equip)),
                ActionMenu(new Button("Unequip blaster", Unequip), nameof(Unequip)),
                ActionMenu(new Button("Fire once", Fire), nameof(Fire)),
                ActionMenu(new Button("Pop all bubbles", PopAll), nameof(PopAll))
            ).WithContentWidth(),
            new TextWrapped("BubbleBlasterStatus", "").WithText(Status));
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
        JellyGunMod.Unequip();
        TemporaryTunnelDrillMod.Unequip();
        MosesStaffMod.Unequip();
        PowerSwordMod.Unequip();
        EquippedState.Value = true;
        EnsureBlasterModel();
        Status.Value = "Bubble Blaster equipped. Close F2 and left-click to fire.";
    }

    [ModAction(ShowInUI = false)]
    public static void Unequip()
    {
        EquippedState.Value = false;
        if (activeHand && handPoseRequested) activeHand.ResetPointing();
        if (blasterRoot) Object.Destroy(blasterRoot);
        blasterRoot = null;
        muzzle = null;
        bubbleTank = null;
        activeHand = null;
        handPoseRequested = false;
        Status.Value = "Bubble Blaster unequipped; active bubbles will still finish normally.";
    }

    [ModAction(ShowInUI = false)]
    public static void PopAll()
    {
        for (var i = ActiveBubbles.Count - 1; i >= 0; i--)
            if (ActiveBubbles[i]) ActiveBubbles[i].Pop();
        ActiveBubbles.Clear();
        Status.Value = "All active bubbles popped.";
    }

    public override void Update()
    {
        var character = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!character)
        {
            if (EquippedState.Value) Unequip();
            if (ActiveBubbles.Count > 0) PopAll();
            return;
        }
        if (!EquippedState.Value) return;
        EnsureBlasterModel();
        UpdateBlasterModel();
        if (Cursor.visible) return;
        if (RapidFire.Value ? Input.GetMouseButton(0) : Input.GetMouseButtonDown(0)) Fire();
    }

    [ModAction(ShowInUI = false)]
    public static void Fire()
    {
        if (!EquippedState.Value || Time.unscaledTime < nextFireTime) return;
        if (!PropSpawnManager.IsServer)
        {
            Status.Value = "Only the offline player or lobby host can fire physics bubbles.";
            return;
        }
        var camera = Camera.main;
        var shooter = GameInstance.InstanceExists
            ? GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter()
            : null;
        if (!camera || !shooter)
        {
            Status.Value = "Enter a save before firing the Bubble Blaster.";
            return;
        }

        nextFireTime = Time.unscaledTime + Mathf.Clamp(FireInterval.Value, 0.05f, 1.5f);
        recoilUntil = Time.unscaledTime + 0.09f;
        var ray = camera.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var position = muzzle ? muzzle.position : ray.origin + ray.direction * 1.2f;
        var projectile = new GameObject("ExtraMods Bubble Projectile");
        projectile.transform.position = position;
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Shimmering Bubble";
        Object.DestroyImmediate(sphere.GetComponent<Collider>());
        sphere.transform.SetParent(projectile.transform, false);
        sphere.transform.localScale = Vector3.one * 0.65f;
        ConfigureBubbleMaterial(sphere.GetComponent<Renderer>(), 0.48f);
        var collider = projectile.AddComponent<SphereCollider>();
        collider.radius = 0.34f;
        var body = projectile.AddComponent<Rigidbody>();
        body.useGravity = false;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.velocity = ray.direction * Mathf.Clamp(ShotSpeed.Value, 10f, 120f);
        var shot = projectile.AddComponent<BubbleProjectile>();
        shot.Configure(shooter, 5f);
        foreach (var shooterCollider in shooter.GetComponentsInChildren<Collider>(true))
            if (shooterCollider) Physics.IgnoreCollision(collider, shooterCollider, true);
        PlayShotSound(position);
        Status.Value = "Bubble fired.";
    }

    internal static bool TryTrap(Collider hitCollider, PlayerCharacter shooter, Vector3 direction)
    {
        if (!hitCollider || !shooter || hitCollider.transform.IsChildOf(shooter.transform)) return false;
        var targetRoot = ResolveTarget(hitCollider, out var bodies, out var label);
        if (!targetRoot || bodies.Count == 0) return false;
        foreach (var bubble in ActiveBubbles)
            if (bubble && bubble.ContainsAny(bodies))
            {
                bubble.Refresh(Mathf.Clamp(BubbleLifetime.Value, 2f, 20f));
                Status.Value = $"Refreshed the bubble around {label}.";
                return true;
            }

        CleanupBubbles();
        var limit = Mathf.Clamp(MaximumBubbles.Value, 1, 20);
        while (ActiveBubbles.Count >= limit)
        {
            var oldest = ActiveBubbles[0];
            ActiveBubbles.RemoveAt(0);
            if (oldest) oldest.Pop();
        }
        var bubbleObject = new GameObject("ExtraMods Floating Bubble Trap");
        var trap = bubbleObject.AddComponent<FloatingBubbleTrap>();
        trap.Initialize(targetRoot, bodies, direction, Mathf.Clamp(BubbleLifetime.Value, 2f, 20f),
            Mathf.Clamp(FloatHeight.Value, 1f, 20f), Mathf.Clamp(PopLaunchPower.Value, 2f, 60f));
        ActiveBubbles.Add(trap);
        Status.Value = $"{label} trapped in a floating bubble.";
        return true;
    }

    private static GameObject ResolveTarget(Collider collider, out List<Rigidbody> bodies, out string label)
    {
        bodies = new List<Rigidbody>();
        var vehicle = collider.GetComponentInParent<PlayerVehicle>();
        if (vehicle)
        {
            AddBodies(vehicle.GetComponentsInChildren<Rigidbody>(true), bodies);
            label = "Vehicle";
            return vehicle.gameObject;
        }
        var character = collider.GetComponentInParent<PlayerCharacter>();
        if (character)
        {
            character.GetRagdollController()?.Ragdoll();
            AddBodies(character.GetComponentsInChildren<Rigidbody>(true), bodies);
            label = collider.GetComponentInParent<PlayerNPCController>() ? "NPC" : "Wobbly";
            if (label == "NPC") PoliceChaseMod.ReportNpcHarassment("bubble blaster hit");
            return character.gameObject;
        }
        var dynamicObject = collider.GetComponentInParent<DynamicObject>();
        if (dynamicObject)
        {
            var body = dynamicObject.GetRigidbody();
            if (body && !body.isKinematic) bodies.Add(body);
            label = "Physics prop";
            return dynamicObject.gameObject;
        }
        var attached = collider.attachedRigidbody;
        if (attached && !attached.isKinematic)
        {
            bodies.Add(attached);
            label = "Physics object";
            return attached.gameObject;
        }
        label = "Static surface";
        return null;
    }

    private static void AddBodies(IEnumerable<Rigidbody> source, ICollection<Rigidbody> destination)
    {
        foreach (var body in source)
            if (body && !body.isKinematic && !destination.Contains(body)) destination.Add(body);
    }

    internal static void Unregister(FloatingBubbleTrap bubble) => ActiveBubbles.Remove(bubble);

    private static void CleanupBubbles()
    {
        for (var i = ActiveBubbles.Count - 1; i >= 0; i--)
            if (!ActiveBubbles[i]) ActiveBubbles.RemoveAt(i);
    }

    internal static void ConfigureBubbleMaterial(Renderer renderer, float alpha)
    {
        if (!renderer) return;
        var material = renderer.material;
        material.color = new Color(0.18f, 0.78f, 1f, alpha);
        material.SetFloat("_Mode", 3f);
        material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.DisableKeyword("_ALPHATEST_ON");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.renderQueue = 3000;
        if (material.HasProperty("_EmissionColor"))
        {
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", new Color(0.04f, 0.38f, 0.7f) * 0.8f);
        }
    }

    private static void EnsureBlasterModel()
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
        if (blasterRoot)
        {
            if (blasterRoot.transform.parent != anchor) blasterRoot.transform.SetParent(anchor, true);
            return;
        }

        blasterRoot = new GameObject("ExtraMods Bubble Blaster");
        blasterRoot.transform.SetParent(anchor, true);
        CreatePart("Bubble Receiver", PrimitiveType.Cube, new Vector3(0f, 0f, 0.2f), Vector3.zero,
            new Vector3(0.3f, 0.28f, 0.9f), new Color(0.08f, 0.38f, 0.72f), false);
        CreatePart("Bubble Grip", PrimitiveType.Cube, new Vector3(0f, -0.3f, -0.02f), new Vector3(-14f, 0f, 0f),
            new Vector3(0.17f, 0.44f, 0.22f), new Color(0.08f, 0.12f, 0.2f), false);
        bubbleTank = CreatePart("Bubble Tank", PrimitiveType.Sphere, new Vector3(0f, 0.24f, 0.1f), Vector3.zero,
            new Vector3(0.48f, 0.48f, 0.48f), new Color(0.18f, 0.78f, 1f, 0.55f), true).transform;
        ConfigureBubbleMaterial(bubbleTank.GetComponent<Renderer>(), 0.55f);
        CreatePart("Bubble Muzzle Ring", PrimitiveType.Cylinder, new Vector3(0f, 0f, 0.86f), new Vector3(90f, 0f, 0f),
            new Vector3(0.27f, 0.08f, 0.27f), new Color(0.75f, 0.92f, 1f), true);
        muzzle = new GameObject("Bubble Muzzle").transform;
        muzzle.SetParent(blasterRoot.transform, false);
        muzzle.localPosition = new Vector3(0f, 0f, 1.05f);
    }

    private static void UpdateBlasterModel()
    {
        if (!blasterRoot || !activeHand || !Camera.main) return;
        var anchor = activeHand.GetAnchorTransform();
        if (!anchor) return;
        var ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));
        var rotation = Quaternion.LookRotation(ray.direction, Camera.main.transform.up);
        var position = anchor.position + rotation * HandOffset;
        if (Time.unscaledTime < recoilUntil) position -= ray.direction * 0.11f;
        blasterRoot.transform.position = Vector3.Lerp(blasterRoot.transform.position, position, 0.68f);
        blasterRoot.transform.rotation = Quaternion.Slerp(blasterRoot.transform.rotation, rotation, 0.68f);
        if (bubbleTank)
        {
            var pulse = 1f + Mathf.Sin(Time.unscaledTime * 4f) * 0.08f;
            bubbleTank.localScale = Vector3.one * 0.48f * pulse;
        }
    }

    private static GameObject CreatePart(string name, PrimitiveType primitive, Vector3 position, Vector3 rotation,
        Vector3 scale, Color color, bool emissive)
    {
        var part = GameObject.CreatePrimitive(primitive);
        part.name = name;
        var collider = part.GetComponent<Collider>();
        if (collider) Object.DestroyImmediate(collider);
        part.transform.SetParent(blasterRoot.transform, false);
        part.transform.localPosition = position;
        part.transform.localRotation = Quaternion.Euler(rotation);
        part.transform.localScale = scale;
        var renderer = part.GetComponent<Renderer>();
        if (renderer)
        {
            renderer.material.color = color;
            if (emissive && renderer.material.HasProperty("_EmissionColor"))
            {
                renderer.material.EnableKeyword("_EMISSION");
                renderer.material.SetColor("_EmissionColor", color * 1.5f);
            }
        }
        return part;
    }

    private static RagdollHandJoint ResolveRightHand()
    {
        if (!GameInstance.InstanceExists || RightHandField == null) return null;
        var character = GameInstance.Instance.GetFirstLocalPlayerController()?.GetPlayerCharacter();
        var ragdoll = character ? character.GetRagdollController() : null;
        return ragdoll ? RightHandField.GetValue(ragdoll) as RagdollHandJoint : null;
    }

    private static void PlayShotSound(Vector3 position)
    {
        try
        {
            var sound = RuntimeManager.CreateInstance(NativeShotSound);
            sound.set3DAttributes(RuntimeUtils.To3DAttributes(position));
            sound.setPitch(1.35f);
            sound.start();
            sound.release();
        }
        catch (Exception exception) { Plugin.Log?.LogWarning($"Bubble shot sound failed: {exception.Message}"); }
    }

    internal static void DrawCrosshair()
    {
        if (!EquippedState.Value || Cursor.visible || Event.current.type != EventType.Repaint) return;
        var x = Screen.width * 0.5f;
        var y = Screen.height * 0.5f;
        var old = GUI.color;
        GUI.color = new Color(0.2f, 0.82f, 1f, 0.95f);
        GUI.DrawTexture(new Rect(x - 2f, y - 2f, 4f, 4f), Texture2D.whiteTexture);
        for (var i = 0; i < 4; i++)
        {
            var angle = Time.unscaledTime * 45f + i * 90f;
            var offset = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * 12f;
            GUI.DrawTexture(new Rect(x + offset.x - 2f, y + offset.y - 2f, 4f, 4f), Texture2D.whiteTexture);
        }
        GUI.color = old;
    }
}

internal sealed class BubbleProjectile : MonoBehaviour
{
    private PlayerCharacter shooter;
    private float expiresAt;
    private bool consumed;
    internal void Configure(PlayerCharacter owner, float lifetime)
    {
        shooter = owner;
        expiresAt = Time.time + lifetime;
    }
    private void Update()
    {
        transform.localScale = Vector3.one * (1f + Mathf.Sin(Time.time * 9f) * 0.08f);
        if (Time.time >= expiresAt) Destroy(gameObject);
    }
    private void OnCollisionEnter(Collision collision)
    {
        if (consumed || !collision.collider || collision.collider.transform.IsChildOf(shooter.transform)) return;
        consumed = true;
        var velocity = GetComponent<Rigidbody>()?.velocity ?? transform.forward;
        BubbleBlasterMod.TryTrap(collision.collider, shooter, velocity.normalized);
        Destroy(gameObject);
    }
}

internal sealed class FloatingBubbleTrap : MonoBehaviour
{
    private sealed class BodyState
    {
        internal Rigidbody Body;
        internal bool UseGravity;
        internal float Drag;
        internal float AngularDrag;
    }

    private readonly List<BodyState> states = new();
    private GameObject target;
    private Transform shell;
    private Vector3 anchor;
    private Vector3 initialAnchor;
    private Vector3 launchDirection;
    private float expiresAt;
    private float floatHeight;
    private float launchPower;
    private float riseAmount;
    private float shellSize;
    private bool popped;

    internal void Initialize(GameObject targetObject, IEnumerable<Rigidbody> bodies, Vector3 direction,
        float lifetime, float height, float popPower)
    {
        target = targetObject;
        launchDirection = direction.sqrMagnitude > 0.01f ? direction.normalized : Vector3.forward;
        floatHeight = height;
        launchPower = popPower;
        expiresAt = Time.time + lifetime;
        foreach (var body in bodies)
        {
            if (!body) continue;
            states.Add(new BodyState { Body = body, UseGravity = body.useGravity, Drag = body.drag, AngularDrag = body.angularDrag });
            body.useGravity = false;
            body.drag = 3.2f;
            body.angularDrag = 2.4f;
        }
        anchor = Center();
        initialAnchor = anchor;
        BuildShell();
    }

    internal bool ContainsAny(IEnumerable<Rigidbody> bodies)
    {
        foreach (var body in bodies)
        foreach (var state in states)
            if (body == state.Body) return true;
        return false;
    }

    internal void Refresh(float lifetime) => expiresAt = Time.time + lifetime;

    private void FixedUpdate()
    {
        if (!target || states.Count == 0) { Pop(); return; }
        var remaining = Mathf.Max(0f, expiresAt - Time.time);
        riseAmount = Mathf.MoveTowards(riseAmount, floatHeight, floatHeight * 0.42f * Time.fixedDeltaTime);
        anchor = initialAnchor + Vector3.up * riseAmount +
                 new Vector3(Mathf.Sin(Time.time * 1.7f), Mathf.Sin(Time.time * 2.2f) * 0.18f,
                     Mathf.Cos(Time.time * 1.3f)) * 0.45f;
        var center = Center();
        foreach (var state in states)
        {
            var body = state.Body;
            if (!body) continue;
            var relative = body.worldCenterOfMass - center;
            var desired = anchor + relative * 0.8f;
            body.AddForce((desired - body.worldCenterOfMass) * 7f - body.velocity * 1.4f, ForceMode.Acceleration);
        }
        if (shell)
        {
            shell.position = center;
            var pulse = 1f + Mathf.Sin(Time.time * 4f) * 0.035f;
            shell.localScale = Vector3.one * shellSize * pulse;
        }
        if (remaining <= 0f) Pop();
    }

    private Vector3 Center()
    {
        var total = Vector3.zero;
        var count = 0;
        foreach (var state in states)
            if (state.Body) { total += state.Body.worldCenterOfMass; count++; }
        return count > 0 ? total / count : transform.position;
    }

    private void BuildShell()
    {
        var bounds = new Bounds(Center(), Vector3.one);
        var initialized = false;
        foreach (var state in states)
        {
            if (!state.Body) continue;
            foreach (var renderer in state.Body.GetComponentsInChildren<Renderer>(true))
            {
                if (!renderer) continue;
                if (!initialized) { bounds = renderer.bounds; initialized = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
        }
        var size = Mathf.Clamp(Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z) + 1.2f, 2f, 14f);
        shellSize = size;
        var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        visual.name = "Floating Bubble Shell";
        Object.DestroyImmediate(visual.GetComponent<Collider>());
        visual.transform.SetParent(transform, true);
        visual.transform.position = Center();
        visual.transform.localScale = Vector3.one * size;
        BubbleBlasterMod.ConfigureBubbleMaterial(visual.GetComponent<Renderer>(), 0.3f);
        shell = visual.transform;
        var light = visual.AddComponent<Light>();
        light.color = new Color(0.15f, 0.72f, 1f);
        light.range = size * 1.4f;
        light.intensity = 0.7f;
    }

    internal void Pop()
    {
        if (popped) return;
        popped = true;
        foreach (var state in states)
        {
            var body = state.Body;
            if (!body) continue;
            body.useGravity = state.UseGravity;
            body.drag = state.Drag;
            body.angularDrag = state.AngularDrag;
            body.AddForce((Vector3.up * 0.78f + launchDirection * 0.22f).normalized * launchPower,
                ForceMode.VelocityChange);
        }
        if (shell)
        {
            var burst = shell.gameObject;
            shell = null;
            burst.transform.localScale *= 1.35f;
            Object.Destroy(burst, 0.12f);
        }
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (!popped)
        {
            foreach (var state in states)
                if (state.Body)
                {
                    state.Body.useGravity = state.UseGravity;
                    state.Body.drag = state.Drag;
                    state.Body.angularDrag = state.AngularDrag;
                }
        }
        BubbleBlasterMod.Unregister(this);
    }
}

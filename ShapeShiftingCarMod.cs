using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife.PropSpawner;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class ShapeShiftingCarMod : BaseMod
{
    private static readonly Ref<string> Status = new("Disabled. Enter a car and enable the mode.");
    private static bool enabled;
    private static int mode;
    private static PlayerVehicle activeVehicle;
    private static ShapeCarPhysics driver;
    public override string Name => "Shape-Shifting Car";
    public override string Description => "Switch your current car between road, flight and hover-boat modes.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;
    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("ShiftCarHelp", "Host/offline: enter a car and enable this mode. Press J to cycle road, flight, and hover-boat. In flight, W/S add thrust, A/D steer, Space rises and Left Ctrl descends. Hover-boat supports the car over water or ground; normal steering still applies. Disable to return to normal driving."),
        new HStack("ShiftCarActions", ActionMenu(new Button("Enable", Enable), nameof(Enable)), ActionMenu(new Button("Disable", Disable), nameof(Disable))).WithContentWidth(),
        new TextWrapped("ShiftCarStatus", "").WithText(Status));
    [ModAction(ShowInUI = false)] public static void Enable()
    {
        if (!PropSpawnManager.IsServer) { Status.Value = "Only the host or offline player can use this mode."; return; }
        enabled = true; mode = 0; Status.Value = "Enabled: ROAD mode. Enter a car and press J to transform.";
    }
    [ModAction(ShowInUI = false)] public static void Disable() { enabled = false; mode = 0; if (driver) Object.Destroy(driver); driver = null; activeVehicle = null; Status.Value = "Disabled; normal vehicle physics restored."; }
    public override void Update()
    {
        if (!enabled) return;
        if (!GameInstance.InstanceExists) { Disable(); return; }
        var player = GameInstance.Instance.GetFirstLocalPlayerController();
        var entered = player ? player.GetPlayerControllerInteractor()?.GetEnteredAction()?.GetGameObject() : null;
        var vehicle = entered ? entered.GetComponentInParent<PlayerVehicle>() : null;
        if (!vehicle || vehicle.GetDriverPlayerController() != player) { if (driver) Object.Destroy(driver); driver = null; activeVehicle = null; mode = 0; return; }
        if (activeVehicle != vehicle) { if (driver) Object.Destroy(driver); activeVehicle = vehicle; driver = vehicle.gameObject.AddComponent<ShapeCarPhysics>(); mode = 0; Status.Value = "ROAD mode. Press J to transform."; }
        if (!Cursor.visible && Input.GetKeyDown(KeyCode.J)) { mode = (mode + 1) % 3; Status.Value = "Vehicle mode: " + (mode == 0 ? "ROAD" : mode == 1 ? "FLIGHT" : "HOVER-BOAT") + ". Press J to switch."; }
    }
    internal static void ApplyVehicleForces(ShapeCarPhysics caller)
    {
        if (!enabled || caller != driver || mode == 0 || !activeVehicle || Cursor.visible) return;
        var movement = activeVehicle.GetVehicleMovementBase();
        var body = movement ? movement.GetRigidbody() : activeVehicle.GetComponent<Rigidbody>();
        if (!body) return;
        if (mode == 1)
        {
            body.AddForce(Vector3.up * Physics.gravity.magnitude, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.W)) body.AddForce(activeVehicle.transform.forward * 28f, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.S)) body.AddForce(-activeVehicle.transform.forward * 18f, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.Space)) body.AddForce(Vector3.up * 18f, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.LeftControl)) body.AddForce(Vector3.down * 18f, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.A)) body.AddTorque(Vector3.down * 3f, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.D)) body.AddTorque(Vector3.up * 3f, ForceMode.Acceleration);
        }
        else
        {
            body.AddForce(Vector3.up * Physics.gravity.magnitude - Vector3.up * body.velocity.y * 2f, ForceMode.Acceleration);
            body.AddForce(-body.velocity * 0.35f, ForceMode.Acceleration);
            if (Input.GetKey(KeyCode.W)) body.AddForce(activeVehicle.transform.forward * 15f, ForceMode.Acceleration);
        }
    }
    internal static void Shutdown() => Disable();
}

public sealed class ShapeCarPhysics : MonoBehaviour
{
    private void FixedUpdate() => ShapeShiftingCarMod.ApplyVehicleForces(this);
}

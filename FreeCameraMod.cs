using lstwoMODS_Core.Hacks;
using lstwoMODS_Core.UI;
using lstwoMODS_Core.UI.Elements;
using lstwoMODS_Core.UI.TabMenus;
using lstwoMODS_WobblyLife;
using UnityEngine;

namespace WobblyLifeExtraMods;

public sealed class FreeCameraMod : BaseMod
{
    private static readonly Ref<string> Status = new("Free camera is off.");
    private static readonly object InputHandle = new();
    private static FreeCameraDriver driver;
    private static PlayerControllerInputManager input;
    private static CursorLockMode oldLock;
    private static bool oldVisible;

    public override string Name => "Free Camera";
    public override string Description => "Fly around the world with a free camera; press O to return.";
    public override ModsWindow ModsWindow => lstwoMODS_WobblyLife.Plugin.ExtraModsWindow;

    [ModSetting(Order = 10, Min = 2f, Max = 100f, Label = "Camera speed")]
    public static Ref<float> Speed = new(20f);

    public override Container BuildPanel(string id) => new Container(id,
        new TextWrapped("FreeCameraHelp", "Start free camera in a world. WASD moves, mouse looks, Space rises, Left Ctrl descends, and Shift moves faster. Press O to return to your player."),
        base.BuildPanel(id),
        new HStack("FreeCameraActions", ActionMenu(new Button("Start free camera", StartCamera), nameof(StartCamera)),
            ActionMenu(new Button("Return to player", StopCamera), nameof(StopCamera))).WithContentWidth(),
        new TextWrapped("FreeCameraStatus", "").WithText(Status));

    [ModAction(ShowInUI = false)]
    public static void StartCamera()
    {
        if (driver) return;
        var player = GameInstance.InstanceExists ? GameInstance.Instance.GetFirstLocalPlayerController() : null;
        var camera = Camera.main;
        if (!player || !player.GetPlayerCharacter() || !camera)
        {
            Status.Value = "Enter a world before starting free camera.";
            return;
        }
        input = player.GetPlayerControllerInputManager();
        if (input) { input.DisablePlayerTransformInput(InputHandle); input.DisableInteratorInput(InputHandle); }
        oldLock = Cursor.lockState;
        oldVisible = Cursor.visible;
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        driver = camera.gameObject.AddComponent<FreeCameraDriver>();
        driver.Initialize(camera.transform);
        Status.Value = "Free camera active. Press O to return.";
    }

    [ModAction(ShowInUI = false)]
    public static void StopCamera()
    {
        if (!driver && !input) return;
        if (driver) Object.Destroy(driver);
        driver = null;
        if (input) { input.EnablePlayerTransformInput(InputHandle); input.EnableInteratorInput(InputHandle); }
        input = null;
        Cursor.lockState = oldLock;
        Cursor.visible = oldVisible;
        Status.Value = "Free camera off; camera returned to player.";
    }

    public override void Update()
    {
        if (driver && (!GameInstance.InstanceExists || Input.GetKeyDown(KeyCode.O))) StopCamera();
        else if (!driver && input) StopCamera();
    }

    internal static void Shutdown() => StopCamera();
}

public sealed class FreeCameraDriver : MonoBehaviour
{
    private Vector3 position;
    private float yaw;
    private float pitch;

    public void Initialize(Transform cameraTransform)
    {
        position = cameraTransform.position;
        yaw = cameraTransform.eulerAngles.y;
        pitch = cameraTransform.eulerAngles.x;
        if (pitch > 180f) pitch -= 360f;
    }

    private void LateUpdate()
    {
        yaw += Input.GetAxis("Mouse X") * 2f;
        pitch = Mathf.Clamp(pitch - Input.GetAxis("Mouse Y") * 2f, -89f, 89f);
        var rotation = Quaternion.Euler(pitch, yaw, 0f);
        var direction = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) direction += rotation * Vector3.forward;
        if (Input.GetKey(KeyCode.S)) direction -= rotation * Vector3.forward;
        if (Input.GetKey(KeyCode.D)) direction += rotation * Vector3.right;
        if (Input.GetKey(KeyCode.A)) direction -= rotation * Vector3.right;
        if (Input.GetKey(KeyCode.Space)) direction += Vector3.up;
        if (Input.GetKey(KeyCode.LeftControl)) direction -= Vector3.up;
        var boost = Input.GetKey(KeyCode.LeftShift) ? 3f : 1f;
        position += direction.normalized * Mathf.Clamp(FreeCameraMod.Speed.Value, 2f, 100f) * boost * Time.unscaledDeltaTime;
        transform.SetPositionAndRotation(position, rotation);
    }
}

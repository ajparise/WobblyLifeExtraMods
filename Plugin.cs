using BepInEx;

namespace WobblyLifeExtraMods;

[BepInPlugin(Guid, Name, Version)]
[BepInDependency("net.lstwo.lstwomods_core")]
[BepInDependency("lstwoMODS_WobblyLife")]
public sealed class Plugin : BaseUnityPlugin
{
    public const string Guid = "com.aj.wobblylife.extramods";
    public const string Name = "Wobbly Life Extra Mods";
    public const string Version = "1.12.0";

    internal static BepInEx.Logging.ManualLogSource Log { get; private set; }

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        Logger.LogInfo($"{Name} {Version} loaded; waiting for lstwoMODS to discover extension mods.");
    }

    private void OnGUI()
    {
        WindCannonMod.DrawCrosshair();
        PropSpawnerGunMod.DrawCrosshair();
        VehicleAircraftSpawnerMod.DrawCrosshair();
        RocketLauncherMod.DrawCrosshair();
        PaintballGunMod.DrawCrosshair();
        MinecraftBuildingMod.DrawCrosshair();
        HeavyAutomaticGunMod.DrawCrosshair();
        GrapplingHookMod.DrawCrosshair();
        ShrinkRayMod.DrawCrosshair();
        LaserEyesMod.DrawCrosshair();
        CamouflagePropHuntMod.DrawCrosshair();
        LavaGunMod.DrawCrosshair();
    }

    internal static UnityEngine.Coroutine RunCoroutine(System.Collections.IEnumerator routine)
    {
        return Instance.StartCoroutine(routine);
    }

    private static Plugin Instance { get; set; }
}

#if !MELONLOADER
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace NocturneFlatScroll;

[BepInPlugin(ModInfo.Id, ModInfo.Name, ModInfo.Version)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        ModLog.Initialize(message => Log.LogInfo(message), message => Log.LogError(message));
        OptionsMenuIntegration.Install(new Harmony(ModInfo.Id));
        OptionsMenuIntegration.AttachToExistingMenus();
        AddComponent<LayoutController>();
        ModLog.Info("Flat scrolling loaded; select a layout in Options > Gameplay > Note scrolling.");
    }
}

public sealed class LayoutController : MonoBehaviour
{
    public LayoutController(IntPtr pointer) : base(pointer) { }
    public void Update() => LayoutDriver.Update();
    public void LateUpdate() => LayoutDriver.LateUpdate();
}
#endif

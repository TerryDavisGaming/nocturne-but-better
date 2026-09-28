#if !MELONLOADER
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace NocturnePlus;

[BepInPlugin(ModInfo.Id, ModInfo.Name, ModInfo.Version)]
public sealed class Plugin : BasePlugin
{
    public override void Load()
    {
        ModLog.Initialize(message => Log.LogInfo(message), message => Log.LogError(message));
        ModSetup.Patch(new Harmony(ModInfo.Id));
        ModSetup.AttachToExisting();
        AddComponent<LayoutController>();
        ModLog.Info($"{ModInfo.Name} {ModInfo.Version} loaded; its settings are in Options > Gameplay and Options > Audio.");
    }
}

public sealed class LayoutController : MonoBehaviour
{
    public LayoutController(IntPtr pointer) : base(pointer) { }
    public void Update() => LayoutDriver.Update();
    public void LateUpdate() => LayoutDriver.LateUpdate();
}
#endif

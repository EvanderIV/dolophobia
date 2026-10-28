using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Dolophobia.Game;

namespace Dolophobia;

[BepInPlugin(Guid, "Dolophobia", Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "dev.dolophobia.mod";
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger;
    internal static ModConfig Settings;

    public override void Load()
    {
        Logger = Log;
        Settings = new ModConfig(Config);

        Harmony.CreateAndPatchAll(typeof(Patches), Guid);
        AddComponent<DolophobiaBehaviour>();

        Logger.LogInfo($"Dolophobia {Version} loaded. Relay: {Settings.RelayUrl.Value}");
    }
}

using BepInEx.Preloader.Core.Patching;

[PatcherPluginInfo("t.fixture.early", "EarlyPatcher", "1.0.0")]
public class EarlyPatcher : MidLate
{
    public override void Initialize()
    {
        Log.LogInfo("EarlyPatcher active");
    }
}

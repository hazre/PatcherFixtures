using BepInEx.Preloader.Core.Patching;

namespace Fixtures;

[PatcherPluginInfo("t.fixture.direct", "DirectPatcher", "1.0.0")]
public class DirectPatcher : BasePatcher
{
    public override void Initialize()
    {
        Log.LogInfo("DirectPatcher active");
    }
}

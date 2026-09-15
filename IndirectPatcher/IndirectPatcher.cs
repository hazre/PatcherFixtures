using BepInEx.Preloader.Core.Patching;

namespace Fixtures;

[PatcherPluginInfo("t.fixture.indirect", "IndirectPatcher", "1.0.0")]
public class IndirectPatcher : MidPatcher
{
    public override void Initialize()
    {
        Log.LogInfo("IndirectPatcher active");
    }
}

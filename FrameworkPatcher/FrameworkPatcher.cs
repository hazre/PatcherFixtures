using BepInEx.Preloader.Core.Patching;

namespace Fixtures;

[FrameworkMeta("t.fixture.framework", "FrameworkPatcher", "1.0.0")]
public class FrameworkPatcher : FrameworkBase<FrameworkPatcher>
{
    public override void Initialize()
    {
        Log.LogInfo("FrameworkPatcher active");
    }
}

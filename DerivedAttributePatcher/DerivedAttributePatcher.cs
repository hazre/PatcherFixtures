using BepInEx.Preloader.Core.Patching;

namespace Fixtures;

public class DerivedPatcherInfoAttribute : PatcherPluginInfoAttribute
{
    public DerivedPatcherInfoAttribute(string GUID, string Name, string Version) : base(GUID, Name, Version) { }
}

[DerivedPatcherInfo("t.fixture.derived-attr", "DerivedAttributePatcher", "1.0.0")]
public class DerivedAttributePatcher : BasePatcher
{
    public override void Initialize()
    {
        Log.LogInfo("DerivedAttributePatcher active");
    }
}

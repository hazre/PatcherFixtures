using BepInEx.Preloader.Core.Patching;

namespace Fixtures;

public class FrameworkMetaAttribute : PatcherPluginInfoAttribute
{
    public FrameworkMetaAttribute(string GUID, string Name, string Version) : base(GUID, Name, Version) { }
}

public abstract class FrameworkBase<T> : BasePatcher where T : FrameworkBase<T>, new() { }

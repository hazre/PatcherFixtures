using UniModFramework;

namespace Fixtures;

public class UniExampleConfig : Config
{
    public ConfigurationKey<bool> ExampleToggle = new("ExampleToggle", "Example toggle", false);
}

[PrePatcherMetadata("t.fixture.uniexample", "UniExamplePatcher", "1.0.0")]
public class UniExamplePatcher : UniPrePatcher<UniExamplePatcher, UniExampleConfig>
{
    protected override bool OnInitialize()
    {
        LogInfo("UniExamplePatcher active");
        return true;
    }

    protected override bool OnFinalize()
    {
        return true;
    }
}

using BepInEx;
using BepInEx.NET.Common;

namespace Fixtures;

[BepInPlugin("t.fixture.plain", "PlainPlugin", "1.0.0")]
public class PlainPlugin : BasePlugin
{
    public override void Load()
    {
        Log.LogInfo("PlainPlugin loaded");
    }
}

using Xunit;

namespace Hitch.Tests;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This integration test exercises the Windows ConPTY or profile installer adapter.";
    }
}

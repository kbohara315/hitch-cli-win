using Hitch;
using Xunit;

namespace Hitch.Tests;

public sealed class AgentSkillTests
{
    [Fact]
    public void HitchSkillOutputMatchesTheRepositorySource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CONTEXT.md")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory!.FullName, "SKILL.md"));

        Assert.Equal(source, Program.GetAgentInstructions());
        Assert.Contains("name: hitch", source, StringComparison.Ordinal);
        Assert.Contains("Session ID", source, StringComparison.Ordinal);
        Assert.Contains("hitch send-keys", source, StringComparison.Ordinal);
    }
}

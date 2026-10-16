using Hitch.Setup;
using Xunit;

namespace Hitch.Tests;

public sealed class ShellIntegrationInstallerTests
{
    [Fact]
    public async Task InstallBroadcastsEnvironmentChangeAfterUpdatingUserPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-installer-test-" + Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        var executable = Path.Combine(root, "hitch.exe");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(executable, "test executable");
        var events = new List<string>();
        var installer = new ShellIntegrationInstaller(
            Path.Combine(root, "install"),
            documents,
            path => events.Add($"path:{path}"),
            () => events.Add("environment-changed"));
        var originalInput = Console.In;

        try
        {
            Console.SetIn(new StringReader("yes" + Environment.NewLine));
            Assert.True(await installer.InstallAsync(executable));
            Assert.Equal(2, events.Count);
            Assert.StartsWith("path:", events[0], StringComparison.Ordinal);
            Assert.Equal("environment-changed", events[1]);
        }
        finally
        {
            Console.SetIn(originalInput);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

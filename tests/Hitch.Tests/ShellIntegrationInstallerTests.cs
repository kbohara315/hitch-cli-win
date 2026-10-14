using Hitch.Setup;
using Xunit;

namespace Hitch.Tests;

public sealed class ShellIntegrationInstallerTests
{
    [WindowsFact]
    public async Task NonInteractiveInstallIsRepeatableWithoutTouchingUserProfileOrPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "hitch-tests-" + Guid.NewGuid().ToString("N"));
        var documents = Path.Combine(root, "documents");
        var sourceExecutable = Path.Combine(root, "source.exe");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(sourceExecutable, "test executable");
        var addedPathDirectories = new List<string>();
        var installer = new ShellIntegrationInstaller(
            Path.Combine(root, "install"), documents, addedPathDirectories.Add);

        try
        {
            Assert.True(await installer.InstallNonInteractiveAsync(sourceExecutable));
            var profile = Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1");
            var wrapper = Path.Combine(root, "install", "bin", "hitch.cmd");
            var installedExecutable = Path.Combine(root, "install", "app", "hitch.exe");
            var firstProfile = await File.ReadAllTextAsync(profile);
            var firstWrapper = await File.ReadAllTextAsync(wrapper);
            var profileWrite = File.GetLastWriteTimeUtc(profile);
            var wrapperWrite = File.GetLastWriteTimeUtc(wrapper);
            var executableWrite = File.GetLastWriteTimeUtc(installedExecutable);
            var backupDirectory = Path.Combine(root, "install", "backups");
            var backupsBeforeRerun = Directory.Exists(backupDirectory) ? Directory.GetFiles(backupDirectory).Length : 0;
            Assert.True(await installer.InstallNonInteractiveAsync(sourceExecutable));

            Assert.Equal(firstProfile, await File.ReadAllTextAsync(profile));
            Assert.Equal(firstWrapper, await File.ReadAllTextAsync(wrapper));
            Assert.Equal(profileWrite, File.GetLastWriteTimeUtc(profile));
            Assert.Equal(wrapperWrite, File.GetLastWriteTimeUtc(wrapper));
            Assert.Equal(executableWrite, File.GetLastWriteTimeUtc(installedExecutable));
            Assert.Equal(backupsBeforeRerun, Directory.Exists(backupDirectory) ? Directory.GetFiles(backupDirectory).Length : 0);
            Assert.Single(addedPathDirectories);
            Assert.All(addedPathDirectories, path => Assert.EndsWith(Path.Combine("install", "bin"), path));
            Assert.True(File.Exists(Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

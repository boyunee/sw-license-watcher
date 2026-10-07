namespace SwLicenseWatcher.Agent.Worker.Tests;

public class LocalIdentityFileAclTests
{
    [Fact]
    public void RestrictPrivateKey_returns_false_when_the_file_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "slw-missing-" + Guid.NewGuid().ToString("N"));

        Assert.False(LocalIdentityFileAcl.RestrictPrivateKey(path));
    }

    [Fact]
    public void RestrictPrivateKey_restricts_an_existing_file_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), "slw-acl-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "key");
        try
        {
            Assert.True(LocalIdentityFileAcl.RestrictPrivateKey(path));
            Assert.True(File.Exists(path));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

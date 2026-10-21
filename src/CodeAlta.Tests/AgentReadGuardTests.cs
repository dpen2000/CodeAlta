using CodeAlta.Agent.Runtime.Tools;

namespace CodeAlta.Tests;

[TestClass]
public sealed class AgentReadGuardTests
{
    [TestMethod]
    public void TheGuard_LetsASessionReadInsideTheRepository_ButNotOutsideIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "alta-read-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(other);
        try
        {
            var guard = new AgentReadGuard([repo]);
            Assert.IsNull(guard.Check(Path.Combine(repo, "src", "a.cs")));
            Assert.IsNull(guard.Check(repo));
            Assert.IsNotNull(guard.Check(Path.Combine(other, "a.txt")));
            Assert.IsNotNull(guard.Check(Path.Combine(repo, "..", "other", "a.txt")));
            Assert.IsNotNull(guard.Check(repo + "-sibling"));

            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(repo, "link"), other);
                Assert.IsNotNull(guard.Check(Path.Combine(repo, "link", "a.txt")));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

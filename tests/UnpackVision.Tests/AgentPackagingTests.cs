namespace UnpackVision.Tests;

public sealed class AgentPackagingTests
{
    [Fact]
    public void PublishScript_ShipsSelfContainedAgentAndItsLicenses()
    {
        var script = ReadTestData("publish.ps1");

        Assert.Contains("src\\UnpackVision.Agent\\UnpackVision.Agent.csproj", script, StringComparison.Ordinal);
        Assert.Contains("--self-contained true", script, StringComparison.Ordinal);
        Assert.Contains("'Agent'", script, StringComparison.Ordinal);
        Assert.Contains("LICENSE-Apache-2.0.txt", script, StringComparison.Ordinal);
        Assert.Contains("LICENSE-MIT.txt", script, StringComparison.Ordinal);
        Assert.Contains("THIRD_PARTY_NOTICES.md", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentProject_PinsOfficialSdkAndCommandLineVersions()
    {
        var project = ReadTestData("UnpackVision.Agent.csproj");

        Assert.Contains("ModelContextProtocol\" Version=\"2.4.0\"", project, StringComparison.Ordinal);
        Assert.Contains("System.CommandLine\" Version=\"2.0.15\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Roslyn", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThirdPartyNotices_UseRestoredPackageLicenseMetadata()
    {
        var notices = ReadTestData("THIRD_PARTY_NOTICES.md");

        Assert.Contains("ModelContextProtocol | 2.4.0 | Apache-2.0", notices, StringComparison.Ordinal);
        Assert.Contains("ModelContextProtocol.Core | 2.4.0 | Apache-2.0", notices, StringComparison.Ordinal);
        Assert.Contains("System.CommandLine | 2.0.15 | MIT", notices, StringComparison.Ordinal);
        Assert.Contains("restored package `.nuspec` metadata", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentDocs_DistinguishSourceBranchFromPublished263AndDescribeScope()
    {
        var chinese = ReadTestData("agent-README.md");
        var english = ReadTestData("agent-README.en.md");

        Assert.Contains("v2.6.3", chinese, StringComparison.Ordinal);
        Assert.Contains("尚未包含", chinese, StringComparison.Ordinal);
        Assert.Contains("stdio", chinese, StringComparison.Ordinal);
        Assert.Contains("不开放远端", chinese, StringComparison.Ordinal);
        Assert.Contains("入队不等于已同步", chinese, StringComparison.Ordinal);

        Assert.Contains("v2.6.3", english, StringComparison.Ordinal);
        Assert.Contains("does **not** include", english, StringComparison.Ordinal);
        Assert.Contains("stdio", english, StringComparison.Ordinal);
        Assert.Contains("No remote HTTP listener", english, StringComparison.Ordinal);
        Assert.Contains("Queued does not mean synced", english, StringComparison.Ordinal);
    }

    private static string ReadTestData(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", fileName));
}

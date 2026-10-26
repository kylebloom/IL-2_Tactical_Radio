using Il2Radio.Core;

namespace Il2Radio.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.2.0+e9d4624a1b2c3d4e5f", "0.2.0 (e9d4624)")]
    [InlineData("0.2.0+abc", "0.2.0 (abc)")]
    [InlineData("0.2.0", "0.2.0")]
    [InlineData(null, "unknown")]
    public void DescribeShortensTheCommit(string? informational, string expected) =>
        Assert.Equal(expected, AppVersion.Describe(informational));
}

namespace FileMover.Tests;

public class PathHelpersTests
{
    private static EndpointSettings Ssh(string path) => new() { Type = EndpointType.SSH, Path = path };
    private static EndpointSettings Local(string path) => new() { Type = EndpointType.Local, Path = path };

    [Theory]
    [InlineData("/data", "a.txt", "/data/a.txt")]
    [InlineData("/data/", "a.txt", "/data/a.txt")]
    [InlineData("/", "a.txt", "/a.txt")]
    public void BuildDestPath_Ssh_JoinsWithSlash(string root, string name, string expected) =>
        Assert.Equal(expected, PathHelpers.BuildDestPath(Ssh(root), name));

    [Fact]
    public void BuildDestPath_Local_CombinesUnderRoot()
    {
        using var tmp = new TempDir();
        Assert.Equal(tmp.Combine("a.txt"), PathHelpers.BuildDestPath(Local(tmp.Path), "a.txt"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("../evil")]
    [InlineData("sub/evil")]
    [InlineData("sub\\evil")]
    [InlineData("C:evil")]
    public void BuildDestPath_RejectsUnsafeNames(string name)
    {
        Assert.Throws<InvalidDataException>(() => PathHelpers.BuildDestPath(Ssh("/data"), name));
        Assert.Throws<InvalidDataException>(() => PathHelpers.BuildDestPath(Local(Path.GetTempPath()), name));
    }

    [Theory]
    [InlineData("a/b/c.txt", "/dst/a/b/c.txt")]
    [InlineData("a\\b\\c.txt", "/dst/a/b/c.txt")]
    [InlineData("a//b.txt", "/dst/a/b.txt")]
    public void BuildDestRelPath_Ssh_NormalisesSeparators(string rel, string expected) =>
        Assert.Equal(expected, PathHelpers.BuildDestRelPath(Ssh("/dst/"), rel));

    [Fact]
    public void BuildDestRelPath_Local_KeepsStructure()
    {
        using var tmp = new TempDir();
        Assert.Equal(tmp.Combine("a", "b", "c.txt"), PathHelpers.BuildDestRelPath(Local(tmp.Path), "a/b/c.txt"));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("")]
    [InlineData("/")]
    public void BuildDestRelPath_RejectsTraversal(string rel)
    {
        Assert.Throws<InvalidDataException>(() => PathHelpers.BuildDestRelPath(Ssh("/dst"), rel));
        Assert.Throws<InvalidDataException>(() => PathHelpers.BuildDestRelPath(Local(Path.GetTempPath()), rel));
    }

    [Fact]
    public void CombineWithin_ThrowsWhenEscapingRoot()
    {
        using var tmp = new TempDir();
        Assert.Throws<InvalidDataException>(() => PathHelpers.CombineWithin(tmp.Path, "..\\x.txt"));
        Assert.Throws<InvalidDataException>(() => PathHelpers.CombineWithin(tmp.Path, "C:\\Windows\\x.txt"));
    }

    [Fact]
    public void CombineWithin_RejectsSiblingWithSharedPrefix()
    {
        using var tmp = new TempDir();
        var root = tmp.Combine("dest");
        Assert.Throws<InvalidDataException>(() => PathHelpers.CombineWithin(root, "..\\dest-evil\\x.txt"));
    }

    [Theory]
    [InlineData("/a/b/c.txt", "/a/b")]
    [InlineData("/c.txt", "/")]
    [InlineData("c.txt", "/")]
    public void SftpDir_ReturnsParent(string path, string expected) =>
        Assert.Equal(expected, PathHelpers.SftpDir(path));

    [Theory]
    [InlineData("/a/b/c.txt", "c.txt")]
    [InlineData("/a/b/", "b")]
    [InlineData("c.txt", "c.txt")]
    public void SftpFileName_ReturnsLastSegment(string path, string expected) =>
        Assert.Equal(expected, PathHelpers.SftpFileName(path));

    [Fact]
    public void FriendlyName_UsesTypeSpecificRules()
    {
        Assert.Equal("c.txt", PathHelpers.FriendlyName("/a/c.txt", EndpointType.SSH));
        Assert.Equal("c.txt", PathHelpers.FriendlyName(@"C:\a\c.txt", EndpointType.Local));
    }

    [Theory]
    [InlineData("/data", "'/data'")]
    [InlineData("it's", "'it'\\''s'")]
    [InlineData("$(rm -rf /)", "'$(rm -rf /)'")]
    [InlineData("a;b`c`", "'a;b`c`'")]
    public void ShellQuote_ProducesSafeSingleQuotedString(string input, string expected) =>
        Assert.Equal(expected, PathHelpers.ShellQuote(input));
}

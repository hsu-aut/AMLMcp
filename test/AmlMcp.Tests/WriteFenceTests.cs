using Xunit;

namespace AmlMcp.Tests;

/// <summary>
/// Writing has its own fence. Being allowed to read a directory is not permission to write
/// into it, and without the option nothing may be written anywhere.
/// </summary>
public sealed class WriteFenceTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private string Inside(string name) => Path.Combine(_ws.Dir, name);

    [Fact]
    public void Without_the_option_nothing_may_be_written()
    {
        var options = ServerOptions.Parse(new[] { "--root", _ws.Dir }, _ => null);

        Assert.Empty(options.WriteRoots);
        Assert.False(options.AllowsWrite(Inside("new.aml")));
        Assert.Equal("none", options.WriteRootsDescription);
    }

    [Fact]
    public void A_write_directory_allows_what_is_below_it()
    {
        var options = ServerOptions.Parse(new[] { "--write", _ws.Dir }, _ => null);

        Assert.True(options.AllowsWrite(Inside("new.aml")));
        Assert.True(options.AllowsWrite(Path.Combine(_ws.Dir, "sub", "new.aml")));
        Assert.True(options.AllowsWrite(_ws.Dir));
        Assert.Contains(_ws.Dir, options.WriteRootsDescription);
    }

    [Fact]
    public void A_read_directory_is_not_a_write_directory()
    {
        var options = ServerOptions.Parse(new[] { "--root", _ws.Dir }, _ => null);

        Assert.True(options.Allows(Inside("plant.aml")));
        Assert.False(options.AllowsWrite(Inside("plant.aml")));
    }

    [Fact]
    public void A_write_directory_outside_the_read_fence_still_allows_writing()
    {
        // The two fences are independent: the server may be allowed to write where it may
        // not read, and the caller finds out when it tries to read the file back.
        using var other = new Workspace();
        var options = ServerOptions.Parse(new[] { "--root", _ws.Dir, "--write", other.Dir }, _ => null);

        Assert.True(options.AllowsWrite(Path.Combine(other.Dir, "new.aml")));
        Assert.False(options.AllowsWrite(Inside("new.aml")));
    }

    [Fact]
    public void A_write_directory_is_repeatable_and_comes_from_the_environment()
    {
        using var other = new Workspace();
        var both = ServerOptions.Parse(new[] { "--write", _ws.Dir, "--write", other.Dir }, _ => null);

        Assert.Equal(2, both.WriteRoots.Count);
        Assert.True(both.AllowsWrite(Inside("a.aml")));
        Assert.True(both.AllowsWrite(Path.Combine(other.Dir, "b.aml")));

        var fromEnvironment = ServerOptions.Parse(Array.Empty<string>(),
            name => name == "AML_MCP_WRITE" ? string.Join(Path.PathSeparator, _ws.Dir, other.Dir) : null);

        Assert.Equal(2, fromEnvironment.WriteRoots.Count);
        Assert.True(fromEnvironment.AllowsWrite(Inside("a.aml")));
    }

    [Fact]
    public void A_drive_root_may_be_a_write_directory()
    {
        var drive = Path.GetPathRoot(Path.GetTempPath())!;
        var options = ServerOptions.Parse(new[] { "--write", drive }, _ => null);

        Assert.True(options.AllowsWrite(Path.Combine(Path.GetTempPath(), "new.aml")));
        Assert.True(options.AllowsWrite(drive));
    }

    [Fact]
    public void A_path_that_leaves_the_directory_with_dots_is_refused()
    {
        var options = ServerOptions.Parse(new[] { "--write", Path.Combine(_ws.Dir, "models") }, _ => null);

        Assert.True(options.AllowsWrite(Path.Combine(_ws.Dir, "models", "new.aml")));
        Assert.False(options.AllowsWrite(Path.Combine(_ws.Dir, "models", "..", "escaped.aml")));
        Assert.False(options.AllowsWrite(Path.Combine(_ws.Dir, "models-next-door", "new.aml")));
    }

    /// <summary>
    /// A link inside the directory that points out of it must not widen the fence. The
    /// resolver is handed in because this machine may not create symbolic links, and the
    /// rule to prove is the comparison, not the operating system call behind it.
    /// </summary>
    [Fact]
    public void A_path_whose_resolved_form_leaves_the_directory_is_refused()
    {
        using var other = new Workspace();
        var link = Path.Combine(_ws.Dir, "link.aml");
        var options = ServerOptions.Parse(new[] { "--write", _ws.Dir }, _ => null);

        Assert.True(options.AllowsWrite(link, _ => null));
        Assert.False(options.AllowsWrite(link, p => p == link ? Path.Combine(other.Dir, "real.aml") : null));
    }

    [Fact]
    public void The_option_without_a_directory_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--write" }, _ => null));

        Assert.Equal("--write needs a directory.", ex.Message);
    }

    [Fact]
    public void The_option_is_refused_when_another_option_follows_it()
    {
        Assert.Throws<ArgumentException>(() => ServerOptions.Parse(new[] { "--write", "--root", "." }, _ => null));
    }
}

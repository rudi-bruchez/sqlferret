// tests/SqlFerret.Core.Tests/RedactionPolicyTests.cs
using SqlFerret.Core.Parameters;
using Xunit;

public class RedactionPolicyTests
{
    [Fact]
    public void Full_keeps_value()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Full).Apply("@id", "42");
        Assert.Equal("42", v);
        Assert.False(redacted);
    }

    [Fact]
    public void Hash_replaces_with_fingerprint()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Hash).Apply("@id", "42");
        Assert.NotEqual("42", v);
        Assert.True(redacted);
        Assert.Equal(64, v.Length); // sha256 hex
    }

    [Fact]
    public void Sensitive_name_forces_hash_even_in_full_mode()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Full).Apply("@Password", "hunter2");
        Assert.NotEqual("hunter2", v);
        Assert.True(redacted);
    }

    [Fact]
    public void Masked_hides_content_keeps_shape()
    {
        var (v, redacted) = new RedactionPolicy(RedactionMode.Masked).Apply("@name", "Alice");
        Assert.DoesNotContain("Alice", v);
        Assert.True(redacted);
    }
}

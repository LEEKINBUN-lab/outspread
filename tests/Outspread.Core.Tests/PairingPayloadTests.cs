namespace Outspread.Core.Tests;

/// <summary>
/// The payload is the contract with an installed phone: six keys, and nothing that varies with
/// which board happened to be open when the code was drawn.
/// </summary>
public class PairingPayloadTests
{
    private static SyncConfig Config() => new() { Owner = "someone", Repo = "boards-private", Branch = "main" };

    [Fact]
    public void The_payload_carries_the_six_keys_and_nothing_else()
    {
        var parsed = PairingPayload.Parse(PairingPayload.Build(Config(), "tok"));

        Assert.NotNull(parsed);
        Assert.Equal(
            new[] { "branch", "host", "owner", "repo", "token", "v" },
            parsed!.Keys.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("1", parsed["v"]);
        Assert.Equal("github", parsed["host"]);
    }

    [Fact]
    public void The_pairing_code_names_the_repository_not_the_board()
    {
        // There is no "--board" in a pairing code, and no version of this design should add one:
        // which boards exist is read from the folder on the far side. Pinned because a claimed
        // divergence between the terminal and the window was measured to be false — the board
        // path only feeds a line of human-readable text.
        var text = PairingPayload.Build(Config(), "tok");

        Assert.DoesNotContain("scratch", text, StringComparison.Ordinal);
        Assert.DoesNotContain("boards/", text, StringComparison.Ordinal);
        Assert.Equal(text, PairingPayload.Build(Config(), "tok"));
    }

    [Fact]
    public void A_code_is_not_built_out_of_a_missing_pointer_or_a_missing_token()
    {
        Assert.Throws<SyncSetupException>(() => PairingPayload.Build(new SyncConfig(), "tok"));
        Assert.Throws<SyncSetupException>(() => PairingPayload.Build(Config(), null));
        Assert.Throws<SyncSetupException>(() => PairingPayload.Build(Config(), "   "));
    }

    [Fact]
    public void A_payload_missing_a_key_does_not_parse()
    {
        Assert.Null(PairingPayload.Parse("""{"v":"1","host":"github","owner":"a","repo":"b","branch":"main"}"""));
        Assert.Null(PairingPayload.Parse("not json"));
    }
}

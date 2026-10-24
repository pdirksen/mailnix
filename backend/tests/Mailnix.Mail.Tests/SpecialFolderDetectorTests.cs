using Candidate = Mailnix.Mail.SpecialFolderDetector.Candidate;

namespace Mailnix.Mail.Tests;

public sealed class SpecialFolderDetectorTests
{
    [Fact]
    public void Server_flags_win_over_names()
    {
        // Seen on a real Dovecot server: both INBOX.Junk and INBOX.Spam exist, Spam carries \Junk.
        var map = SpecialFolderDetector.Detect(
        [
            new("INBOX.Junk", "Junk", true, null),
            new("INBOX.Spam", "Spam", true, SpecialFolderKind.Junk),
        ]);

        map.Junk.ShouldBe("INBOX.Spam");
    }

    [Fact]
    public void Shallowest_folder_wins_among_name_matches()
    {
        var map = SpecialFolderDetector.Detect(
        [
            new("INBOX.Old.Trash", "Trash", true, null),
            new("INBOX.Trash", "Trash", true, null),
        ]);

        map.Trash.ShouldBe("INBOX.Trash");
    }

    [Fact]
    public void Earlier_names_in_the_list_are_preferred_at_the_same_depth()
    {
        var map = SpecialFolderDetector.Detect(
        [
            new("Bin", "Bin", true, null),
            new("Trash", "Trash", true, null),
        ]);

        map.Trash.ShouldBe("Trash");
    }

    [Theory]
    [InlineData("Gelöschte Elemente")]
    [InlineData("deleted items")]
    [InlineData("Papierkorb")]
    public void Recognizes_common_trash_names(string name)
    {
        SpecialFolderDetector.Detect([new(name, name, true, null)]).Trash.ShouldBe(name);
    }

    [Fact]
    public void Ignores_folders_that_cannot_be_selected()
    {
        SpecialFolderDetector.Detect([new("Trash", "Trash", false, SpecialFolderKind.Trash)]).Trash.ShouldBeNull();
    }

    [Fact]
    public void Missing_folders_stay_null()
    {
        SpecialFolderDetector.Detect([new("INBOX", "INBOX", true, null)]).ShouldBe(SpecialFolderMap.Empty);
    }
}

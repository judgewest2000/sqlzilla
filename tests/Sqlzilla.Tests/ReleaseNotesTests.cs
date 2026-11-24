using Sqlzilla.Services;

namespace Sqlzilla.Tests;

public class ReleaseNotesTests
{
    [Fact]
    public void ParsesVersionsDatesAndNestedBullets()
    {
        var notes = ReleaseNotes.Parse("# Release notes\r\n\r\n## 1.1.0 (2026-11-18)\r\n- **Big** thing\r\n  - detail\r\n\r\n## 1.0.0\n- First\n");

        Assert.Equal(2, notes.Count);
        Assert.Equal(("1.1.0", "2026-11-18"), (notes[0].Version, notes[0].Date));
        Assert.Equal([0, 1], notes[0].Items.Select(i => i.Level));
        Assert.Equal(("1.0.0", (string?)null), (notes[1].Version, notes[1].Date));
        Assert.Equal("First", Assert.Single(Assert.Single(notes[1].Items).Spans).Text);
    }

    [Fact]
    public void SplitsInlineFormatting()
    {
        var spans = ReleaseNotes.Inline("Use **Open with**, `-Flag` or *Close* now");

        Assert.Equal(
            [("Use ", NoteStyle.Plain), ("Open with", NoteStyle.Bold), (", ", NoteStyle.Plain), ("-Flag", NoteStyle.Code),
             (" or ", NoteStyle.Plain), ("Close", NoteStyle.Italic), (" now", NoteStyle.Plain)],
            spans.Select(s => (s.Text, s.Style)));
    }
}

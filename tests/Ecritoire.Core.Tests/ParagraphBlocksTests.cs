using Ecritoire.Core;
using Xunit;

namespace Ecritoire.Core.Tests;

public class ParagraphBlocksTests
{
    [Fact]
    public void MovesLastParagraphToTop()
    {
        var r = ParagraphBlocks.Move("A\n\nB\n\nC\n", 4, 0);
        Assert.Equal(("C\n\nA\n\nB\n", 0), r);
    }

    [Fact]
    public void MovesFirstParagraphToEnd()
    {
        var r = ParagraphBlocks.Move("A\n\nB\n\nC", 0, 5);
        Assert.Equal(("B\n\nC\n\nA", 4), r);
    }

    [Fact]
    public void MovesDownBetweenParagraphs()
    {
        var r = ParagraphBlocks.Move("A\n\nB\n\nC", 0, 4);
        Assert.Equal(("B\n\nA\n\nC", 2), r);
    }

    [Fact]
    public void SamePlaceIsNoMove()
    {
        Assert.Null(ParagraphBlocks.Move("A\n\nB\n\nC", 2, 2));
        Assert.Null(ParagraphBlocks.Move("A\n\nB\n\nC", 2, 4));
        Assert.Null(ParagraphBlocks.Move("A\n\nB\n\nC", 4, 5));
    }

    [Fact]
    public void MovesIntroCalloutAsOneBlock()
    {
        var r = ParagraphBlocks.Move("# T\n\nCorps\n\n> [!intro]\n> Chapeau", 5, 2);
        Assert.Equal(("# T\n\n> [!intro]\n> Chapeau\n\nCorps", 2), r);
    }

    [Fact]
    public void ListItemsStayTight()
    {
        var r = ParagraphBlocks.Move("- a\n- b\n- c", 2, 0);
        Assert.Equal(("- c\n- a\n- b", 0), r);
    }

    [Fact]
    public void DuplicatesAndDeletes()
    {
        Assert.Equal(("A\n\nA\n\nB", 2), ParagraphBlocks.Duplicate("A\n\nB", 0));
        Assert.Equal(("A\n\nB\n\nB", 4), ParagraphBlocks.Duplicate("A\n\nB", 2));
        Assert.Equal("B", ParagraphBlocks.Delete("A\n\nB", 0).Text);
        Assert.Equal("A", ParagraphBlocks.Delete("A\n\nB", 2).Text);
    }

    [Fact]
    public void TransformsPrefix()
    {
        Assert.Equal("## Titre", ParagraphBlocks.Transform("# Titre", "## "));
        Assert.Equal("Texte", ParagraphBlocks.Transform("> Texte", ""));
        Assert.Equal("- Texte", ParagraphBlocks.Transform("Texte", "- "));
    }
}

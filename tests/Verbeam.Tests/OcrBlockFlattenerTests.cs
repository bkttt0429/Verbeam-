using Verbeam.Core.Models;
using Verbeam.Core.Services;

namespace Verbeam.Tests;

public sealed class OcrBlockFlattenerTests
{
    private static OcrBoundingBox Box(int x) => new(x, x, 10, 10);

    [Fact]
    public void FlatBlocks_ReturnedAsIs()
    {
        var page = new OcrPageResult
        {
            Blocks =
            [
                new OcrBlock { Id = "b1", Text = "a", BoundingBox = Box(1) },
                new OcrBlock { Id = "b2", Text = "b", BoundingBox = Box(2) }
            ]
        };

        var ids = OcrBlockFlattener.Flatten(page).Select(b => b.Id).ToArray();

        Assert.Equal(["b1", "b2"], ids);
    }

    [Fact]
    public void ContainerWithChildren_EmitsLeavesNotParent()
    {
        var page = new OcrPageResult
        {
            Blocks =
            [
                new OcrBlock
                {
                    Id = "p1",
                    Text = "parent",
                    BoundingBox = Box(1),
                    Children =
                    [
                        new OcrBlock { Id = "c1", Text = "child1", BoundingBox = Box(2) },
                        new OcrBlock { Id = "c2", Text = "child2", BoundingBox = Box(3) }
                    ]
                }
            ]
        };

        var ids = OcrBlockFlattener.Flatten(page).Select(b => b.Id).ToArray();

        Assert.Equal(["c1", "c2"], ids); // parent container itself is not emitted
    }

    [Fact]
    public void Table_EmitsCellsKeyedByBlockAndCell()
    {
        var page = new OcrPageResult
        {
            Blocks =
            [
                new OcrBlock
                {
                    Id = "t1",
                    Type = "table",
                    Engine = "ppstructure",
                    Table = new OcrTableBlock
                    {
                        RowCount = 1,
                        ColumnCount = 2,
                        Cells =
                        [
                            new OcrTableCell { Id = "cellA", Text = "x", BoundingBox = Box(1) },
                            new OcrTableCell { RowIndex = 0, ColumnIndex = 1, Text = "y", BoundingBox = Box(2) },
                            new OcrTableCell { Id = "empty", Text = "  ", BoundingBox = Box(3) }, // skipped: blank
                            new OcrTableCell { Id = "nobox", Text = "z", BoundingBox = null }       // skipped: no bbox
                        ]
                    }
                }
            ]
        };

        var units = OcrBlockFlattener.Flatten(page).ToArray();

        Assert.Equal(["t1#cellA", "t1#r0c1"], units.Select(u => u.Id).ToArray());
        Assert.All(units, u => Assert.Equal("table-cell", u.Type));
        Assert.All(units, u => Assert.NotNull(u.BoundingBox));
    }

    [Fact]
    public void NestedTableInChildBlocks_Flattens()
    {
        var page = new OcrPageResult
        {
            Blocks =
            [
                new OcrBlock
                {
                    Id = "sec",
                    Children =
                    [
                        new OcrBlock { Id = "para", Text = "p", BoundingBox = Box(1) },
                        new OcrBlock
                        {
                            Id = "tbl",
                            Table = new OcrTableBlock
                            {
                                Cells = [new OcrTableCell { Id = "a", Text = "v", BoundingBox = Box(2) }]
                            }
                        }
                    ]
                }
            ]
        };

        var ids = OcrBlockFlattener.Flatten(page).Select(b => b.Id).ToArray();

        Assert.Equal(["para", "tbl#a"], ids);
    }
}

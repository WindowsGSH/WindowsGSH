using Xunit;

namespace WindowsGSH.Tests;

public sealed class StableItemCollectionTests
{
    [Fact]
    public void Update_ReusesRowsWhenValuesChange()
    {
        var collection = new StableItemCollection<string>();
        collection.Update(["one", "two"]);
        var first = collection.Items[0];
        var second = collection.Items[1];
        var collectionChanges = 0;
        collection.Items.CollectionChanged += (_, _) => collectionChanges++;

        collection.Update(["updated", "values"]);

        Assert.Same(first, collection.Items[0]);
        Assert.Same(second, collection.Items[1]);
        Assert.Equal("updated", first.Value);
        Assert.Equal("values", second.Value);
        Assert.Equal(0, collectionChanges);
    }

    [Fact]
    public void Update_OnlyAddsOrRemovesRowsWhenCountChanges()
    {
        var collection = new StableItemCollection<int>();
        collection.Update([1]);
        var first = collection.Items[0];

        collection.Update([2, 3]);

        Assert.Same(first, collection.Items[0]);
        Assert.Equal([2, 3], collection.Items.Select(item => item.Value));

        collection.Update([4]);

        Assert.Same(first, Assert.Single(collection.Items));
        Assert.Equal(4, first.Value);
    }
}

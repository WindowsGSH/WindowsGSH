using WindowsGSH.Core.Metrics;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class MetricHistoryBufferTests
{
    [Fact]
    public void Add_SingleItem_IsRetrievable()
    {
        var buffer = new MetricHistoryBuffer<int>(10);
        buffer.Add(42);

        Assert.Equal(1, buffer.Count);
        Assert.Equal([42], buffer.ToReadOnlyList());
    }

    [Fact]
    public void Add_UpToCapacity_AllItemsRetained()
    {
        var buffer = new MetricHistoryBuffer<int>(5);
        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(5, buffer.Count);
        Assert.Equal([1, 2, 3, 4, 5], buffer.ToReadOnlyList());
    }

    [Fact]
    public void Add_BeyondCapacity_OldestItemsDropped()
    {
        var buffer = new MetricHistoryBuffer<int>(3);
        for (var i = 1; i <= 6; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal([4, 5, 6], buffer.ToReadOnlyList());
    }

    [Fact]
    public void ToReadOnlyList_PreservesChronologicalOrder()
    {
        var buffer = new MetricHistoryBuffer<int>(4);
        buffer.Add(10);
        buffer.Add(20);
        buffer.Add(30);
        buffer.Add(40);
        buffer.Add(50); // wraps: drops 10

        Assert.Equal([20, 30, 40, 50], buffer.ToReadOnlyList());
    }

    [Fact]
    public void Add_SingleCapacityBuffer_AlwaysHoldsLatest()
    {
        var buffer = new MetricHistoryBuffer<int>(1);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Add(3);

        Assert.Equal(1, buffer.Count);
        Assert.Equal([3], buffer.ToReadOnlyList());
    }

    [Fact]
    public void Count_Zero_WhenEmpty()
    {
        var buffer = new MetricHistoryBuffer<int>(10);
        Assert.Equal(0, buffer.Count);
        Assert.Equal([], buffer.ToReadOnlyList());
    }

    [Fact]
    public void Clear_ResetsBuffer()
    {
        var buffer = new MetricHistoryBuffer<int>(5);
        buffer.Add(1);
        buffer.Add(2);
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.Equal([], buffer.ToReadOnlyList());
    }

    [Fact]
    public void Capacity_ReturnsConstructorValue()
    {
        var buffer = new MetricHistoryBuffer<string>(7);
        Assert.Equal(7, buffer.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_ThrowsOnNonPositiveCapacity(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MetricHistoryBuffer<int>(capacity));
    }

    [Fact]
    public void ToReadOnlyList_IsStableSnapshot_NotAffectedBySubsequentAdds()
    {
        var buffer = new MetricHistoryBuffer<int>(5);
        buffer.Add(1);
        buffer.Add(2);

        var snapshot = buffer.ToReadOnlyList();
        buffer.Add(3);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Add_WrapsMultipleTimes_RetainsLastCapacityItems()
    {
        var buffer = new MetricHistoryBuffer<int>(3);
        for (var i = 1; i <= 12; i++)
        {
            buffer.Add(i);
        }

        Assert.Equal(3, buffer.Count);
        Assert.Equal([10, 11, 12], buffer.ToReadOnlyList());
    }
}

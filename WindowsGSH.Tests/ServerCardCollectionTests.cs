using WindowsGSH.Core.Servers;
using Xunit;

namespace WindowsGSH.Tests;

public sealed class ServerCardCollectionTests
{
    [Fact]
    public void Update_ReusesCardsAndPublishesNewServerValues()
    {
        var collection = new ServerCardCollection();
        collection.Update([Server("1", "First", "1%")]);
        var card = Assert.Single(collection.Items);
        var notifications = new List<string?>();
        var collectionChanges = 0;
        card.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        collection.Items.CollectionChanged += (_, _) => collectionChanges++;

        var refreshed = Server("1", "Renamed", "25%");
        collection.Update([refreshed]);

        Assert.Same(card, Assert.Single(collection.Items));
        Assert.Same(refreshed, card.Server);
        Assert.Equal("Renamed", card.Name);
        Assert.Equal("25%", card.CpuUsage);
        Assert.Contains(string.Empty, notifications);
        Assert.Equal(0, collectionChanges);
    }

    [Fact]
    public void Update_OnlyChangesCollectionWhenServerMembershipOrOrderChanges()
    {
        var collection = new ServerCardCollection();
        collection.Update([Server("1", "One", "1%"), Server("2", "Two", "2%")]);
        var first = collection.Items[0];
        var second = collection.Items[1];

        collection.Update([Server("2", "Two updated", "3%"), Server("3", "Three", "4%")]);

        Assert.Equal(2, collection.Items.Count);
        Assert.Same(second, collection.Items[0]);
        Assert.Equal("Two updated", collection.Items[0].Name);
        Assert.Equal("3", collection.Items[1].Id);
        Assert.DoesNotContain(first, collection.Items);
    }

    private static InstalledServer Server(string id, string name, string cpuUsage)
    {
        return new InstalledServer(
            id,
            name,
            "module",
            "native",
            $"server-{id}",
            $"server-{id}/files",
            $"server-{id}/ServerConfig.json",
            "127.0.0.1",
            "27015",
            "1",
            "public",
            "10",
            "123",
            cpuUsage,
            "100 MB",
            "0 / 10",
            "Online",
            "1m",
            false,
            "",
            null,
            true,
            ServerRuntimeStatus.Running,
            "Running",
            "GoodBrush",
            false,
            "1",
            "1",
            "",
            true,
            true,
            false,
            true);
    }
}

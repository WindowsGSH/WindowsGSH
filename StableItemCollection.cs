using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WindowsGSH;

internal sealed class StableItemCollection<T>
{
    public ObservableCollection<StableItem<T>> Items { get; } = [];

    public void Update(IReadOnlyList<T> values)
    {
        var sharedCount = Math.Min(Items.Count, values.Count);
        for (var index = 0; index < sharedCount; index++)
        {
            Items[index].Update(values[index]);
        }

        for (var index = Items.Count; index < values.Count; index++)
        {
            Items.Add(new StableItem<T>(values[index]));
        }

        while (Items.Count > values.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }
}

internal sealed class StableItem<T> : INotifyPropertyChanged
{
    public StableItem(T value)
    {
        Value = value;
    }

    public T Value { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(T value)
    {
        if (EqualityComparer<T>.Default.Equals(Value, value))
        {
            return;
        }

        Value = value;
        OnPropertyChanged(nameof(Value));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

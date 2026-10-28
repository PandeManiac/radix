namespace Radix.Core.Simulation;

internal enum SimEventKind : byte
{
    WakeComponent = 0,
    CommitOutput = 1,
    PrimitiveTimer = 2,
}

internal readonly record struct SimEvent(long Time, long Sequence, SimEventKind Kind, int Target, int Value);

/// <summary>
/// Binary min-heap ordered by (time, insertion sequence). Insertion order is the
/// tie-breaker that makes simultaneous events deterministic.
/// </summary>
internal sealed class SimEventHeap
{
    private SimEvent[] _items = new SimEvent[256];
    private int _count;

    public int Count => _count;

    public void Clear() => _count = 0;

    public void Push(SimEvent value)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        int index = _count++;
        _items[index] = value;
        SiftUp(index);
    }

    public SimEvent Pop()
    {
        if (_count == 0)
        {
            throw new InvalidOperationException("Event queue is empty.");
        }

        SimEvent result = _items[0];
        _count--;
        if (_count > 0)
        {
            _items[0] = _items[_count];
            SiftDown(0);
        }

        return result;
    }

    public bool TryPeek(out SimEvent value)
    {
        if (_count == 0)
        {
            value = default;
            return false;
        }

        value = _items[0];
        return true;
    }

    private static bool Before(SimEvent left, SimEvent right) =>
        left.Time < right.Time || (left.Time == right.Time && left.Sequence < right.Sequence);

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!Before(_items[index], _items[parent]))
            {
                break;
            }

            (_items[parent], _items[index]) = (_items[index], _items[parent]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            int left = (index * 2) + 1;
            if (left >= _count)
            {
                return;
            }

            int right = left + 1;
            int smallest = right < _count && Before(_items[right], _items[left]) ? right : left;
            if (!Before(_items[smallest], _items[index]))
            {
                return;
            }

            (_items[index], _items[smallest]) = (_items[smallest], _items[index]);
            index = smallest;
        }
    }
}

using System.Collections;
using Radix.Core.Compilation;

namespace Radix.Core.Simulation;

/// <summary>
/// Bounded ring buffer of net transitions. Old entries are overwritten when the
/// capacity is reached; <see cref="TotalRecorded"/> still reports the true count.
/// </summary>
public sealed class SimulationTrace : IReadOnlyList<NetChange>
{
    private readonly NetChange[] _buffer;
    private int _start;
    private int _count;

    public SimulationTrace(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _buffer = capacity == 0 ? [] : new NetChange[capacity];
    }

    public int Capacity => _buffer.Length;

    public int Count => _count;

    public long TotalRecorded { get; private set; }

    public bool IsTruncated => TotalRecorded > _count;

    public NetChange this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _buffer[(_start + index) % _buffer.Length];
        }
    }

    internal void Add(NetChange change)
    {
        TotalRecorded++;
        if (_buffer.Length == 0)
        {
            return;
        }

        if (_count < _buffer.Length)
        {
            _buffer[(_start + _count) % _buffer.Length] = change;
            _count++;
        }
        else
        {
            _buffer[_start] = change;
            _start = (_start + 1) % _buffer.Length;
        }
    }

    internal void Clear()
    {
        _start = 0;
        _count = 0;
        TotalRecorded = 0;
        if (_buffer.Length > 0)
        {
            Array.Clear(_buffer);
        }
    }

    public IEnumerator<NetChange> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

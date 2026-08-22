using System.Globalization;
using System.Text;

namespace Miller.Testing;

/// <summary>
/// The capture buffer for ONE of a child process's output streams, bounded in characters.
///
/// <para>The drain loop used to append every chunk to an uncapped <see cref="StringBuilder"/>. Nothing bounded
/// how much a child could write: the stall guard bounds SILENCE, which a chatty process never trips, and the
/// only total bound is the 30-minute provider window. A test that logs at 10MB/s therefore grew about 18GB of
/// UTF-16 text inside the CT daemon, and taking a snapshot of it doubled the peak again.</para>
///
/// <para>What it keeps is a HEAD plus a rolling TAIL, joined by one marker naming the elided characters. Both
/// halves are load-bearing: a provider's failure summary reads the FIRST line of the text, and the failure
/// output and summary lines a reader needs are at the END. A run that fits inside the cap is returned exactly
/// as it arrived - same characters, same order, no marker - however the pipe chunked it.</para>
///
/// <para>Truncation is REPORTED rather than hidden, because both stdout result parsers tolerate lines they do
/// not recognise: the xunit path skips an unparseable JSONL line and the cargo path ignores any line matching
/// no pattern. An elided middle would silently drop test cases and could turn a red run green, so the parsers
/// refuse a truncated stream instead of reading it (see
/// <see cref="TestProcessResult.RequireCompleteStandardOutput"/>).</para>
/// </summary>
internal sealed class BoundedOutputBuffer
{
    /// <summary>
    /// The share of the cap reserved for the head. The tail gets the rest: a failure summary needs one line
    /// from the front and everything it can get from the back.
    /// </summary>
    private const int HeadDivisor = 4;

    private readonly object _gate = new();
    private readonly StringBuilder _head = new();
    private readonly int _headCapacity;
    private readonly char[]? _tail;
    private int _tailStart;
    private int _tailLength;
    private long _elided;

    /// <param name="maxCharacters">
    /// The most characters this buffer retains. Zero or a negative value disables the bound and restores the
    /// unbounded capture, which is the escape hatch for an operator who would rather risk the memory.
    /// </param>
    public BoundedOutputBuffer(int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            _headCapacity = int.MaxValue;
            return;
        }

        _headCapacity = Math.Max(1, maxCharacters / HeadDivisor);
        _tail = new char[maxCharacters - _headCapacity];
    }

    /// <summary>True once the buffer has dropped characters, so a result parser must refuse this stream.</summary>
    public bool Truncated
    {
        get
        {
            lock (_gate)
                return _elided > 0;
        }
    }

    public void Append(char[] chunk, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (count <= 0)
            return;

        lock (_gate)
        {
            var data = chunk.AsSpan(offset, count);
            if (_head.Length < _headCapacity)
            {
                int toHead = Math.Min(_headCapacity - _head.Length, data.Length);
                _head.Append(data[..toHead]);
                data = data[toHead..];
            }

            if (data.Length > 0 && _tail is not null)
                AppendTail(data);
        }
    }

    /// <summary>
    /// The retained text: head, then the elision marker when anything was dropped, then the tail. Allocates
    /// one string of at most the cap plus the marker, so the snapshot cannot be the thing that exhausts memory.
    /// </summary>
    public string Snapshot()
    {
        lock (_gate)
        {
            if (_tail is null || (_tailLength == 0 && _elided == 0))
                return _head.ToString();

            var builder = new StringBuilder(_head.Length + _tailLength + 48);
            builder.Append(_head);
            if (_elided > 0)
            {
                builder.Append('\n')
                    .Append("[... ")
                    .Append(_elided.ToString(CultureInfo.InvariantCulture))
                    .Append(" characters elided ...]")
                    .Append('\n');
            }

            // The ring may wrap, so the tail is written in at most two runs, oldest first.
            int firstRun = Math.Min(_tailLength, _tail.Length - _tailStart);
            builder.Append(_tail.AsSpan(_tailStart, firstRun));
            if (firstRun < _tailLength)
                builder.Append(_tail.AsSpan(0, _tailLength - firstRun));

            return builder.ToString();
        }
    }

    /// <summary>Writes into the ring, counting whatever it pushes out. Caller holds the gate.</summary>
    private void AppendTail(ReadOnlySpan<char> data)
    {
        int capacity = _tail!.Length;
        if (data.Length >= capacity)
        {
            // One chunk larger than the whole ring: everything already held, plus the front of this chunk, is
            // gone. Copying the ring first would be wasted work.
            _elided += _tailLength + (data.Length - capacity);
            data[^capacity..].CopyTo(_tail);
            _tailStart = 0;
            _tailLength = capacity;
            return;
        }

        int overflow = _tailLength + data.Length - capacity;
        if (overflow > 0)
        {
            _elided += overflow;
            _tailStart = (_tailStart + overflow) % capacity;
            _tailLength -= overflow;
        }

        int writeAt = (_tailStart + _tailLength) % capacity;
        int firstRun = Math.Min(data.Length, capacity - writeAt);
        data[..firstRun].CopyTo(_tail.AsSpan(writeAt));
        if (firstRun < data.Length)
            data[firstRun..].CopyTo(_tail.AsSpan(0));

        _tailLength += data.Length;
    }
}

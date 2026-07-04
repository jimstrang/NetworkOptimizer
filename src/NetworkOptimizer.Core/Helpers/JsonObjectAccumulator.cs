using System.Text;

namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Streaming extractor for top-level JSON objects from a text stream that prints them back-to-back
/// (e.g. <c>iperf3 -s -J</c> emits one JSON object per completed test). Feed it lines as they
/// arrive; it brace-counts and invokes the callback once per complete top-level object. Shared by
/// the central iperf3 server (<c>Iperf3ServerService</c>) and the on-site agent (<c>Iperf3Runner</c>)
/// so both capture client-initiated results identically. Not thread-safe: feed from one reader.
/// </summary>
public sealed class JsonObjectAccumulator
{
    private readonly StringBuilder _buffer = new();
    private int _braceDepth;
    private bool _inJson;

    /// <summary>
    /// Feeds one line (without its trailing newline) and invokes <paramref name="onComplete"/> once
    /// for each complete top-level JSON object that closes within it.
    /// </summary>
    public void Feed(string line, Action<string> onComplete)
    {
        foreach (var ch in line)
        {
            if (ch == '{')
            {
                if (!_inJson) { _inJson = true; _buffer.Clear(); }
                _braceDepth++;
            }

            if (_inJson) _buffer.Append(ch);

            if (ch == '}' && _inJson)
            {
                _braceDepth--;
                if (_braceDepth == 0)
                {
                    var json = _buffer.ToString();
                    _buffer.Clear();
                    _inJson = false;
                    onComplete(json);
                }
            }
        }

        if (_inJson) _buffer.AppendLine();
    }
}

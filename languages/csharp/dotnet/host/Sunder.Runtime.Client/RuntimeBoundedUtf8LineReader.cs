using System.Text;

namespace Sunder.Runtime.Client;

internal sealed class RuntimeBoundedUtf8LineReader(Stream stream)
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private readonly byte[] _buffer = new byte[8192];
    private int _offset;
    private int _length;
    private bool _completed;

    public async ValueTask<string?> ReadLineAsync(int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (_completed && _offset == _length)
        {
            return null;
        }

        using var line = new MemoryStream(Math.Min(maximumBytes, 8192));
        var observed = false;
        while (true)
        {
            if (_offset == _length)
            {
                _offset = 0;
                _length = await stream.ReadAsync(_buffer, cancellationToken).ConfigureAwait(false);
                if (_length == 0)
                {
                    _completed = true;
                    if (!observed)
                    {
                        return null;
                    }
                    return Decode(line);
                }
            }

            var newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _length - _offset);
            var end = newline >= 0 ? newline : _length;
            var count = end - _offset;
            if (line.Length + count > maximumBytes)
            {
                throw new InvalidDataException(
                    $"Runtime stream line exceeded the {maximumBytes} byte client parser limit.");
            }
            if (count > 0)
            {
                line.Write(_buffer, _offset, count);
                observed = true;
            }
            _offset = newline >= 0 ? newline + 1 : end;
            if (newline >= 0)
            {
                return Decode(line);
            }
        }
    }

    private static string Decode(MemoryStream line)
    {
        var bytes = line.GetBuffer().AsSpan(0, checked((int)line.Length));
        if (!bytes.IsEmpty && bytes[^1] == (byte)'\r')
        {
            bytes = bytes[..^1];
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Runtime stream returned invalid UTF-8.", exception);
        }
    }
}

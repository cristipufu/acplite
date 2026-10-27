using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Acplite.Sdk.Runtime;

/// <summary>
/// Live byte-offset <c>.jsonl</c> transcript tailer using <see cref="FileSystemWatcher"/> and
/// <see cref="ArrayPool{T}.Shared"/> buffers without locking active agent session files.
/// </summary>
public static class TranscriptTailer
{
    public const int ChunkSize = 8192;

    /// <summary>
    /// Default tail window (512 KB) when reading existing history from large multi-day <c>.jsonl</c> transcripts.
    /// Pass <see langword="null"/> to read from byte 0 regardless of file size.
    /// </summary>
    public const long DefaultMaxInitialBytes = 512 * 1024;
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromMilliseconds(500);

    public static async IAsyncEnumerable<string> TailLinesAsync(
        string filePath,
        bool includeExistingLines = true,
        bool followLiveChanges = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        long? maxInitialBytes = DefaultMaxInitialBytes)
    {
        if (!File.Exists(filePath))
        {
            yield break;
        }

        long offset = 0;
        var remainder = new StringBuilder();

        if (!includeExistingLines)
        {
            try
            {
                offset = new FileInfo(filePath).Length;
            }
            catch (IOException)
            {
                yield break;
            }
        }
        else
        {
            await foreach (var line in StreamInitialLinesAsync(
                filePath,
                remainder,
                maxInitialBytes,
                newOffset => offset = newOffset,
                cancellationToken).ConfigureAwait(false))
            {
                yield return line;
            }
        }

        if (!followLiveChanges)
        {
            yield break;
        }

        var dir = Path.GetDirectoryName(filePath);
        var fileName = Path.GetFileName(filePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(fileName))
        {
            yield break;
        }

        var wakeChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        using var watcher = new FileSystemWatcher(dir, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        void SignalWake(object sender, FileSystemEventArgs e) => wakeChannel.Writer.TryWrite(true);
        watcher.Changed += SignalWake;
        watcher.Created += SignalWake;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!wakeChannel.Reader.TryRead(out _))
                {
                    var waitTask = wakeChannel.Reader.WaitToReadAsync(cancellationToken).AsTask();
                    var delayTask = Task.Delay(FallbackPollInterval, cancellationToken);
                    await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = wakeChannel.Reader.TryRead(out _);
                }

                long currentLen;
                try
                {
                    var fi = new FileInfo(filePath);
                    if (!fi.Exists)
                    {
                        continue;
                    }
                    currentLen = fi.Length;
                }
                catch (IOException)
                {
                    continue;
                }

                if (currentLen < offset)
                {
                    offset = 0;
                    remainder.Clear();
                }

                if (currentLen > offset)
                {
                    var newLines = await ReadAvailableLinesAsync(filePath, remainder, () => offset, newOffset => offset = newOffset, cancellationToken).ConfigureAwait(false);
                    foreach (var line in newLines)
                    {
                        yield return line;
                    }
                }
            }
        }
        finally
        {
            watcher.Changed -= SignalWake;
            watcher.Created -= SignalWake;
        }
    }

    private static async IAsyncEnumerable<string> StreamInitialLinesAsync(
        string filePath,
        StringBuilder remainder,
        long? maxInitialBytes,
        Action<long> setOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuffer = ArrayPool<char>.Shared.Rent(ChunkSize);
        var chunkLines = new List<string>(16);

        FileStream? stream = null;
        var offset = 0L;
        var skipUntilNewline = false;

        try
        {
            try
            {
                stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: ChunkSize,
                    useAsync: true);

                var length = stream.Length;
                if (maxInitialBytes is > 0 && length > maxInitialBytes.Value)
                {
                    offset = length - maxInitialBytes.Value;
                    stream.Seek(offset, SeekOrigin.Begin);
                    skipUntilNewline = true;
                }
            }
            catch (IOException)
            {
                yield break;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    break;
                }

                if (bytesRead <= 0)
                {
                    break;
                }

                offset += bytesRead;
                var decodeStart = 0;

                if (skipUntilNewline)
                {
                    var nlIdx = buffer.AsSpan(0, bytesRead).IndexOf((byte)'\n');
                    if (nlIdx < 0)
                    {
                        continue;
                    }

                    skipUntilNewline = false;
                    decodeStart = nlIdx + 1;
                    if (decodeStart >= bytesRead)
                    {
                        continue;
                    }
                }

                var charsDecoded = decoder.GetChars(buffer, decodeStart, bytesRead - decodeStart, charBuffer, 0, flush: false);
                for (var i = 0; i < charsDecoded; i++)
                {
                    var ch = charBuffer[i];
                    if (ch == '\n')
                    {
                        var completed = remainder.ToString().TrimEnd('\r');
                        remainder.Clear();
                        if (!string.IsNullOrWhiteSpace(completed))
                        {
                            chunkLines.Add(completed);
                        }
                    }
                    else
                    {
                        remainder.Append(ch);
                    }
                }

                if (chunkLines.Count > 0)
                {
                    foreach (var line in chunkLines)
                    {
                        yield return line;
                    }
                    chunkLines.Clear();
                }
            }

            setOffset(offset);
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            ArrayPool<char>.Shared.Return(charBuffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<List<string>> ReadAvailableLinesAsync(
        string filePath,
        StringBuilder remainder,
        Func<long> getOffset,
        Action<long> setOffset,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var decoder = Encoding.UTF8.GetDecoder();
        var charBuffer = ArrayPool<char>.Shared.Rent(ChunkSize);

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: ChunkSize,
                useAsync: true);

            var offset = getOffset();
            if (stream.Length <= offset)
            {
                return lines;
            }

            stream.Seek(offset, SeekOrigin.Begin);

            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                offset += bytesRead;
                var charsDecoded = decoder.GetChars(buffer, 0, bytesRead, charBuffer, 0, flush: false);
                for (var i = 0; i < charsDecoded; i++)
                {
                    var ch = charBuffer[i];
                    if (ch == '\n')
                    {
                        var completed = remainder.ToString().TrimEnd('\r');
                        remainder.Clear();
                        if (!string.IsNullOrWhiteSpace(completed))
                        {
                            lines.Add(completed);
                        }
                    }
                    else
                    {
                        remainder.Append(ch);
                    }
                }
            }

            setOffset(offset);
            return lines;
        }
        catch (IOException)
        {
            return lines;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(charBuffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

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

    public static async IAsyncEnumerable<string> TailLinesAsync(
        string filePath,
        bool includeExistingLines = true,
        bool followLiveChanges = true,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            yield break;
        }

        long offset = 0;
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

        var remainder = new StringBuilder();

        foreach (var line in await ReadAvailableLinesAsync(filePath, remainder, () => offset, newOffset => offset = newOffset, cancellationToken).ConfigureAwait(false))
        {
            yield return line;
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
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                pollCts.CancelAfter(TimeSpan.FromMilliseconds(300));

                try
                {
                    await wakeChannel.Reader.ReadAsync(pollCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Periodic fallback check in case OS coalesced FileSystemWatcher notifications
                }

                if (!File.Exists(filePath))
                {
                    continue;
                }

                long currentLen;
                try
                {
                    currentLen = new FileInfo(filePath).Length;
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

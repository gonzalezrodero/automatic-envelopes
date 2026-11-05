using System.IO.Compression;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace AutomaticEnvelopes.Api.Features.Knowledge.Extractors;

/// <summary>
/// PdfPig's decoded-size check runs only between chained filters, and only after each
/// filter has allocated its output. One FlateDecode stream can therefore expand without
/// a limit. This provider stops that inflate once the running total passes the cap,
/// and leaves every other filter unchanged.
/// </summary>
internal sealed class BoundedFlateFilterProvider : IFilterProvider
{
    private readonly IFilterProvider inner = DefaultFilterProvider.Instance;
    private readonly BoundedFlateFilter flate;

    public BoundedFlateFilterProvider(long maxDecodedBytes)
    {
        flate = new BoundedFlateFilter(maxDecodedBytes);
    }

    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) =>
        Wrap(inner.GetFilters(dictionary));

    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) =>
        Wrap(inner.GetNamedFilters(names));

    public IReadOnlyList<IFilter> GetAllFilters() =>
        Wrap(inner.GetAllFilters());

    private IReadOnlyList<IFilter> Wrap(IReadOnlyList<IFilter> filters)
    {
        if (filters.Count == 0)
        {
            return filters;
        }

        var wrapped = false;
        var copy = new IFilter[filters.Count];
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i] is FlateFilter)
            {
                copy[i] = flate;
                wrapped = true;
            }
            else
            {
                copy[i] = filters[i];
            }
        }

        return wrapped ? copy : filters;
    }
}

internal sealed class BoundedFlateFilter : IFilter
{
    private readonly FlateFilter inner = new();
    private readonly long maxDecodedBytes;
    private long used;

    public BoundedFlateFilter(long maxDecodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecodedBytes);
        this.maxDecodedBytes = maxDecodedBytes;
    }

    public bool IsSupported => true;

    public Memory<byte> Decode(Memory<byte> input, DictionaryToken streamDictionary, IFilterProvider filterProvider, int filterIndex)
    {
        var allowed = Math.Max(0, maxDecodedBytes - used);
        if (TryMeasureInflated(input, allowed, out var inflated))
        {
            if (inflated > allowed)
            {
                throw DecodeLimitExceeded();
            }

            used += inflated;
            return inner.Decode(input, streamDictionary, filterProvider, filterIndex);
        }

        var decoded = inner.Decode(input, streamDictionary, filterProvider, filterIndex);
        if (decoded.Length > allowed)
        {
            throw DecodeLimitExceeded();
        }

        used += decoded.Length;
        return decoded;
    }

    private PdfDocumentFormatException DecodeLimitExceeded() =>
        new($"PDF stream expands past the {maxDecodedBytes} byte decode limit.");

    /// <summary>
    /// Inflates the zlib payload and discards the output. Returns false when the bytes
    /// are not a Flate stream, so the caller can keep PdfPig's lenient fallback.
    /// </summary>
    private static bool TryMeasureInflated(ReadOnlyMemory<byte> input, long allowed, out long inflated)
    {
        inflated = 0;
        if (input.Length < 2)
        {
            return false;
        }

        var compressed = input.ToArray();
        MemoryStream? raw = null;
        DeflateStream? deflate = null;
        try
        {
            raw = new MemoryStream(compressed, 2, compressed.Length - 2, writable: false);
            deflate = new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true);
            Span<byte> buffer = stackalloc byte[8192];
            while (true)
            {
                var read = deflate.Read(buffer);
                if (read == 0)
                {
                    return true;
                }

                inflated += read;
                if (inflated > allowed)
                {
                    return true;
                }
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        finally
        {
            deflate?.Dispose();
            raw?.Dispose();
        }
    }
}

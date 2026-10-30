using System.Buffers;
using System.Security.Cryptography;

namespace RecompRadar.BinaryAnalysis;

public static class FileHasher
{
    /// <summary>Computes the SHA-256 of the whole stream in 1 MiB blocks and leaves the stream at position 0.</summary>
    public static async Task<string> ComputeSha256Async(Stream stream, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long total = stream.Length;
            long done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                done += read;
                progress?.Report(total == 0 ? 1.0 : (double)done / total);
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            stream.Position = 0;
        }
    }
}

using System.Buffers;
using System.IO;
using System.Security.Cryptography;

namespace CloudLink.Core;

/// <summary>
/// OneDrive's QuickXorHash: every input byte is XORed into a 160-bit circular register,
/// each one 11 bits further along than the last, and the length is XORed into the tail.
/// </summary>
public sealed class QuickXorHash
{
    const int Bits = 160;
    readonly byte[] _reg = new byte[Bits / 8];
    int _bit;
    long _length;

    public void Append(ReadOnlySpan<byte> data)
    {
        var reg = _reg;
        int bit = _bit;
        foreach (byte b in data)
        {
            int idx = bit >> 3;
            int v = b << (bit & 7);
            reg[idx] ^= (byte)v;
            reg[idx == 19 ? 0 : idx + 1] ^= (byte)(v >> 8);
            bit += 11;
            if (bit >= Bits) bit -= Bits;
        }
        _bit = bit;
        _length += data.Length;
    }

    public byte[] Finish()
    {
        var result = (byte[])_reg.Clone();
        var len = BitConverter.GetBytes(_length);
        if (!BitConverter.IsLittleEndian) Array.Reverse(len);
        for (int i = 0; i < 8; i++) result[12 + i] ^= len[i];
        return result;
    }
}

public static class Hashing
{
    public static async Task<byte[]> ComputeAsync(string path, HashKind kind, CancellationToken ct)
    {
        byte[] buf = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (kind == HashKind.QuickXor)
            {
                var q = new QuickXorHash();
                int n;
                while ((n = await fs.ReadAsync(buf, ct)) > 0) q.Append(buf.AsSpan(0, n));
                return q.Finish();
            }

            using var h = IncrementalHash.CreateHash(kind switch
            {
                HashKind.Md5 => HashAlgorithmName.MD5,
                HashKind.Sha1 => HashAlgorithmName.SHA1,
                HashKind.Sha256 => HashAlgorithmName.SHA256,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            });
            int read;
            while ((read = await fs.ReadAsync(buf, ct)) > 0) h.AppendData(buf, 0, read);
            return h.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}

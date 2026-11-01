using System;
using System.Security.Cryptography;
using NBitcoin;

namespace Gateway.Blockchain.Tron;

public class TronAddressDerivationService
{
    private readonly ExtPubKey _extPubKey;

    public TronAddressDerivationService(string accountXPub)
    {
        if (string.IsNullOrWhiteSpace(accountXPub))
            throw new ArgumentException("Account xPub cannot be empty.", nameof(accountXPub));

        var btcExtPubKey = new BitcoinExtPubKey(accountXPub.Trim(), Network.Main);
        _extPubKey = btcExtPubKey.ExtPubKey;
    }

    public string DeriveAddress(uint index)
    {
        // اشتقاق آدرس عمومی فرزند در شاخه: 0 / index
        var childPubKey = _extPubKey.Derive(0).Derive(index).PubKey;
        return PublicKeyToTronAddress(childPubKey);
    }

    public static string PublicKeyToTronAddress(PubKey pubKey)
    {
        // استخراج ۶۴ بایت کلید عمومی خام (حذف بایت 0x04)
        var uncompressed = pubKey.Decompress().ToBytes();
        var rawKey = new byte[64];
        Buffer.BlockCopy(uncompressed, 1, rawKey, 0, 64);

        // محاسبه هش استاندارد Keccak-256
        var hash = Keccak256(rawKey);

        // ۲۰ بایت انتهای هش به همراه پیشوند 0x41 شبکه ترون (مجموعاً ۲۱ بایت)
        var addressPayload = new byte[21];
        addressPayload[0] = 0x41;
        Buffer.BlockCopy(hash, 12, addressPayload, 1, 20);

        // انکود Base58Check به فرمت آدرس استاندارد ترون (شروع با T)
        return Base58CheckEncode(addressPayload);
    }

    private static string Base58CheckEncode(byte[] payload)
    {
        using var sha256 = SHA256.Create();
        var hash1 = sha256.ComputeHash(payload);
        var hash2 = sha256.ComputeHash(hash1);

        var dataWithChecksum = new byte[payload.Length + 4];
        Buffer.BlockCopy(payload, 0, dataWithChecksum, 0, payload.Length);
        Buffer.BlockCopy(hash2, 0, dataWithChecksum, payload.Length, 4);

        return ToBase58(dataWithChecksum);
    }

    private static string ToBase58(byte[] data)
    {
        const string alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        var intData = new System.Numerics.BigInteger(data, isUnsigned: true, isBigEndian: true);
        var result = string.Empty;

        while (intData > 0)
        {
            intData = System.Numerics.BigInteger.DivRem(intData, 58, out var remainder);
            result = alphabet[(int)remainder] + result;
        }

        for (var i = 0; i < data.Length && data[i] == 0; i++)
        {
            result = '1' + result;
        }

        return result;
    }

    #region Pure Keccak-256 Implementation
    private static readonly ulong[] RoundConstants =
    [
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808aUL, 0x8000000080008000UL,
        0x000000000000808bUL, 0x0000000080000001UL, 0x8000000080008081UL, 0x8000000000008009UL,
        0x000000000000008aUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000aUL,
        0x000000008000808bUL, 0x800000000000008bUL, 0x8000000000008089UL, 0x8000000000008003UL,
        0x8000000000008002UL, 0x8000000000000080UL, 0x000000000000800aUL, 0x800000008000000aUL,
        0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL
    ];

    private static readonly int[] RhoOffsets =
    [
        0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14
    ];

    private static byte[] Keccak256(byte[] message)
    {
        const int rate = 136; // 1088 bits
        var state = new ulong[25];
        var padded = Pad(message, rate);

        for (var i = 0; i < padded.Length; i += rate)
        {
            for (var j = 0; j < rate / 8; j++)
            {
                state[j] ^= BitConverter.ToUInt64(padded, i + j * 8);
            }
            KeccakF1600(state);
        }

        var output = new byte[32];
        for (var i = 0; i < 4; i++)
        {
            var bytes = BitConverter.GetBytes(state[i]);
            Buffer.BlockCopy(bytes, 0, output, i * 8, 8);
        }

        return output;
    }

    private static byte[] Pad(byte[] data, int rate)
    {
        var padLen = rate - (data.Length % rate);
        if (padLen == 0) padLen = rate;

        var result = new byte[data.Length + padLen];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);

        if (padLen == 1)
        {
            result[data.Length] = 0x81;
        }
        else
        {
            result[data.Length] = 0x01; // استاندارد Keccak-256 (EVM/TRON)
            result[^1] = 0x80;
        }

        return result;
    }

    private static void KeccakF1600(ulong[] a)
    {
        var c = new ulong[5];
        var d = new ulong[5];
        var b = new ulong[25];

        for (var round = 0; round < 24; round++)
        {
            for (var x = 0; x < 5; x++)
                c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];

            for (var x = 0; x < 5; x++)
                d[x] = c[(x + 4) % 5] ^ RotL(c[(x + 1) % 5], 1);

            for (var i = 0; i < 25; i++)
                a[i] ^= d[i % 5];

            for (var i = 0; i < 25; i++)
                b[PiIndex(i)] = RotL(a[i], RhoOffsets[i]);

            for (var y = 0; y < 25; y += 5)
            {
                for (var x = 0; x < 5; x++)
                    a[y + x] = b[y + x] ^ (~b[y + (x + 1) % 5] & b[y + (x + 2) % 5]);
            }

            a[0] ^= RoundConstants[round];
        }
    }

    private static int PiIndex(int index)
    {
        var x = index % 5;
        var y = index / 5;
        return ((0 * x + 1 * y) % 5) + 5 * ((2 * x + 3 * y) % 5);
    }

    private static ulong RotL(ulong val, int shift) => (val << shift) | (val >> (64 - shift));
    #endregion
}
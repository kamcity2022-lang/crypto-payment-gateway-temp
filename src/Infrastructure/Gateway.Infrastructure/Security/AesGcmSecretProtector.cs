using System;
using System.Security.Cryptography;
using System.Text;
using Gateway.Application.Contracts;
using Microsoft.Extensions.Configuration;

namespace Gateway.Infrastructure.Security;

public class AesGcmSecretProtector : ISecretProtector
{
    private readonly byte[] _masterKey;

    public AesGcmSecretProtector(IConfiguration configuration)
    {
        var keyHex = configuration["Security:MasterEncryptionKey"];
        if (string.IsNullOrWhiteSpace(keyHex))
        {
            throw new InvalidOperationException("Fatal: 'Security:MasterEncryptionKey' configuration is missing. Hardcoded keys are prohibited.");
        }

        try
        {
            _masterKey = Convert.FromHexString(keyHex.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Fatal: 'Security:MasterEncryptionKey' must be a valid hex string.", ex);
        }

        if (_masterKey.Length != 32) // 256 bits
        {
            throw new InvalidOperationException($"Fatal: 'Security:MasterEncryptionKey' must be exactly 256 bits (32 bytes). Current: {_masterKey.Length} bytes.");
        }
    }

    public string Encrypt(string plainText)
    {
        if (plainText == null) throw new ArgumentNullException(nameof(plainText));

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        RandomNumberGenerator.Fill(nonce);

        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aesGcm = new AesGcm(_masterKey, AesGcm.TagByteSizes.MaxSize);
        aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var combined = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, combined, nonce.Length + tag.Length, cipherBytes.Length);

        return Convert.ToBase64String(combined);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText)) throw new ArgumentException("Ciphertext cannot be empty.", nameof(cipherText));

        var combined = Convert.FromBase64String(cipherText);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;

        if (combined.Length < nonceSize + tagSize)
            throw new CryptographicException("Invalid ciphertext length.");

        var nonce = new byte[nonceSize];
        var tag = new byte[tagSize];
        var cipherBytes = new byte[combined.Length - nonceSize - tagSize];

        Buffer.BlockCopy(combined, 0, nonce, 0, nonceSize);
        Buffer.BlockCopy(combined, nonceSize, tag, 0, tagSize);
        Buffer.BlockCopy(combined, nonceSize + tagSize, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = new byte[cipherBytes.Length];
        using var aesGcm = new AesGcm(_masterKey, tagSize);
        aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
using System.Security.Cryptography;

namespace MegaDownloaderNext.Core.Mega;

public static class MegaCrypto
{
    public static void DecryptInPlace(byte[] data, int length, byte[] aesKey, byte[] nonce, long offset)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey;

        var encryptor = aes.CreateEncryptor();
        
        // Compute IV for the current offset
        var iv = new byte[16];
        Array.Copy(nonce, 0, iv, 0, 8);
        AdvanceCounter(iv, offset);

        // Compute the offset within the 16-byte block
        int blockOffset = (int)(offset % 16);
        
        var keystream = new byte[16];
        encryptor.TransformBlock(iv, 0, 16, keystream, 0);

        for (int i = 0; i < length; i++)
        {
            if (blockOffset == 16)
            {
                IncrementCounter(iv);
                encryptor.TransformBlock(iv, 0, 16, keystream, 0);
                blockOffset = 0;
            }

            data[i] ^= keystream[blockOffset];
            blockOffset++;
        }
    }

    private static void IncrementCounter(byte[] counter)
    {
        for (int i = 15; i >= 8; i--)
        {
            if (++counter[i] != 0) break;
        }
    }

    public static void AdvanceCounter(byte[] counter, long offset)
    {
        long blocks = offset / 16;
        for (int i = 0; i < 8; i++)
        {
            long val = counter[15 - i] + (blocks & 0xFF);
            counter[15 - i] = (byte)val;
            blocks = (blocks >> 8) + (val >> 8);
        }
    }
}

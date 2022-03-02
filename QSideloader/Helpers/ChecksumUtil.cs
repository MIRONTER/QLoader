using System;
// ReSharper disable InconsistentNaming

namespace QSideloader.Helpers;

public static class ChecksumUtil
{
    public static string GetChecksum(HashingAlgoTypes hashingAlgoType, string filename)
    {
        using var hasher = System.Security.Cryptography.HashAlgorithm.Create(hashingAlgoType.ToString()) ?? 
                           throw new ArgumentException($"{hashingAlgoType.ToString()} is not a valid hash algorithm");
        using var stream = System.IO.File.OpenRead(filename);
        var hash = hasher.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "");
    }

}
public enum HashingAlgoTypes
{
    MD5,
    SHA1,
    SHA256,
    SHA384,
    SHA512
}
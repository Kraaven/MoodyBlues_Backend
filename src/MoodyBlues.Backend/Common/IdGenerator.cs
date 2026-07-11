using System.Security.Cryptography;

namespace MoodyBlues.Backend.Common;

/// <summary>Generates short, opaque, URL/filesystem-safe random tokens (e.g. <c>Project.DeveloperId</c>).</summary>
public static class IdGenerator
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public static string NewToken(int length = 16)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);

        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }
}

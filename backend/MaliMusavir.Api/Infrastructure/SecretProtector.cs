using System.Security.Cryptography;
using System.Text;

namespace MaliMusavir.Api.Infrastructure;

/// <summary>
/// Hassas değerleri (entegratör API anahtarları) AES-256 ile şifreler/çözer.
/// Anahtar appsettings "Security:SecretKey" değerinden okunur (tam 32 karakter).
/// Değerler DB'de "base64(IV + ciphertext)" olarak saklanır; düz metin ASLA yazılmaz.
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedText);
}

public class SecretProtector : ISecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(IConfiguration configuration)
    {
        var key = configuration["Security:SecretKey"]
                  ?? throw new InvalidOperationException("Security:SecretKey yapılandırması eksik.");

        if (key.Length != 32)
            throw new InvalidOperationException("Security:SecretKey tam 32 karakter olmalıdır (AES-256).");

        _key = Encoding.UTF8.GetBytes(key);
    }

    public string Protect(string plainText)
    {
        ArgumentException.ThrowIfNullOrEmpty(plainText);

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        // IV + şifreli veri birlikte saklanır; IV gizli değildir, her şifrelemede rastgeledir.
        var output = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, output, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, output, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(output);
    }

    public string Unprotect(string protectedText)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedText);

        var input = Convert.FromBase64String(protectedText);

        using var aes = Aes.Create();
        aes.Key = _key;

        var ivLength = aes.BlockSize / 8; // 16 bayt
        if (input.Length <= ivLength)
            throw new InvalidOperationException("Şifreli veri bozuk: çözümlenemedi.");

        var iv = new byte[ivLength];
        Buffer.BlockCopy(input, 0, iv, 0, ivLength);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        var plainBytes = decryptor.TransformFinalBlock(input, ivLength, input.Length - ivLength);

        return Encoding.UTF8.GetString(plainBytes);
    }
}

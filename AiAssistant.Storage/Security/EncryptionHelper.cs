using System;
using System.Security.Cryptography;
using System.Text;

namespace AiAssistant.Storage.Security;

public static class EncryptionHelper
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AiAssistant.Vsix.SecretEntropy_v1");

    /// <summary>
    /// Encrypts a plain text string using Windows DPAPI. Returns Base64 encoded encrypted string.
    /// </summary>
    public static string? Encrypt(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
            return plainText;

        try
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encryptedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encryptedBytes);
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Failed to encrypt data using DPAPI.", ex);
        }
    }

    /// <summary>
    /// Decrypts a Base64 encoded encrypted string using Windows DPAPI.
    /// If the string is not encrypted (e.g. legacy plain text), it returns the plain text.
    /// </summary>
    public static string? Decrypt(string? encryptedText)
    {
        if (string.IsNullOrWhiteSpace(encryptedText))
            return encryptedText;

        try
        {
            byte[] encryptedBytes;
            try
            {
                encryptedBytes = Convert.FromBase64String(encryptedText);
            }
            catch (FormatException)
            {
                return encryptedText; // Legacy plaintext key
            }
            
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (CryptographicException)
        {
            // If Unprotect fails, it might be because the key was created on another machine,
            // or it's actually just a legacy plain text key that happened to be valid Base64.
            // We'll assume it's legacy plain text if decryption fails.
            return encryptedText;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Decryption failed: {ex.Message}");
            return encryptedText;
        }
    }
}

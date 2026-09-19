using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AiAssistant.Storage.Models;

/// <summary>
/// Represents a database connection configuration with encrypted credential storage.
/// </summary>
public record DatabaseConnection
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string Name { get; init; } = string.Empty;
    public string ServerAddress { get; init; } = string.Empty;
    public string DatabaseName { get; init; } = string.Empty;
    public AuthenticationType Authentication { get; init; } = AuthenticationType.WindowsAuth;
    public string? Username { get; init; }
    public bool IsEnabled { get; init; } = true;
    public PermissionLevel Permission { get; init; } = PermissionLevel.ReadOnly;
    public bool RequireApproval { get; init; } = true;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gets the encrypted password blob (stored as Base64 string).
    /// This is decrypted at runtime using Windows DPAPI.
    /// </summary>
    public string? EncryptedPassword { get; init; }

    /// <summary>
    /// Sets the password by encrypting it with Windows DPAPI (CurrentUser scope).
    /// </summary>
    public DatabaseConnection WithEncryptedPassword(string? plainPassword)
    {
        if (string.IsNullOrEmpty(plainPassword))
            return this with { EncryptedPassword = null };

        var plainBytes = Encoding.UTF8.GetBytes(plainPassword);
        var encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        return this with { EncryptedPassword = Convert.ToBase64String(encryptedBytes) };
    }

    /// <summary>
    /// Decrypts the password using Windows DPAPI (CurrentUser scope).
    /// </summary>
    public string? DecryptPassword()
    {
        if (string.IsNullOrEmpty(EncryptedPassword))
            return null;

        try
        {
            var encryptedBytes = Convert.FromBase64String(EncryptedPassword);
            var decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
        }
        catch (CryptographicException)
        {
            return null; // Decryption failed — password was encrypted by a different user
        }
    }

    public override string ToString()
    {
        return $"{Name} ({ServerAddress}/{DatabaseName}) [{Permission}]";
    }
}

public enum AuthenticationType
{
    WindowsAuth,
    SqlAuth
}

public enum PermissionLevel
{
    ReadOnly,
    AllowWrite,
    FullAccess
}

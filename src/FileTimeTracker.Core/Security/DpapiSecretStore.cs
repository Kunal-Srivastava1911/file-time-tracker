using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using FileTimeTracker.Core.Serialization;
using FileTimeTracker.Core.Storage;

namespace FileTimeTracker.Core.Security;

/// <summary>
/// Stores the Dropbox refresh token on disk encrypted with Windows DPAPI at
/// <see cref="DataProtectionScope.LocalMachine"/> scope (design.md §4.3).
/// </summary>
/// <remarks>
/// LocalMachine scope — not CurrentUser — because the installer writes the token while elevated
/// but the agent reads it as the logged-on employee. The blob is therefore decryptable by anyone
/// on that machine, so the file's ACL is the real control: the installer restricts write to
/// Administrators and the directory lives under ProgramData. The residual exposure is the §4.3
/// caveat, bounded by app-folder scoping and rotation.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore
{
    // Ties the ciphertext to this application; a blob lifted from another product will not decrypt.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FileTimeTracker/v1/dropbox");

    private readonly string _path;

    public DpapiSecretStore(string path)
    {
        _path = path;
    }

    public bool Exists => File.Exists(_path);

    public string Path => _path;

    public void Save(DropboxCredentials credentials)
    {
        credentials.Validate();

        var plaintext = Encoding.UTF8.GetBytes(TrackerJson.Serialize(credentials));
        try
        {
            var cipher = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(_path, cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public DropboxCredentials? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var cipher = File.ReadAllBytes(_path);
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
            return TrackerJson.Deserialize<DropboxCredentials>(Encoding.UTF8.GetString(plaintext));
        }
        catch (CryptographicException ex)
        {
            // Wrong machine, corrupted blob, or a restored disk image. Fail loudly: silently
            // running with no upload path would look like a working agent that never reports.
            throw new InvalidOperationException(
                $"Could not decrypt the Dropbox credential store at '{_path}'. Re-run the installer to re-provision it.",
                ex);
        }
        finally
        {
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public void Delete()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

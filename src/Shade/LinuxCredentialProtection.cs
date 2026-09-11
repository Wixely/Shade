using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Shade;

internal sealed class CredentialStoreException(string message) : Exception(message);

// Only the random encryption key is stored in Secret Service. Ciphertext lives in private
// settings, so replacement/forget has the same atomic file lifecycle as Windows DPAPI.
[SupportedOSPlatform("linux")]
internal static class LinuxCredentialProtection
{
    private const string Prefix = "secret-service-aes-v1:";
    private const string Secret = "libsecret-1.so.0", Glib = "libglib-2.0.so.0";
    internal static Task<string> ProtectAsync(string password) => Task.Run(() =>
    {
        var key = GetKey(create: true);
        try { return Encrypt(password, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    });
    internal static Task<string> UnprotectAsync(string value) => Task.Run(() =>
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CredentialStoreException("Saved credentials belong to another platform. Re-enter the broker password.");
        var key = GetKey(create: false);
        try { return Decrypt(value, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    });

    internal static string Encrypt(string password, byte[] key)
    {
        var plain = Encoding.UTF8.GetBytes(password);
        var envelope = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(envelope.AsSpan(0, 12));
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(envelope.AsSpan(0, 12), plain, envelope.AsSpan(28), envelope.AsSpan(12, 16), Encoding.UTF8.GetBytes(Prefix));
            return Prefix + Convert.ToBase64String(envelope);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    internal static string Decrypt(string value, byte[] key)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) throw new CryptographicException();
        var envelope = Convert.FromBase64String(value[Prefix.Length..]);
        if (envelope.Length is < 28 or > 16412) throw new CryptographicException();
        var plain = new byte[envelope.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.AsSpan(0, 12), envelope.AsSpan(28), envelope.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(Prefix));
            return Encoding.UTF8.GetString(plain);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] GetKey(bool create)
    {
        // Serialize cooperating Shade processes so first-use key creation cannot overwrite a
        // key already used to encrypt another instance's settings. Never replace an unreadable key.
        using var mutex = new Mutex(false, "Shade.CredentialKey." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName))));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new CredentialStoreException("Credential store is busy. Retry saving the broker settings.");
            return ReadOrCreateKey(create);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { throw new CredentialStoreException("Linux password storage requires libsecret and a desktop Secret Service. No password was saved."); }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }
    private static byte[] ReadOrCreateKey(bool create)
    {
        var library = NativeLibrary.Load(Glib);
        nint attributes = 0, cancellable = 0, error = 0, result = 0;
        List<nint> strings = [];
        try
        {
            attributes = g_hash_table_new(NativeLibrary.GetExport(library, "g_str_hash"), NativeLibrary.GetExport(library, "g_str_equal"));
            void Attribute(string name, string value)
            {
                var k = Marshal.StringToCoTaskMemUTF8(name); strings.Add(k);
                var v = Marshal.StringToCoTaskMemUTF8(value); strings.Add(v);
                g_hash_table_insert(attributes, k, v);
            }
            Attribute("application", "org.shade.desktop"); Attribute("purpose", "broker-encryption-key-v1");
            cancellable = g_cancellable_new();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var cancel = deadline.Token.Register(() => g_cancellable_cancel(cancellable));
            result = secret_password_lookupv_sync(0, attributes, cancellable, ref error);
            Check(error, deadline.Token);
            if (result != 0)
            {
                var encoded = Marshal.PtrToStringUTF8(result) ?? throw new CryptographicException();
                var key = Convert.FromBase64String(encoded);
                if (key.Length == 32) return key;
                CryptographicOperations.ZeroMemory(key); throw new CryptographicException();
            }
            if (!create) throw new CredentialStoreException("The desktop keyring does not contain Shade's encryption key. Unlock the original keyring or re-enter the password.");
            // A cancelled unlock can look like an empty lookup. Search metadata without unlocking
            // before creation, so an existing locked key is never silently replaced.
            var matches = secret_password_searchv_sync(0, attributes, 0, cancellable, ref error);
            try
            {
                Check(error, deadline.Token);
                if (matches != 0) throw new CredentialStoreException("Shade's encryption key is locked. Unlock the desktop keyring and retry.");
            }
            finally
            {
                for (var item = matches; item != 0; item = Marshal.ReadIntPtr(item, IntPtr.Size))
                    g_object_unref(Marshal.ReadIntPtr(item));
                if (matches != 0) g_list_free(matches);
            }
            var generated = RandomNumberGenerator.GetBytes(32);
            try
            {
                var success = secret_password_storev_sync(0, attributes, 0, "Shade broker credential encryption", Convert.ToBase64String(generated), cancellable, ref error);
                Check(error, deadline.Token);
                if (success == 0) throw new CredentialStoreException("Unlock the desktop keyring and retry saving the broker password.");
                return generated.ToArray();
            }
            finally { CryptographicOperations.ZeroMemory(generated); }
        }
        finally
        {
            if (result != 0) secret_password_free(result);
            if (error != 0) g_error_free(error);
            if (cancellable != 0) g_object_unref(cancellable);
            if (attributes != 0) g_hash_table_unref(attributes);
            foreach (var value in strings) Marshal.FreeCoTaskMem(value);
            NativeLibrary.Free(library);
        }
    }
    private static void Check(nint error, CancellationToken token)
    {
        if (token.IsCancellationRequested) throw new CredentialStoreException("Desktop keyring operation timed out. Unlock the keyring and retry.");
        if (error != 0) throw new CredentialStoreException("Desktop keyring unavailable or locked. Unlock it and retry saving the broker settings.");
    }
    [DllImport(Secret)] private static extern nint secret_password_lookupv_sync(nint schema, nint attributes, nint cancellable, ref nint error);
    [DllImport(Secret)] private static extern nint secret_password_searchv_sync(nint schema, nint attributes, int flags, nint cancellable, ref nint error);
    [DllImport(Secret)] private static extern int secret_password_storev_sync(nint schema, nint attributes, nint collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label, [MarshalAs(UnmanagedType.LPUTF8Str)] string password, nint cancellable, ref nint error);
    [DllImport(Secret)] private static extern void secret_password_free(nint password);
    [DllImport(Glib)] private static extern nint g_hash_table_new(nint hash, nint equal);
    [DllImport(Glib)] private static extern int g_hash_table_insert(nint table, nint key, nint value);
    [DllImport(Glib)] private static extern void g_hash_table_unref(nint table);
    [DllImport(Glib)] private static extern void g_error_free(nint error);
    [DllImport(Glib)] private static extern void g_list_free(nint list);
    [DllImport("libgio-2.0.so.0")] private static extern nint g_cancellable_new();
    [DllImport("libgio-2.0.so.0")] private static extern void g_cancellable_cancel(nint cancellable);
    [DllImport("libgobject-2.0.so.0")] private static extern void g_object_unref(nint value);
}

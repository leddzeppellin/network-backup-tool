using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace NetBackupTool.Droid;

/// <summary>
/// Guarda segredos (a senha das pastas da empresa) criptografados com AES-GCM. A chave fica no
/// Android Keystore e não sai do aparelho; o texto cifrado fica nas preferências do app.
/// </summary>
internal static class AndroidSecrets
{
    private const string KeyAlias = "nbt-secrets";
    private const string Prefs = "nbt-secrets";

    private static IKey GetKey()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.GetKey(KeyAlias, null) is IKey existing) return existing;
        var gen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
        gen.Init(new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
            .Build());
        return gen.GenerateKey()!;
    }

    public static void Save(Context ctx, string name, string? value)
    {
        var prefs = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private)!;
        var edit = prefs.Edit()!;
        if (value == null)
        {
            edit.Remove(name);
        }
        else
        {
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.EncryptMode, GetKey());
            var data = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(value))!;
            var iv = cipher.GetIV()!;
            edit.PutString(name, Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(data));
        }
        edit.Apply();
    }

    public static string? Load(Context ctx, string name)
    {
        try
        {
            var stored = ctx.GetSharedPreferences(Prefs, FileCreationMode.Private)!.GetString(name, null);
            if (stored == null) return null;
            var parts = stored.Split(':');
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.DecryptMode, GetKey(), new GCMParameterSpec(128, Convert.FromBase64String(parts[0])));
            return System.Text.Encoding.UTF8.GetString(cipher.DoFinal(Convert.FromBase64String(parts[1]))!);
        }
        catch
        {
            // Chave trocada (ex.: backup restaurado em outro aparelho): pede a senha de novo.
            return null;
        }
    }
}

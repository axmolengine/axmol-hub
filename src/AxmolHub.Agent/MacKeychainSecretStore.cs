using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxmolHub.Core;

namespace AxmolHub.Agent;

/// <summary>Native generic-password operations, injectable without accessing a user's keychain.</summary>
public interface IMacKeychainApi
{
    (int Status, string? Value) Read(string service, string account);
    int Add(string service, string account, string value);
    int Update(string service, string account, string value);
    int Delete(string service, string account);
}

/// <summary>
/// Secrets isolated by data root in the current user's login keychain, held in <b>one</b> generic-password item.
///
/// <para><b>Why one item rather than one per credential.</b> macOS grants access per <i>item</i> and per
/// application identity: a new item is a new password sheet, and "Always Allow" covers only the item it was
/// clicked for. One item per credential therefore asked once per provider — and once more per extra account of
/// the same provider — on every launch whose signature macOS had not seen before. A single bundle costs one
/// sheet per launch instead, and it keeps the service name, so the data-root isolation (and the identity macOS
/// already knows this app by) does not change.</para>
///
/// <para>The bundle is a JSON object of credential id → secret under <see cref="Account"/>: API keys and OpenAI
/// token bundles together, a few kilobytes, well inside what a generic password carries.</para>
///
/// <para><b>Legacy items are folded in on read.</b> An earlier build stored every secret under its own account
/// (the credential id). <see cref="Read"/> looks there only when the bundle has no entry for that id, moves what
/// it finds into the bundle and deletes the old item, so an old install upgrades as its credentials are used
/// rather than paying one sheet per stored key before anything is asked of it.</para>
///
/// <para><b>Reads are memoized per instance.</b> A one-shot "Allow" grant means rehydrating six credentials
/// would raise six sheets even though all six now live in one item. The bundle is fetched once and kept in
/// memory. Nothing is cached unless it actually arrived, so a locked keychain can be retried once the user
/// unlocks it instead of staying failed for the life of the process.</para>
/// </summary>
public sealed class MacKeychainSecretStore : ISecretStore
{
    public const int NotFound = -25300;
    public const int Duplicate = -25299;

    /// <summary>The account holding every secret. Stable: renaming it would strand the bundle it names.</summary>
    public const string Account = "hub-secrets";

    private readonly IMacKeychainApi _api;
    private readonly object _gate = new();
    private Dictionary<string, string>? _entries;
    private string? _problem;

    public string Service { get; }

    public MacKeychainSecretStore(string dataRoot, IMacKeychainApi? api = null)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).Normalize(NormalizationForm.FormC);
        Service = "dev.axmol.hub." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root))).ToLowerInvariant();
        _api = api ?? new NativeMacKeychainApi();
    }

    public SecretStoreDescriptor Descriptor => new(SecretStoreKind.Keychain, Service, _problem);

    public string? Read(string id)
    {
        lock (_gate)
        {
            var entries = Entries();
            return entries.TryGetValue(id, out var value) ? value : Adopt(id, entries);
        }
    }

    public void Write(string id, string value)
    {
        lock (_gate)
        {
            var entries = new Dictionary<string, string>(Entries());
            if (entries.TryGetValue(id, out var stored) && stored == value) return;
            entries[id] = value;
            Store(entries);
            _entries = entries;
            // Best effort: the bundle already answers for this id. Removing a credential is the operation that
            // has to guarantee the old item is gone, and it says so by throwing — see Delete.
            _api.Delete(Service, id);
        }
    }

    public void Delete(string id)
    {
        lock (_gate)
        {
            // Strict, and before the bundle is touched: if the old item cannot be removed the credential is not
            // removed either, so a failure never leaves a secret behind that the next read would resurrect.
            LegacyDelete(id);
            var entries = new Dictionary<string, string>(Entries());
            if (!entries.Remove(id)) return;
            if (entries.Count == 0) DeleteBundle(); else Store(entries);
            _entries = entries;
        }
    }

    /// <summary>The bundle, fetched once per instance; a failed fetch is not remembered.</summary>
    private Dictionary<string, string> Entries()
    {
        if (_entries is not null) return _entries;
        var (status, document) = _api.Read(Service, Account);
        if (status == NotFound) { _problem = null; return _entries = []; }
        Check(status);
        _entries = Parse(document, out var corrupt);
        if (corrupt) _problem = "The Keychain secret bundle could not be read; stored keys have to be entered again.";
        return _entries;
    }

    /// <summary>A bundle that does not parse reads as empty so the next write can replace it, not as a hard failure.</summary>
    private static Dictionary<string, string> Parse(string? document, out bool corrupt)
    {
        corrupt = false;
        if (string.IsNullOrEmpty(document)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(document) ?? []; }
        catch (JsonException) { corrupt = true; return []; }
    }

    /// <summary>Moves a legacy per-credential item into the bundle the first time that credential is asked for.</summary>
    private string? Adopt(string id, Dictionary<string, string> entries)
    {
        var (status, value) = _api.Read(Service, id);
        if (status == NotFound) return null;
        Check(status);
        if (value is null) return null;
        var adopted = new Dictionary<string, string>(entries) { [id] = value };
        Store(adopted);
        _entries = adopted;
        _api.Delete(Service, id);
        return value;
    }

    private void Store(Dictionary<string, string> entries)
    {
        var document = JsonSerializer.Serialize(entries);
        var status = _api.Update(Service, Account, document);
        if (status == NotFound) status = _api.Add(Service, Account, document);
        if (status == Duplicate) status = _api.Update(Service, Account, document);
        Check(status);
    }

    private void DeleteBundle()
    {
        var status = _api.Delete(Service, Account);
        if (status != NotFound) Check(status);
    }

    private void LegacyDelete(string id)
    {
        var status = _api.Delete(Service, id);
        if (status != NotFound) Check(status);
    }

    private void Check(int status)
    {
        if (status == 0) { _problem = null; return; }
        _problem = $"Keychain operation failed (OSStatus {status}). Unlock the login keychain and allow Axmol Hub access, then retry.";
        throw new SecretStoreException(SecretStoreFailure.KeyUnusable, _problem);
    }
}

/// <summary>Core Foundation objects are released on every exit, including failed Security calls.</summary>
internal sealed class NativeMacKeychainApi : IMacKeychainApi
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly IntPtr SecurityHandle = NativeLibrary.Load(Security);
    private static readonly IntPtr CFHandle = NativeLibrary.Load(CF);
    private static IntPtr Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle, name));
    private static IntPtr True => Marshal.ReadIntPtr(NativeLibrary.GetExport(CFHandle, "kCFBooleanTrue"));
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keys, IntPtr values);
    [DllImport(CF)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);
    [DllImport(CF)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint count);
    [DllImport(CF)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CF)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CF)] private static extern void CFRelease(IntPtr value);
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);

    private sealed class Attributes : IDisposable
    {
        private readonly List<IntPtr> _owned = [];
        public IntPtr Handle { get; } = CFDictionaryCreateMutable(IntPtr.Zero, 0,
            NativeLibrary.GetExport(CFHandle, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CFHandle, "kCFTypeDictionaryValueCallBacks"));
        public Attributes(string? service = null, string? account = null)
        {
            if (service is null) return;
            Set("kSecClass", Constant("kSecClassGenericPassword"));
            Text("kSecAttrService", service);
            Text("kSecAttrAccount", account!);
        }
        public void Set(string key, IntPtr value) => CFDictionarySetValue(Handle, Constant(key), value);
        public void Text(string key, string value)
        {
            var pointer = CFStringCreateWithCString(IntPtr.Zero, value, 0x08000100);
            _owned.Add(pointer); Set(key, pointer);
        }
        public void Data(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                var pointer = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
                _owned.Add(pointer); Set("kSecValueData", pointer);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose()
        {
            CFRelease(Handle);
            foreach (var pointer in _owned) CFRelease(pointer);
        }
    }
    public (int Status, string? Value) Read(string service, string account)
    {
        using var query = new Attributes(service, account);
        query.Set("kSecReturnData", True);
        query.Set("kSecMatchLimit", Constant("kSecMatchLimitOne"));
        var status = SecItemCopyMatching(query.Handle, out var result);
        try
        {
            if (status != 0 || result == IntPtr.Zero) return (status, null);
            var bytes = new byte[checked((int)CFDataGetLength(result))];
            try
            {
                Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, bytes.Length);
                return (status, Encoding.UTF8.GetString(bytes));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (result != IntPtr.Zero) CFRelease(result); }
    }
    public int Add(string service, string account, string value)
    {
        using var attributes = new Attributes(service, account);
        attributes.Data(value);
        return SecItemAdd(attributes.Handle, IntPtr.Zero);
    }
    public int Update(string service, string account, string value)
    {
        using var query = new Attributes(service, account);
        using var attributes = new Attributes();
        attributes.Data(value);
        return SecItemUpdate(query.Handle, attributes.Handle);
    }
    public int Delete(string service, string account)
    {
        using var query = new Attributes(service, account);
        return SecItemDelete(query.Handle);
    }
}

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cranberry.Zone.Economy;

/// <summary>
/// Durable account snapshots with one atomic commit for debit/consume/grant/state/receipt.
/// No configured balance is reapplied to an existing account. Reads fail closed on corrupt files.
/// A per-file process gate and exclusive lock file serialize callers, including separate store
/// instances. Cross-process contention fails closed. Disk is authoritative; there is no stale cache.
/// </summary>
public sealed class AccountEconomyStore
{
    public const uint MaximumValue = int.MaxValue;
    public const int MaximumItems = 20_000;
    public const int MaximumCurrencies = 64;
    public const int MaximumStates = 10_000;
    // Level 100 takes 99,000 kill awards. Preserve their replay receipts plus room for
    // other account operations; aggregate text and file limits must accommodate them too.
    public const int MaximumReceipts = 150_000;
    public const int MaximumPayloadLength = 65_536;
    public const int MaximumTextLength = 32 * 1024 * 1024;
    public const int MaximumMutationsPerOperation = 1024;
    public const int MaximumFileBytes = 128 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Action<string> _log;
    private readonly Action<EconomyPersistenceStage>? _persistenceFault;
    public string Root { get; }

    public AccountEconomyStore(string root, Action<string>? log = null,
        Action<EconomyPersistenceStage>? persistenceFault = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
        _log = log ?? (_ => { });
        _persistenceFault = persistenceFault;
    }

    /// <summary>Hash an opaque server-resolved ID; no untrusted text becomes a filesystem path.</summary>
    public static string FileNameFor(string accountId)
    {
        ValidateAccountId(accountId);
        return "a-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))).ToLowerInvariant() + ".json";
    }

    /// <summary>Load an existing account, or durably create it using explicit seeds exactly once.</summary>
    public AccountEconomySnapshot GetOrCreate(string accountId, AccountEconomySeed? seed = null)
    {
        string path = Path.Combine(Root, FileNameFor(accountId));
        lock (Gates.GetOrAdd(path, _ => new object()))
        {
            try
            {
                Directory.CreateDirectory(Root);
                using FileStream accountLock = LockAccount(path);
                return new AccountEconomySnapshot(LoadOrCreate(path, accountId, seed));
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                throw StorageException(exception);
            }
        }
    }

    /// <summary>
    /// Run a server-authorized operation. An existing operation ID returns its original receipt
    /// without running the callback; reusing the ID for a different kind is refused. A domain
    /// rejection discards the complete draft. Other callback exceptions propagate without a commit.
    /// Operation IDs require a caller's verified retry/lifecycle policy, not an invented wire field.
    /// </summary>
    public AccountEconomyResult Execute(string accountId, string operationId, string operationKind,
        Func<AccountEconomyDraft, string?> mutate, AccountEconomySeed? seed = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        ValidateText(operationId, 256, "Operation ID");
        ValidateText(operationKind, 128, "Operation kind");
        string path = Path.Combine(Root, FileNameFor(accountId));
        lock (Gates.GetOrAdd(path, _ => new object()))
        {
            EconomyAccountFile? committed = null;
            try
            {
                Directory.CreateDirectory(Root);
                using FileStream accountLock = LockAccount(path);
                committed = LoadOrCreate(path, accountId, seed);
                if (committed.Receipts.TryGetValue(operationId, out EconomyOperationReceipt? previous))
                {
                    if (!string.Equals(previous.Kind, operationKind, StringComparison.Ordinal))
                        return Failure("Operation ID already belongs to a different operation kind.", committed);
                    return new(true, true, null, new(committed), previous);
                }
                if (committed.Receipts.Count >= MaximumReceipts)
                    return Failure("Account receipt limit reached; archival requires an explicit replay-safe policy.", committed);

                EconomyAccountFile draft = committed.Clone();
                string? resultJson = mutate(new AccountEconomyDraft(draft));
                if (resultJson is not null) ValidateText(resultJson, MaximumPayloadLength, "Result payload", allowEmpty: true);
                if (draft.Revision == long.MaxValue) throw new EconomyRejectedException("Account revision limit reached.");
                draft.Revision++;
                var receipt = new EconomyOperationReceipt(operationId, operationKind, resultJson, draft.Revision, DateTimeOffset.UtcNow);
                draft.Receipts.Add(operationId, receipt);
                ValidateFile(draft, accountId);
                // Freeze the callback's retained object before persistence or publication.
                EconomyAccountFile next = draft.Clone();
                Persist(path, next);
                return new(true, false, null, new(next), receipt);
            }
            catch (EconomyRejectedException exception)
            {
                return Failure(exception.Message, committed);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                AccountEconomyStoreException error = StorageException(exception);
                return Failure(error.Message, committed);
            }
        }
    }

    private static FileStream LockAccount(string path) =>
        new(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private EconomyAccountFile LoadOrCreate(string path, string accountId, AccountEconomySeed? seed)
    {
        if (File.Exists(path))
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumFileBytes) throw new InvalidDataException("Account file exceeds the size limit.");
            EconomyAccountFile file = JsonSerializer.Deserialize<EconomyAccountFile>(File.ReadAllBytes(path), Json)
                ?? throw new InvalidDataException("Account file is empty.");
            ValidateFile(file, accountId);
            return file;
        }

        // An interrupted first creation needs explicit recovery. Never interpret leftover state
        // as permission to create a second funded account while its original commit is uncertain.
        if (Directory.EnumerateFiles(Root, Path.GetFileName(path) + ".pending-*").Any())
            throw new InvalidDataException("An incomplete account creation requires recovery; refusing to reseed.");

        var created = new EconomyAccountFile { AccountId = accountId };
        foreach ((uint id, uint amount) in seed?.Balances ?? new Dictionary<uint, uint>())
            created.Balances.Add(id, amount);
        foreach (OwnedAccountItem item in seed?.Items ?? [])
        {
            ValidateItem(item);
            if (!created.Items.TryAdd(item.InstanceId, item))
                throw new EconomyRejectedException("Seed item instance IDs must be unique.");
        }
        ValidateFile(created, accountId);
        Persist(path, created);
        return created;
    }

    private void Persist(string path, EconomyAccountFile file)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(file, Json);
        if (bytes.Length > MaximumFileBytes) throw new InvalidDataException("Account file exceeds the size limit.");
        _persistenceFault?.Invoke(EconomyPersistenceStage.BeforeWrite);
        string temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        // Failed temporary files are retained as recovery evidence, never deleted.
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        _persistenceFault?.Invoke(EconomyPersistenceStage.AfterFlushBeforeReplace);
        if (File.Exists(path)) ReplaceCommittedFile(temporary, path);
        else File.Move(temporary, path);
        // No fallible logging, callback or additional disk operation occurs after the commit.
    }

    private static void ReplaceCommittedFile(string temporary, string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
                return;
            }
            catch (IOException exception) when (OperatingSystem.IsWindows() && attempt < 4
                && (exception.HResult & 0xffff) is 32 or 33 or 1175)
            {
                // A brief reader without delete sharing can block a newly created account's
                // next admission write. Retry the same flushed draft, never the mutation.
                // ReplaceFile guarantees both names survive errors 32/33/1175. Do not retry
                // 1176/1177: those failures can leave the files renamed or the old name absent.
                // All other/permanent failures retain the pending file and fail closed above.
                Thread.Sleep(25 * (attempt + 1)); // At most 250 ms across four retries.
            }
        }
    }

    private AccountEconomyStoreException StorageException(Exception exception)
    {
        string message = "Account economy storage is unavailable or invalid; no fresh balance was substituted.";
        try { _log(message + " " + exception.Message); } catch { /* Logging must not change transaction semantics. */ }
        return new AccountEconomyStoreException(message, exception);
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException
            or AccountEconomyStoreException;

    private static AccountEconomyResult Failure(string reason, EconomyAccountFile? file) =>
        new(false, false, reason, file is null ? null : new AccountEconomySnapshot(file), null);

    internal static void ValidateText(string? value, int maximumLength, string label, bool allowEmpty = false)
    {
        if (value is null || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximumLength)
            throw new EconomyRejectedException($"{label} is missing or exceeds its limit.");
    }

    private static void ValidateAccountId(string accountId) => ValidateText(accountId, 512, "Account ID");

    internal static void ValidateItem(OwnedAccountItem? item)
    {
        if (item is null || item.InstanceId == 0 || item.AccountItemId == 0 || item.Count == 0 || item.Count > MaximumValue)
            throw new EconomyRejectedException("Invalid owned account item.");
        ValidateText(item.Source, 256, "Item acquisition source");
    }

    private static void ValidateFile(EconomyAccountFile file, string accountId)
    {
        try
        {
            if (file.FormatVersion != 1 || file.AccountId != accountId || file.Revision < 0
                || file.Balances is null || file.Items is null || file.States is null || file.Receipts is null
                || file.Balances.Count > MaximumCurrencies || file.Items.Count > MaximumItems
                || file.States.Count > MaximumStates || file.Receipts.Count > MaximumReceipts
                || file.Receipts.Count != file.Revision)
                throw new InvalidDataException("Account identity, schema or collection limits are invalid.");
            long textLength = file.AccountId.Length;
            foreach ((uint id, uint balance) in file.Balances)
                if (id == 0 || balance > MaximumValue) throw new InvalidDataException("Invalid saved currency.");
            foreach ((ulong id, OwnedAccountItem item) in file.Items)
            {
                ValidateItem(item);
                textLength += item.Source.Length;
                if (id != item.InstanceId) throw new InvalidDataException("Owned item key does not match its instance.");
            }
            foreach ((string key, string value) in file.States)
            {
                ValidateText(key, 256, "State key");
                ValidateText(value, MaximumPayloadLength, "State value", allowEmpty: true);
                textLength += key.Length + value.Length;
            }
            var receiptRevisions = new HashSet<long>();
            foreach ((string key, EconomyOperationReceipt receipt) in file.Receipts)
            {
                ValidateText(key, 256, "Operation ID");
                if (receipt is null || key != receipt.OperationId || receipt.Revision < 1
                    || receipt.Revision > file.Revision || !receiptRevisions.Add(receipt.Revision))
                    throw new InvalidDataException("Invalid saved operation receipt.");
                ValidateText(receipt.Kind, 128, "Operation kind");
                if (receipt.ResultJson is not null)
                    ValidateText(receipt.ResultJson, MaximumPayloadLength, "Result payload", allowEmpty: true);
                textLength += key.Length + receipt.Kind.Length + (receipt.ResultJson?.Length ?? 0);
            }
            if (textLength > MaximumTextLength) throw new InvalidDataException("Account text payload limit reached.");
        }
        catch (EconomyRejectedException exception)
        {
            throw new InvalidDataException("Invalid saved account values.", exception);
        }
    }
}

using System.Collections.Concurrent;

namespace Cranberry.Harness.Verification;

/// <summary>One row of the client's own <c>ClientItemDefinitions.txt</c>, reduced to what a probe asks.</summary>
public sealed record ClientItemRow(uint Id, string CodeFactory, string Name, uint ItemClass, string ModelName)
{
    /// <summary>
    /// True when the client would build this row through its <c>Weapon</c> item class — the class
    /// whose <c>vtable+0x50</c> reader is the 68-byte tail of docs/58 §5. Taken from the client's
    /// own <c>CODE_FACTORY_NAME</c> column, never from a Cranberry list.
    /// </summary>
    public bool IsWeaponClass => string.Equals(CodeFactory, "Weapon", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The August client's own data, read straight off disk, so a wire assertion can be checked against
/// what the client actually ships rather than against another server-side table.
///
/// <para><b>Why the run lane needs this.</b> docs/54 A3 — the shotgun's ammunition box rendered
/// white because <c>Models.txt</c> row 10137 names <c>Common_Props_AmmoBoxes_Shotgun.adr</c> and
/// <b>no such asset exists in any of the 256 packs</b>. That failure is invisible on the wire (the
/// packet is well formed) and invisible to a unit test (the row exists in the sheet). It is visible
/// here: resolve every model id the server spawns through <c>Models.txt</c> and ask the extracted
/// actor corpus whether the file it names is real. Colour cannot be asserted by a wire harness —
/// but "the client cannot load this actor at all" can.</para>
/// </summary>
public sealed class ClientAssets
{

    private readonly Dictionary<uint, string> _models = [];
    private readonly HashSet<string> _actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, ClientItemRow> _items = [];
    private readonly ConcurrentDictionary<uint, bool> _resolves = new();

    private ClientAssets(string root) => Root = root;

    /// <summary>Where the extracted client data lives; overridable for another machine.</summary>
    public static string DefaultRoot { get; } =
        Environment.GetEnvironmentVariable("CRANBERRY_CLIENT_DATA") ?? @"C:\Aug2017\out\data_aug";

    private static readonly Lazy<ClientAssets> Shared = new(() => Load(DefaultRoot));

    /// <summary>The process-wide instance, loaded once.</summary>
    public static ClientAssets Current => Shared.Value;

    public string Root { get; }

    /// <summary>False when the extracted corpus is missing, which makes a probe NOT-TESTABLE rather than failed.</summary>
    public bool Available => _models.Count > 0 && _actors.Count > 0;

    public int ModelCount => _models.Count;

    public int ActorCount => _actors.Count;

    public int ItemCount => _items.Count;

    public static ClientAssets Load(string root)
    {
        var assets = new ClientAssets(root);
        assets.LoadModels(Path.Combine(root, "Models.txt"));
        assets.LoadActors(Path.Combine(root, "adr"));
        assets.LoadItems(Path.Combine(root, "ClientItemDefinitions.txt"));
        return assets;
    }

    /// <summary><c>Models.txt</c> column MODEL_FILE_NAME for a model id, or null when the sheet has no such row.</summary>
    public string? ModelFile(uint modelId) => _models.TryGetValue(modelId, out string? file) ? file : null;

    /// <summary>The item row, or null.</summary>
    public ClientItemRow? Item(uint definitionId) => _items.TryGetValue(definitionId, out ClientItemRow? row) ? row : null;

    /// <summary>
    /// True when the model id names a row of <c>Models.txt</c> <i>and</i> the actor definition that
    /// row names ships in the client. Anything else is an object the client cannot build.
    /// </summary>
    public bool ModelResolves(uint modelId) => _resolves.GetOrAdd(modelId, id =>
        ModelFile(id) is string file && file.Length > 0 && _actors.Contains(file));

    /// <summary>Why a model id does not resolve, for a failure report.</summary>
    public string Explain(uint modelId) => ModelFile(modelId) switch
    {
        null => $"model {modelId} is not a row of Models.txt",
        "" => $"model {modelId} has an empty MODEL_FILE_NAME",
        string file when !_actors.Contains(file) => $"model {modelId} names '{file}', which ships in no pack",
        string file => $"model {modelId} = '{file}' (resolves)",
    };

    private void LoadModels(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (string[] fields in Rows(path))
        {
            if (fields.Length > 1 && uint.TryParse(fields[0], out uint id))
            {
                _models[id] = fields[1].Trim();
            }
        }
    }

    private void LoadActors(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.adr"))
        {
            _actors.Add(Path.GetFileName(file));
        }
    }

    private void LoadItems(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (string[] fields in Rows(path))
        {
            // ID^CODE_FACTORY_NAME^NAME_ID^DESCRIPTION_ID^IMAGE_SET_ID^ACTIVATABLE_ABILITY_ID^
            // PASSIVE_ABILITY_ID^COST^ITEM_CLASS^...^MODEL_NAME(15)
            if (fields.Length < 15 || !uint.TryParse(fields[0], out uint id))
            {
                continue;
            }

            _ = uint.TryParse(fields[8], out uint itemClass);
            _items[id] = new ClientItemRow(id, fields[1].Trim(), fields[1].Trim(), itemClass, fields[14].Trim());
        }
    }

    /// <summary>The sheets are caret-delimited with a <c>#*</c> header line.</summary>
    private static IEnumerable<string[]> Rows(string path)
    {
        using var reader = new StreamReader(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        while (reader.ReadLine() is string line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            yield return line.Split('^');
        }
    }
}

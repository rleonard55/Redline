using Redline.Core.Interfaces;

namespace Redline.Analysis;

/// <summary>
/// Words the user has accepted. Persisted as UTF-8, one word per line. Thread-safe.
/// Matching is case-insensitive so "Redline" also accepts "redline" at sentence start.
/// </summary>
public sealed class PersonalDictionary : IPersonalDictionary
{
    private readonly object _gate = new();
    private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;

    /// <param name="path">Backing file; null keeps the dictionary in memory only.</param>
    public PersonalDictionary(string? path)
    {
        _path = path;
        if (path is not null && File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var word = line.Trim();
                if (word.Length > 0)
                    _words.Add(word);
            }
        }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Redline", "personal_dictionary.txt");

    public event Action? Changed;

    public IReadOnlyCollection<string> Words
    {
        get { lock (_gate) return _words.ToArray(); }
    }

    public bool Contains(string word)
    {
        lock (_gate) return _words.Contains(word);
    }

    public void Add(string word)
    {
        word = word.Trim();
        if (word.Length == 0 || word.Any(char.IsWhiteSpace))
            throw new ArgumentException("Dictionary entries must be a single non-empty word.", nameof(word));

        lock (_gate)
        {
            if (!_words.Add(word))
                return;
            Save();
        }
        Changed?.Invoke();
    }

    public bool Remove(string word)
    {
        lock (_gate)
        {
            if (!_words.Remove(word.Trim()))
                return false;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Caller holds _gate. Writes to a temp file then swaps, so a crash can't truncate the dictionary.</summary>
    private void Save()
    {
        if (_path is null) return;

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllLines(temp, _words.Order(StringComparer.OrdinalIgnoreCase));
        File.Move(temp, _path, overwrite: true);
    }
}

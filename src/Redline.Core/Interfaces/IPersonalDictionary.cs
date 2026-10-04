namespace Redline.Core.Interfaces;

public interface IPersonalDictionary
{
    bool Contains(string word);
    void Add(string word);
    bool Remove(string word);
    IReadOnlyCollection<string> Words { get; }

    /// <summary>Adds the words that aren't in the dictionary yet; returns how many were added.</summary>
    int AddRange(IEnumerable<string> words)
    {
        int added = 0;
        foreach (var word in words)
        {
            if (Contains(word)) continue;
            Add(word);
            added++;
        }
        return added;
    }

    /// <summary>Raised after a word is added or removed.</summary>
    event Action? Changed;
}

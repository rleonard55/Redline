namespace Redline.Core.Interfaces;

public interface IPersonalDictionary
{
    bool Contains(string word);
    void Add(string word);
    bool Remove(string word);
    IReadOnlyCollection<string> Words { get; }
}

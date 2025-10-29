// Services/TokenCache.cs
using System.Collections.Concurrent;

namespace pdf_extractor.Services;

public sealed class TokenCache
{
    private readonly ConcurrentDictionary<string, string> _tokens = new();
    public bool TryGet(string userName, out string token) => _tokens.TryGetValue(userName, out token!);
    public void Set(string userName, string token) => _tokens[userName] = token;
    public void Clear(string userName) => _tokens.TryRemove(userName, out _);
}
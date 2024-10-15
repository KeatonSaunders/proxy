using System.Collections.Concurrent;

namespace Proxy
{
    public class HttpCache
    {
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

        public HttpMessage GetCachedResponse(HttpMessage request)
        {
            string cacheKey = GenerateCacheKey(request);
            if (_cache.TryGetValue(cacheKey, out CacheEntry entry))
            {
                if (entry.ExpirationTime > DateTime.UtcNow)
                {
                    return entry.Response;
                }
                else
                {
                    _cache.TryRemove(cacheKey, out _);
                }
            }
            return null;
        }

        public void CacheResponse(HttpMessage request, HttpMessage response)
        {
            if (request.Method != "GET" || response.StatusCode != 200)
                return;

            string cacheKey = GenerateCacheKey(request);
            int maxAge = GetMaxAge(response);

            if (maxAge > 0)
            {
                var entry = new CacheEntry
                {
                    Response = response,
                    ExpirationTime = DateTime.UtcNow.AddSeconds(maxAge)
                };
                _cache[cacheKey] = entry;
            }
        }

        private string GenerateCacheKey(HttpMessage request)
        {
            return $"{request.Method}:{request.Uri}";
        }


        private int GetMaxAge(HttpMessage response)
        {
            if (response.Headers.TryGetValue("cache-control", out string cacheControl))
            {
                var directives = cacheControl.Split(',').Select(d => d.Trim().ToLower());
                foreach (var directive in directives)
                {
                    if (directive.StartsWith("max-age="))
                    {
                        if (int.TryParse(directive.Substring(8), out int maxAge))
                        {
                            return maxAge;
                        }
                    }
                }
            }
            return 0;
        }

        private class CacheEntry
        {
            public HttpMessage Response { get; set; }
            public DateTime ExpirationTime { get; set; }
        }
    }
}

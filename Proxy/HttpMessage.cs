using System.IO.Compression;
using System.Text;

namespace Proxy
{
    public class HttpMessage
    {
        public HttpMessageType Type { get; private set; }
        public HttpState State { get; private set; }
        public Dictionary<string, string> Headers { get; private set; }
        public string Method { get; private set; }
        public string Uri { get; private set; }
        public string Version { get; private set; }
        public int StatusCode { get; private set; }
        public string StatusMessage { get; private set; }
        public int ContentLength { get; private set; }
        public byte[] Body { get; private set; }

        private byte[] _residual;
        private byte[] _startLine;

        public HttpMessage(HttpMessageType type)
        {
            Type = type;
            Headers = new();
            _residual = Array.Empty<byte>();
            State = HttpState.START;
            Body = Array.Empty<byte>();
        }

        public void Parse(ReadOnlySpan<byte> data)
        {
            ReadOnlySpan<byte> buffer = CombineBytes(_residual, data);

            int position = 0;
            if (State == HttpState.START)
            {
                if (!TryParseRequestLine(buffer, ref position))
                {
                    _residual = buffer[position..].ToArray();
                    return;
                }
            }
            if (State == HttpState.HEADERS)
            {
                if (!TryParseHeaders(buffer, ref position))
                {
                    _residual = buffer[position..].ToArray();
                    return;
                }
            }
            if (State == HttpState.BODY)
            {
                Body = CombineBytes(Body, buffer[position..]);

                if (ContentLength == 0 || Body.Length >= ContentLength)
                    State = HttpState.END;
            }

            _residual = Array.Empty<byte>();
        }

        private bool TryParseRequestLine(ReadOnlySpan<byte> buffer, ref int position)
        {
            int lineEnd = buffer[position..].IndexOf((byte)'\n');
            if (lineEnd == -1)
                return false;

            ReadOnlySpan<byte> line = buffer.Slice(position, lineEnd).TrimEnd((byte)'\r');
            position += lineEnd + 1;

            int firstSpace = line.IndexOf((byte)' ');
            if (firstSpace == -1)
                return false;

            int secondSpace = line[(firstSpace + 1)..].IndexOf((byte)' ');
            if (secondSpace == -1)
                return false;
            secondSpace += firstSpace + 1;

            if (Type == HttpMessageType.Request)
            {
                Method = Encoding.ASCII.GetString(line[..firstSpace]);
                Uri = Encoding.ASCII.GetString(line[(firstSpace + 1)..secondSpace]);
                Version = Encoding.ASCII.GetString(line[(secondSpace + 1)..]);
            }
            else
            {
                Version = Encoding.ASCII.GetString(line[..firstSpace]);
                if (!int.TryParse(line.Slice(firstSpace + 1, secondSpace - firstSpace - 1), out int statusCode))
                    return false;
                StatusCode = statusCode;
                StatusMessage = Encoding.ASCII.GetString(line[(secondSpace + 1)..]);
            }

            _startLine = line.ToArray();
            State = HttpState.HEADERS;
            return true;
        }

        private bool TryParseHeaders(ReadOnlySpan<byte> buffer, ref int position)
        {
            while (true)
            {
                int lineEnd = buffer[position..].IndexOf((byte)'\n');
                if (lineEnd == -1)
                    return false;

                if (lineEnd == 0 || (lineEnd == 1 && buffer[position] == '\r'))
                {
                    position += lineEnd + 1;
                    State = Method == "GET" ? HttpState.END : HttpState.BODY;
                    return true;
                }

                ReadOnlySpan<byte> line = buffer.Slice(position, lineEnd).TrimEnd((byte)'\r');
                position += lineEnd + 1;

                int colonIndex = line.IndexOf((byte)':');
                if (colonIndex == -1)
                    return false;

                string key = Encoding.ASCII.GetString(line[..colonIndex]).Trim().ToLowerInvariant();
                string value = Encoding.ASCII.GetString(line[(colonIndex + 1)..]).Trim();

                Headers[key] = value;

                if (key == "content-length")
                {
                    if (int.TryParse(value, out int contentLength))
                        ContentLength = contentLength;
                }
            }
        }

        private byte[] CombineBytes(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            byte[] result = new byte[first.Length + second.Length];
            var resultSpan = result.AsSpan();
            first.CopyTo(resultSpan);
            second.CopyTo(resultSpan.Slice(first.Length));
            return result;
        }

        public bool KeepAlive()
        {
            Headers.TryGetValue("connection", out string? connection);

            if (Version == "HTTP/1.0")
            {
                return connection != null && connection.ToLowerInvariant() == "keep-alive";
            }
            if (Version == "HTTP/1.1")
            {
                return !(connection != null && connection.ToLowerInvariant() == "close");
            }
            return false;
        }

        public void AddHeader(string headerName, string headerValue)
        {
            Headers[headerName] = headerValue;
        }

        public byte[] GetBytes(bool compress = false)
        {
            if (compress)
                TryGzip();

            int totalSize = _startLine.Length + 2;
            foreach (var kvp in Headers)
                totalSize += kvp.Key.Length + 2 + kvp.Value.Length + 2;

            totalSize += 2 + Body.Length;
            byte[] result = new byte[totalSize];

            int position = 0;
            _startLine.CopyTo(result.AsSpan(position));
            position += _startLine.Length;

            foreach (var kvp in Headers)
            {
                position += Encoding.ASCII.GetBytes(kvp.Key, result.AsSpan(position));
                result[position++] = (byte)':';
                result[position++] = (byte)' ';
                position += Encoding.ASCII.GetBytes(kvp.Value, result.AsSpan(position));
                result[position++] = (byte)'\r';
                result[position++] = (byte)'\n';
            }

            result[position++] = (byte)'\r';
            result[position++] = (byte)'\n';
            Body.CopyTo(result.AsSpan(position));
            return result;
        }

        public void TryGzip()
        {
            if (!Headers.TryGetValue("accept-encoding", out string acceptEncoding))
                return;

            if (!acceptEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase))
                return;

            if (!Headers.TryGetValue("content-type", out string contentType))
                return;

            string[] compressibleTypes = new[] { "text/", "application/javascript", "application/json", "application/xml", "application/x-www-form-urlencoded" };

            if (!compressibleTypes.Any(type => contentType.StartsWith(type, StringComparison.OrdinalIgnoreCase)))
                return;

            using (var memoryStream = new MemoryStream())
            {
                using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal))
                {
                    gzipStream.Write(Body, 0, Body.Length);
                }
                Body = memoryStream.ToArray();
            }

            Headers["Content-Encoding"] = "gzip";
            Headers["Content-Length"] = Body.Length.ToString();
            Headers.Remove("Transfer-Encoding");
        }
    }

    public enum HttpState
    {
        START,
        HEADERS,
        BODY,
        END
    }

    public enum HttpMessageType
    {
        Request,
        Response
    }
}

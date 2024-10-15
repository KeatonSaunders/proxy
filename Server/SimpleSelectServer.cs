using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Server
{
    public class SimpleSelectServer
    {
        private TcpListener _listener;
        private string _rootPath;
        private List<Socket> _clientSockets = new();
        private byte[] _buffer = new byte[4096];
        private const int MAX_CONNECTIONS = 100;

        // For testing:
        public long RequestCount = 0;

        public SimpleSelectServer(int port)
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _rootPath = Utils.GetProjectDirectory();
        }

        public void Start()
        {
            _listener.Start();
            Console.WriteLine($"Listening for connections on port {((IPEndPoint)_listener.LocalEndpoint).Port}");
            Console.WriteLine($"Serving files from {_rootPath}");

            while (true)
            {
                List<Socket> readSockets = new List<Socket>(_clientSockets);
                readSockets.Add(_listener.Server);

                List<Socket> writeSockets = new List<Socket>(_clientSockets);
                List<Socket> errorSockets = new List<Socket>(_clientSockets);

                Socket.Select(readSockets, writeSockets, errorSockets, 1000000);

                foreach (Socket socket in readSockets)
                {
                    if (socket == _listener.Server)
                    {
                        AcceptNewClient();
                    }
                    else
                    {
                        HandleClientRequest(socket);
                    }
                }

                foreach (Socket socket in errorSockets)
                {
                    CloseClientSocket(socket);
                }
            }
        }

        private void AcceptNewClient()
        {
            try
            {
                Socket clientSocket = _listener.AcceptSocket();
                if (_clientSockets.Count < MAX_CONNECTIONS)
                {
                    _clientSockets.Add(clientSocket);
                    Console.WriteLine($"New client connected: {clientSocket.RemoteEndPoint}");
                }
                else
                {
                    Console.WriteLine($"Max connections reached. Rejecting new connection from {clientSocket.RemoteEndPoint}");
                    clientSocket.Close();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error accepting new connection: {e.Message}");
            }
        }

        private void HandleClientRequest(Socket socket)
        {
            try
            {
                int bytesRead = socket.Receive(_buffer);
                if (bytesRead == 0)
                {
                    CloseClientSocket(socket);
                    return;
                }

                RequestCount++;
                string request = Encoding.ASCII.GetString(_buffer, 0, bytesRead);
                string[] requestLines = request.Split('\n');
                string[] requestParts = requestLines[0].Split(' ');

                if (requestParts.Length < 2)
                {
                    SendResponse(socket, "400 Bad Request", "text/plain", "Bad Request");
                    return;
                }

                string method = requestParts[0];
                string path = requestParts[1].TrimStart('/');
                string requestId = ExtractRequestId(requestLines);

                if (string.IsNullOrEmpty(path))
                {
                    path = "index.html";
                }

                string filePath = Path.Combine(_rootPath, path);

                if (File.Exists(filePath))
                {
                    SendFile(socket, filePath, requestId);
                }
                else
                {
                    SendResponse(socket, "404 Not Found", "text/html", "<HTML><BODY><H1>404 Not Found</H1></BODY></HTML>");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling client: {ex.Message}");
                CloseClientSocket(socket);
            }
        }

        private string ExtractRequestId(string[] requestLines)
        {
            foreach (var line in requestLines)
            {
                if (line.StartsWith("request-id:", StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring("request-id:".Length).Trim();
                }
            }
            return null;
        }

        private void SendFile(Socket socket, string filePath, string requestId)
        {
            int maxAgeSeconds = GetMaxAgeForFile(filePath);
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            string contentType = GetContentType(extension);
            byte[] content = File.ReadAllBytes(filePath);

            string headers = $"HTTP/1.1 200 OK\r\n" +
                             $"Content-Type: {contentType}\r\n" +
                             $"Content-Length: {content.Length}\r\n" +
                             $"Cache-Control: max-age={maxAgeSeconds}\r\n";

            if (!string.IsNullOrEmpty(requestId))
            {
                headers += $"Request-Id: {requestId}\r\n";
            }

            headers += "\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            socket.Send(headerBytes);
            socket.Send(content);
        }

        private void SendResponse(Socket socket, string status, string contentType, string content, int maxAgeSeconds = 3600, string requestId = null)
        {
            byte[] contentBytes = Encoding.UTF8.GetBytes(content);
            string headers = $"HTTP/1.1 {status}\r\n" +
                             $"Content-Type: {contentType}\r\n" +
                             $"Content-Length: {contentBytes.Length}\r\n" +
                             $"Cache-Control: max-age={maxAgeSeconds}\r\n";

            if (!string.IsNullOrEmpty(requestId))
            {
                headers += $"Request-Id: {requestId}\r\n";
            }

            headers += "\r\n";

            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            socket.Send(headerBytes);
            socket.Send(contentBytes);
        }

        private string GetContentType(string extension)
        {
            return extension switch
            {
                ".html" => "text/html",
                ".css" => "text/css",
                ".jpeg" => "image/jpeg",
                ".jpg" => "image/jpeg",
                ".png" => "image/png",
                _ => "application/octet-stream",
            };
        }

        private int GetMaxAgeForFile(string filePath)
        {
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension switch
            {
                ".html" => 3600,        // 1 hour
                ".css" => 2592000,      // 30 days
                ".jpeg" => 2592000,     // 30 days
                ".jpg" => 2592000,      // 30 days
                ".png" => 2592000,      // 30 days
                ".pdf" => 86400,        // 1 day
                _ => 0,                 // No caching for other file types
            };
        }

        private void CloseClientSocket(Socket socket)
        {
            Console.WriteLine($"Client disconnected: {socket.RemoteEndPoint}");
            _clientSockets.Remove(socket);
            socket.Close();
        }

        public void Stop()
        {
            foreach (Socket socket in _clientSockets)
            {
                socket.Close();
            }
            _listener.Stop();
        }
    }
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Proxy
{
    public class HttpProxy
    {
        protected const int BufferSize = 4096;
        private readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        private const string Error505 = "HTTP/1.1 500 Internal Server Error\r\n\r\n";
        private readonly string _serverHost;
        private readonly int _serverPort;
        private readonly int _maxConnections;
        private readonly HttpCache _cache = new();
        private readonly Dictionary<string, HttpMessage> _requestCache = new();

        protected readonly List<Socket> _allSockets = new();
        protected readonly Dictionary<Socket, HttpMessage> _requestForClient = new();
        protected readonly Dictionary<Socket, Queue<byte[]>> _writeQueues = new();
        protected readonly Dictionary<Socket, Socket> _clientForUpstreamConnection = new();

        protected readonly ConcurrentQueue<Socket> _availableConnections = new();
        protected readonly ConcurrentDictionary<Socket, byte[]> _upstreamBuffers = new();
        protected readonly Dictionary<Socket, HttpMessage> _responseForUpstream = new();
        protected readonly Queue<(byte[] data, Socket clientSocket)> _pendingRequests = new();

        public HttpProxy(string serverHost, int serverPort, int maxConnections = 5)
        {
            _serverHost = serverHost;
            _serverPort = serverPort;
            _maxConnections = maxConnections;
        }

        public void Run(string listenHost, int listenPort)
        {
            InitializeConnectionPool();

            using Socket listenSocket = CreateListenSocket(listenHost, listenPort);

            while (true)
            {
                var readableSockets = new List<Socket>(_allSockets);
                var writableSockets = new List<Socket>(_allSockets.Where(s => _writeQueues.ContainsKey(s) && _writeQueues[s].Count > 0));
                Socket.Select(readableSockets, writableSockets, null, 1000000);

                foreach (var socket in readableSockets)
                {
                    if (socket == listenSocket)
                    {
                        AcceptNewConnection(listenSocket);
                    }
                    else if (_upstreamBuffers.ContainsKey(socket))
                    {
                        HandleUpstream(socket);
                    }
                    else
                    {
                        HandleClient(socket);
                    }
                }
                foreach (var socket in writableSockets)
                {
                    HandleWrite(socket);
                }
            }
        }

        public virtual void InitializeConnectionPool()
        {
            for (int i = 0; i < _maxConnections; i++)
            {
                var upstreamSocket = CreateUpstreamSocket();
                _availableConnections.Enqueue(upstreamSocket);
            }
        }

        private void HandleClient(Socket clientSocket)
        {
            if (!_requestForClient.TryGetValue(clientSocket, out var httpRequest))
            {
                httpRequest = new HttpMessage(HttpMessageType.Request);
                _requestForClient[clientSocket] = httpRequest;
            }

            byte[] buffer = _arrayPool.Rent(BufferSize);
            try
            {
                int bytesRead = clientSocket.Receive(buffer);
                if (bytesRead == 0)
                {
                    CloseConnection(clientSocket);
                    return;
                }

                Console.WriteLine($"Received {bytesRead}B from {clientSocket.RemoteEndPoint}");
                httpRequest.Parse(buffer.AsSpan(0, bytesRead));

                if (httpRequest.State == HttpState.END)
                {
                    HttpMessage? cachedResponse = _cache.GetCachedResponse(httpRequest);
                    if (cachedResponse != null)
                    {
                        Console.WriteLine($"Cache hit for {httpRequest.Uri}");
                        SendCachedResponse(clientSocket, cachedResponse);
                    }
                    else
                    {
                        Console.WriteLine($"Cache miss for {httpRequest.Uri}");
                        string requestId = Guid.NewGuid().ToString();
                        httpRequest.AddHeader("request-id", requestId );
                        _requestCache.Add(requestId, httpRequest);
                        SendToUpstream(httpRequest.GetBytes(), clientSocket);
                    }

                    _requestForClient.Remove(clientSocket);

                    if (!httpRequest.KeepAlive() && _writeQueues[clientSocket].Count == 0)
                        CloseConnection(clientSocket);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Exception handling client: " + ex.Message);
                try { _writeQueues[clientSocket].Enqueue(Encoding.ASCII.GetBytes(Error505)); } catch { }
                try { CloseConnection(clientSocket); } catch { }
            }
            finally
            {
                _arrayPool.Return(buffer);
            }
        }

        private void SendCachedResponse(Socket clientSocket, HttpMessage cachedResponse)
        {
            byte[] responseBytes = cachedResponse.GetBytes();
            _writeQueues[clientSocket].Enqueue(responseBytes);
            Console.WriteLine($"Queued {responseBytes.Length}B cached response to send to {clientSocket.RemoteEndPoint}");
        }

        public virtual void SendToUpstream(byte[] data, Socket clientSocket)
        {
            if (_availableConnections.TryDequeue(out var upstreamSocket))
            {
                SendRequestToUpstream(data, clientSocket, upstreamSocket);
                UpdateMaxConcurrentConnections();
            }
            else
            {
                Console.WriteLine($"No available connections. Queueing request from {clientSocket.RemoteEndPoint}");
                _pendingRequests.Enqueue((data, clientSocket));
            }
        }

        protected virtual void SendRequestToUpstream(byte[] data, Socket clientSocket, Socket upstreamSocket)
        {
            _writeQueues[upstreamSocket].Enqueue(data);
            _clientForUpstreamConnection[upstreamSocket] = clientSocket;
            Console.WriteLine($"Queued {data.Length}B from {clientSocket.RemoteEndPoint} to send to {upstreamSocket.RemoteEndPoint}");
        }

        private void HandleUpstream(Socket upstreamSocket)
        {
            try
            {
                byte[] buffer = _upstreamBuffers[upstreamSocket];
                int bytesRead = upstreamSocket.Receive(buffer);
                if (bytesRead > 0)
                {
                    Console.WriteLine($"Received {bytesRead}B from upstream {upstreamSocket.RemoteEndPoint}");

                    if (!_responseForUpstream.TryGetValue(upstreamSocket, out var response))
                    {
                        response = new HttpMessage(HttpMessageType.Response);
                        _responseForUpstream[upstreamSocket] = response;
                    }

                    response.Parse(buffer.AsSpan(0, bytesRead));

                    if (_clientForUpstreamConnection.TryGetValue(upstreamSocket, out var clientSocket))
                    {
                        _writeQueues[clientSocket].Enqueue(buffer.Take(bytesRead).ToArray());
                        Console.WriteLine($"Queued {bytesRead}B to send to {clientSocket.RemoteEndPoint}");
                    }
                    else
                    {
                        Console.WriteLine($"Could not find client for upstream. This is bad!");
                    }

                    if (response.State == HttpState.END)
                    {
                        ReturnConnectionToPool(upstreamSocket);

                        if (response.Headers.TryGetValue("request-id", out var id))
                        {
                            if(_requestCache.TryGetValue(id, out var request))
                            {
                                _cache.CacheResponse(request, response);
                                _requestCache.Remove(id);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception handling upstream {upstreamSocket.RemoteEndPoint}: " + ex.Message);
                RecreateUpstreamSocket(upstreamSocket);
            }
        }

        public virtual void ReturnConnectionToPool(Socket upstreamSocket)
        {
            _responseForUpstream.Remove(upstreamSocket);
            _clientForUpstreamConnection.Remove(upstreamSocket);

            if (_pendingRequests.Count > 0)
            {
                var (data, clientSocket) = _pendingRequests.Dequeue();
                SendRequestToUpstream(data, clientSocket, upstreamSocket);
                UpdateMaxConcurrentConnections();
            }
            else
            {
                _availableConnections.Enqueue(upstreamSocket);
                Console.WriteLine($"Returned connection {upstreamSocket.RemoteEndPoint} to pool");
            }
        }

        private void HandleWrite(Socket socket)
        {
            if (_writeQueues.TryGetValue(socket, out var queue) && queue.Count > 0)
            {
                byte[] data = queue.Peek();
                try
                {
                    int bytesSent = socket.Send(data);
                    Console.WriteLine($"Sent {bytesSent}B to {socket.RemoteEndPoint}");

                    if (bytesSent == data.Length)
                    {
                        queue.Dequeue();
                    }
                    else if (bytesSent > 0)
                    {
                        queue.Enqueue(data.AsSpan(bytesSent).ToArray());
                        queue.Dequeue();
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode != SocketError.WouldBlock)
                    {
                        Console.WriteLine($"Error sending data to {socket.RemoteEndPoint}: {ex.Message}");
                        if (_upstreamBuffers.ContainsKey(socket))
                        {
                            RecreateUpstreamSocket(socket);
                        }
                        else
                        {
                            CloseConnection(socket);
                        }
                    }
                }
            }
        }

        protected virtual Socket CreateUpstreamSocket()
        {
            var upstreamSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            upstreamSocket.Connect(_serverHost, _serverPort);
            upstreamSocket.Blocking = false;
            Console.WriteLine($"Created new upstream connection to {upstreamSocket.RemoteEndPoint}");
            _allSockets.Add(upstreamSocket);
            _writeQueues[upstreamSocket] = new Queue<byte[]>();
            _upstreamBuffers[upstreamSocket] = new byte[BufferSize];
            return upstreamSocket;
        }

        private Socket CreateListenSocket(string listenHost, int listenPort)
        {
            var listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            var listenEndpoint = new IPEndPoint(IPAddress.Parse(listenHost), listenPort);
            listenSocket.Bind(listenEndpoint);
            listenSocket.Listen(10);
            _allSockets.Add(listenSocket);
            Console.WriteLine($"Accepting new connections on {listenEndpoint}");
            return listenSocket;
        }

        private void AcceptNewConnection(Socket listenSocket)
        {
            var clientSocket = listenSocket.Accept();
            clientSocket.Blocking = false;
            Console.WriteLine($"New connection from {clientSocket.RemoteEndPoint}");
            _allSockets.Add(clientSocket);
            _writeQueues[clientSocket] = new Queue<byte[]>();
        }

        private void CloseConnection(Socket socket)
        {
            Console.WriteLine($"Closing connection with {socket.RemoteEndPoint}");
            socket.Shutdown(SocketShutdown.Both);
            socket.Close();
            _allSockets.Remove(socket);
            _writeQueues.Remove(socket);
            _requestForClient.Remove(socket);

            // Won't be enqueued to _availableConnections again
            _upstreamBuffers.TryRemove(socket, out _);
        }

        private void RecreateUpstreamSocket(Socket oldSocket)
        {
            CloseConnection(oldSocket);
            Socket newSocket = CreateUpstreamSocket();
            _availableConnections.Enqueue(newSocket);

            // Transfer any pending writes from the old socket to the new one
            if (_writeQueues.TryGetValue(oldSocket, out Queue<byte[]> pendingWrites))
            {
                _writeQueues[newSocket] = pendingWrites;
            }

            Console.WriteLine($"Recreated upstream socket {newSocket.RemoteEndPoint}");
        }

        // For testing:
        public virtual int PendingRequestsCount => _pendingRequests.Count;
        public virtual int AvailableConnectionsCount => _availableConnections.Count;
        public virtual int MaxConcurrentConnectionsUsed { get; protected set; } = 0;

        protected virtual void UpdateMaxConcurrentConnections()
        {
            int currentConnections = _maxConnections - _availableConnections.Count;
            if (currentConnections > MaxConcurrentConnectionsUsed)
            {
                MaxConcurrentConnectionsUsed = currentConnections;
            }
        }
    }
}

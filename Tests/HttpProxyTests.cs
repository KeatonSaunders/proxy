using Moq;
using Proxy;
using Server;
using System.Net.Sockets;

namespace ProxyTests;

[TestFixture]
public class HttpProxyTests
{
    private string _host = "127.0.0.1";
    private int _serverPort = 9000;

    private class TestableHttpProxy : HttpProxy
    {
        public TestableHttpProxy(string serverHost, int serverPort, int maxConnections) : base(serverHost, serverPort, maxConnections) { }

        protected override Socket CreateUpstreamSocket()
        {
            var mockSocket = new Mock<Socket>(SocketType.Stream, ProtocolType.Tcp);
            _allSockets.Add(mockSocket.Object);
            _writeQueues[mockSocket.Object] = new Queue<byte[]>();
            _upstreamBuffers[mockSocket.Object] = new byte[BufferSize];
            return mockSocket.Object;
        }

        public virtual Socket GetUpstreamConnection()
        {
            return _upstreamBuffers.Keys.FirstOrDefault();
        }
    }

    [Test]
    public void SendToUpstream_QueuesPendingRequestsWhenNoConnectionsAvailable()
    {
        // Arrange
        var proxy = new TestableHttpProxy(_host, _serverPort, maxConnections: 1);
        proxy.InitializeConnectionPool();
        var mockClientSocket = new Mock<Socket>(SocketType.Stream, ProtocolType.Tcp);
        byte[] testData = new byte[] { 1, 2, 3, 4 };

        // Act
        proxy.SendToUpstream(testData, mockClientSocket.Object);
        proxy.SendToUpstream(testData, mockClientSocket.Object);

        // Assert
        Assert.That(proxy.PendingRequestsCount, Is.EqualTo(1));
        Assert.That(proxy.AvailableConnectionsCount, Is.EqualTo(0));
    }

    [Test]
    public void ReturnConnectionToPool_ProcessesPendingRequestBeforeReturningToPool()
    {
        // Arrange
        var proxy = new TestableHttpProxy(_host, _serverPort, maxConnections: 1);
        proxy.InitializeConnectionPool();
        var mockClientSocket = new Mock<Socket>(SocketType.Stream, ProtocolType.Tcp);
        byte[] testData = new byte[] { 1, 2, 3, 4 };

        // Simulate two requests to ensure one is pending
        proxy.SendToUpstream(testData, mockClientSocket.Object);
        proxy.SendToUpstream(testData, mockClientSocket.Object);

        Socket upstreamSocket = proxy.GetUpstreamConnection();

        // Act
        proxy.ReturnConnectionToPool(upstreamSocket);

        // Assert
        Assert.That(proxy.PendingRequestsCount, Is.EqualTo(0));
        Assert.That(proxy.AvailableConnectionsCount, Is.EqualTo(0)); // The connection should be used for the pending request
    }
}

[TestFixture]
public class HttpProxyIntegrationTests
{
    private HttpProxy _proxy;
    private SimpleSelectServer _server;
    private HttpClient _client;
    private string _host = "127.0.0.1";
    private int _serverPort = 9000;
    private int _proxyPort = 8000;

    [SetUp]
    public async Task Setup()
    {
        _server = new SimpleSelectServer(_serverPort);
        Task.Run(() => _server.Start());

        _proxy = new HttpProxy(_host, _serverPort, maxConnections: 2);
        Task.Run(() => _proxy.Run(_host, _proxyPort));

        _client = new HttpClient();
        _client.BaseAddress = new Uri($"http://{_host}:{_proxyPort}");

        // Give some time for the server and proxy to start
        await Task.Delay(2000);
    }

    [TearDown]
    public void TearDown()
    {
        _server.Stop();
        _client.Dispose();
    }

    [Test]
    public async Task MultipleSimultaneousRequests_AreProcessedCorrectly()
    {
        // Arrange
        var tasks = new List<Task<HttpResponseMessage>>();

        // Act
        for (int i = 0; i < 5; i++) // Send 5 requests, but we only have 2 connections
        {
            tasks.Add(_client.GetAsync($"/styles.css"));
        }

        await Task.WhenAll(tasks);

        // Assert
        Assert.That(tasks, Has.All.Matches<Task<HttpResponseMessage>>(t => t.Result.IsSuccessStatusCode));
        Assert.That(_proxy.MaxConcurrentConnectionsUsed, Is.LessThanOrEqualTo(2));
    }
}
using Proxy;
using Server;

namespace ProxyTests
{
    [TestFixture]
    public class ProxyTestHarness
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
        [TestCase(1000, 50)]
        public async Task RunLoadTest(int numRequests, int concurrentRequests)
        {
            var tasks = new List<Task<HttpResponseMessage>>();

            for (int i = 0; i < numRequests; i += concurrentRequests)
            {
                var batch = Enumerable.Range(i, Math.Min(concurrentRequests, numRequests - i)).Select(j => _client.GetAsync($"/test?id={j}")).ToList();

                tasks.AddRange(batch);

                if (tasks.Count >= 1000 || i + concurrentRequests >= numRequests)
                {
                    await Task.WhenAll(tasks);
                    tasks.Clear();
                }
            }

            Console.WriteLine($"All {numRequests} requests completed.");
            Console.WriteLine($"Max concurrent connections used: {_proxy.MaxConcurrentConnectionsUsed}");
            Console.WriteLine($"Total requests processed by server: {_server.RequestCount}");

            Assert.That(_server.RequestCount, Is.EqualTo(numRequests));
            Assert.That(_proxy.MaxConcurrentConnectionsUsed, Is.LessThanOrEqualTo(5));
            Assert.That(_proxy.PendingRequestsCount, Is.EqualTo(0));
        }
    }
}

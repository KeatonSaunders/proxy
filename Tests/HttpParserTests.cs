using Proxy;
using System.Text;

namespace HttpParserTests;

[TestFixture]
public class HttpParserTests
{
    [Test]
    public void Parse_CompleteGetRequest_SetsCorrectState()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData = Encoding.ASCII.GetBytes("GET /styles.css HTTP/1.1\r\nHost: example.com\r\n\r\n");

        // Act
        request.Parse(requestData);

        // Assert
        Assert.AreEqual(HttpState.END, request.State);
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual("/styles.css", request.Uri);
        Assert.AreEqual("HTTP/1.1", request.Version);
        Assert.AreEqual(1, request.Headers.Count);
        Assert.AreEqual("example.com", request.Headers["host"]);
    }

    [Test]
    public void Parse_PartialRequestLine_SetsStartState()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData = Encoding.ASCII.GetBytes("GET /styles");

        // Act
        request.Parse(requestData);

        // Assert
        Assert.AreEqual(HttpState.START, request.State);
    }

    [Test]
    public void Parse_CompleteRequestLinePartialHeaders_SetsHeadersState()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData = Encoding.ASCII.GetBytes("GET /styles.css HTTP/1.1\r\nHost: example");

        // Act
        request.Parse(requestData);

        // Assert
        Assert.AreEqual(HttpState.HEADERS, request.State);
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual("/styles.css", request.Uri);
        Assert.AreEqual("HTTP/1.1", request.Version);
        Assert.AreEqual(0, request.Headers.Count);
    }

    [Test]
    public void Parse_PartialRequest_ThenComplete_SetsCorrectState()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData1 = Encoding.ASCII.GetBytes("GET /styles.css HTTP/1.1\r\nHo");
        var requestData2 = Encoding.ASCII.GetBytes("st: example.com\r\n\r\n");

        // Act
        request.Parse(requestData1);
        request.Parse(requestData2);

        // Assert
        Assert.AreEqual(HttpState.END, request.State);
        Assert.AreEqual("GET", request.Method);
        Assert.AreEqual("/styles.css", request.Uri);
        Assert.AreEqual("HTTP/1.1", request.Version);
        Assert.AreEqual(1, request.Headers.Count);
        Assert.AreEqual("example.com", request.Headers["host"]);
    }

    [Test]
    public void Parse_PostRequestWithBody_SetsEndState()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData = Encoding.ASCII.GetBytes(
            "POST /submit HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Content-Length: 11\r\n" +
            "\r\n" +
            "Hello World");

        // Act
        request.Parse(requestData);

        // Assert
        Assert.AreEqual(HttpState.END, request.State);
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/submit", request.Uri);
        Assert.AreEqual("HTTP/1.1", request.Version);
        Assert.AreEqual(2, request.Headers.Count);
        Assert.AreEqual("example.com", request.Headers["host"]);
        Assert.AreEqual("11", request.Headers["content-length"]);
        Assert.AreEqual("Hello World", Encoding.ASCII.GetString(request.Body));
    }

    [Test]
    public void KeepAlive_Http10WithoutKeepAlive_ReturnsFalse()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\n\r\n");

        // Act
        request.Parse(requestData);

        // Assert
        Assert.IsFalse(request.KeepAlive());
    }

    [Test]
    public void KeepAlive_Http11WithoutClose_ReturnsTrue()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n");

        // Act
        request.Parse(requestData);

        // Assert
        Assert.IsTrue(request.KeepAlive());
    }

    [Test]
    public void KeepAlive_Http11WithConnectionClose_ReturnsFalse()
    {
        // Arrange
        var request = new HttpMessage(HttpMessageType.Request);
        var requestData = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: example.com\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        // Act
        request.Parse(requestData);

        // Assert
        Assert.IsFalse(request.KeepAlive());
    }
}
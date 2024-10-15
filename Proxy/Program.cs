using Proxy;

var proxy = new HttpProxy("127.0.0.1", 9000);

proxy.Run("127.0.0.1", 8000);
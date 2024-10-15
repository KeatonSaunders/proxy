A simplified implementation of an http proxy that makes use of IO multiplexing and upstream connection pooling for concurrency.

Purely pedadgogical and not intended for producton use of any kind, where an async / await approach would be preferred.

Handles concurrent persistent connections, various interleavings of operations, and client / upstream disconnects gracefully.

Goal is to better understand how abstractions such as async / await utilize the underlying I/O multiplexing system calls, and create a better mental model of what is happening under the hood when using higher level APIs.

Only supports http 1.x and handles very basic header modification, gziping and caching.

Written as solution to a problem in the CS Primer curriculum.

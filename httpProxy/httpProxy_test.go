package main

import (
	"bytes"
	"github.com/GameXG/ProxyClient"
	"net"
	"os"
	"testing"
	"time"
)

func TestHttpProxy(t *testing.T) {
	httpProxy, err := NewHttpServer(":18048")
	if err != nil {
		t.Fatal(err)
	}
	go httpProxy.ListenAndServe()

	l, err := net.Listen("tcp", ":15365")
	if err != nil {
		panic(err)
	}

	// 执行连接
	go func() {
		p, err := proxyclient.NewProxyClient("http://127.0.0.1:18048")
		if err != nil {
			panic(err)
		}
		c, err := p.Dial("tcp", "127.0.0.1:15365")
		if err != nil {
			panic(err)
		}

		c.SetDeadline(time.Now().Add(3 * time.Second))

		buf := make([]byte, 50)

		if n, err := c.Read(buf); err != nil {
			panic(err)
		} else {
			buf = buf[:n]
		}

		if _, err := c.Write(buf); err != nil {
			panic(err)
		}
	}()

	okChan := make(chan int)

	time.AfterFunc(10*time.Second, func() { okChan <- 0 })

	go func() {
		c, err := l.Accept()
		if err != nil {
			panic(err)
		}

		c.SetDeadline(time.Now().Add(3 * time.Second))

		buf := []byte("xb4wh6awgfdjgffgdttjng3q")

		if _, err := c.Write(buf); err != nil {
			panic(err)
		}

		buf2 := make([]byte, len(buf))

		if _, err := c.Read(buf2); err != nil {
			panic(err)
		}

		if bytes.Compare(buf, buf2) != 0 {
			panic("buf!=buf2")
		}

		okChan <- 1
	}()

	ok := <-okChan

	if ok == 0 {
		t.Fatal("ok==0")
	}
}

func TestHttpsProxy(t *testing.T) {
	if _, err := os.Stat("tls.crt"); err != nil {
		t.Skip("不存在 crt ，跳过 https 单元测试。")
	}

	httpProxy, err := NewHttpServer("")
	if err != nil {
		t.Fatal(err)
	}

	httpsProxy, err := NewTlsServer(":18043", "proxy.com", "", "tls.crt", "tls.key", httpProxy)
	if err != nil {
		panic(err)
	}

	go httpsProxy.ListenAndServe()

	l, err := net.Listen("tcp", ":15364")
	if err != nil {
		panic(err)
	}

	// 执行连接
	go func() {
		p, err := proxyclient.NewHttpProxyClient("https", "127.0.0.1:18043", "proxy.com", "", true, nil, make(map[string][]string))
		if err != nil {
			panic(err)
		}

		c, err := p.Dial("tcp", "127.0.0.1:15364")
		if err != nil {
			panic(err)
		}
		c.SetDeadline(time.Now().Add(3 * time.Second))

		buf := make([]byte, 50)

		if n, err := c.Read(buf); err != nil {
			panic(err)
		} else {
			buf = buf[:n]
		}

		if _, err := c.Write(buf); err != nil {
			panic(err)
		}
	}()

	okChan := make(chan int)

	time.AfterFunc(10*time.Second, func() { okChan <- 0 })

	go func() {
		c, err := l.Accept()
		if err != nil {
			panic(err)
		}
		c.SetDeadline(time.Now().Add(3 * time.Second))

		buf := []byte("xb4wh6awgfdjgffgdttjng3q")

		if _, err := c.Write(buf); err != nil {
			panic(err)
		}

		buf2 := make([]byte, len(buf))

		if _, err := c.Read(buf2); err != nil {
			panic(err)
		}

		if bytes.Compare(buf, buf2) != 0 {
			panic("buf!=buf2")
		}

		okChan <- 1
	}()

	ok := <-okChan

	if ok == 0 {
		t.Fatal("ok==0")
	}
}

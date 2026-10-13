package main

import (
	"bufio"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"
	"time"
)

type httpTestPool struct {
	target string
	calls  chan string
}

func (p *httpTestPool) DialTimeout(network, address string, timeout time.Duration) (net.Conn, UpStreamErrorReporting, error) {
	p.calls <- address
	conn, err := net.DialTimeout(network, p.target, timeout)
	return conn, nil, err
}

func startMixedProxyTest(t *testing.T, pool UpStreamDial) string {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = listener.Close() })
	core := NewServer(listener.Addr().String(), pool)
	go func() {
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			go core.handlerConn(conn)
		}
	}()
	return listener.Addr().String()
}

func TestHTTPProxyUsesTcpRoutePoolAndRemovesProxyHeaders(t *testing.T) {
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Proxy-Authorization") != "" || r.Header.Get("X-Hop") != "" {
			t.Error("hop-by-hop headers reached destination")
		}
		_, _ = io.WriteString(w, "through pool")
	}))
	defer backend.Close()
	target, _ := url.Parse(backend.URL)
	pool := &httpTestPool{target: target.Host, calls: make(chan string, 2)}
	proxyURL, _ := url.Parse("http://" + startMixedProxyTest(t, pool))
	transport := &http.Transport{Proxy: http.ProxyURL(proxyURL), DisableKeepAlives: true}
	defer transport.CloseIdleConnections()
	request, _ := http.NewRequest("GET", "http://forwarded.example/path", nil)
	request.Header.Set("Proxy-Authorization", "Basic secret")
	request.Header.Set("Connection", "X-Hop")
	request.Header.Set("X-Hop", "secret")
	response, err := (&http.Client{Transport: transport, Timeout: 3 * time.Second}).Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(response.Body)
	if string(body) != "through pool" || response.StatusCode != 200 {
		t.Fatalf("body=%s status=%d", body, response.StatusCode)
	}
	if address := <-pool.calls; address != "forwarded.example:80" {
		t.Fatalf("route=%s", address)
	}
}

func TestHTTPConnectPreservesBufferedPayload(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		defer conn.Close()
		_, _ = io.Copy(conn, conn)
	}()
	pool := &httpTestPool{target: listener.Addr().String(), calls: make(chan string, 1)}
	client, err := net.Dial("tcp", startMixedProxyTest(t, pool))
	if err != nil {
		t.Fatal(err)
	}
	defer client.Close()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	_, _ = fmt.Fprint(client, "CONNECT secure.example:443 HTTP/1.1\r\nHost: secure.example:443\r\n\r\nhello")
	reader := bufio.NewReader(client)
	response, err := http.ReadResponse(reader, &http.Request{Method: "CONNECT"})
	if err != nil || response.StatusCode != 200 {
		t.Fatalf("CONNECT error=%v", err)
	}
	var reply [5]byte
	if _, err := io.ReadFull(reader, reply[:]); err != nil || string(reply[:]) != "hello" {
		t.Fatalf("payload=%q error=%v", reply, err)
	}
	if address := <-pool.calls; address != "secure.example:443" {
		t.Fatalf("route=%s", address)
	}
}

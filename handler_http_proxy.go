package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"
)

type httpProxyFactory struct{ upstream UpStreamDial }
type httpProxyConnection struct {
	conn     net.Conn
	upstream UpStreamDial
}

func NewHTTPProxyHandler(upstream UpStreamDial) HandlerNewer {
	return &httpProxyFactory{upstream: upstream}
}
func (f *httpProxyFactory) New(conn net.Conn) (Handler, bool, error) {
	var prefix [8]byte
	n, err := conn.Read(prefix[:])
	if err != nil {
		return nil, true, err
	}
	text := string(prefix[:n])
	for _, method := range []string{"CONNECT", "GET", "HEAD", "POST", "PUT", "DELETE", "OPTIONS", "PATCH"} {
		if strings.HasPrefix(text, method+" ") || (n < len(method)+1 && strings.HasPrefix(method+" ", text)) {
			return &httpProxyConnection{conn: conn, upstream: f.upstream}, true, nil
		}
	}
	return nil, true, NoHandleError("not an HTTP proxy request")
}
func (*httpProxyConnection) String() string { return "HTTP proxy" }

type singleProxyListener struct {
	conn     net.Conn
	accepted bool
	done     chan struct{}
	once     sync.Once
}
type trackedProxyConn struct {
	net.Conn
	listener *singleProxyListener
}

func (c *trackedProxyConn) Close() error { err := c.Conn.Close(); _ = c.listener.Close(); return err }
func (l *singleProxyListener) Accept() (net.Conn, error) {
	if !l.accepted {
		l.accepted = true
		return &trackedProxyConn{Conn: l.conn, listener: l}, nil
	}
	<-l.done
	return nil, net.ErrClosed
}
func (l *singleProxyListener) Close() error   { l.once.Do(func() { close(l.done) }); return nil }
func (l *singleProxyListener) Addr() net.Addr { return l.conn.LocalAddr() }

func (h *httpProxyConnection) Handle() error {
	listener := &singleProxyListener{conn: h.conn, done: make(chan struct{})}
	defer listener.Close()
	transport := &http.Transport{Proxy: nil, DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
		timeout := 15 * time.Second
		if deadline, ok := ctx.Deadline(); ok && time.Until(deadline) < timeout {
			timeout = time.Until(deadline)
		}
		conn, _, err := h.upstream.DialTimeout(network, address, timeout)
		return conn, err
	}, ResponseHeaderTimeout: 30 * time.Second}
	defer transport.CloseIdleConnections()
	server := &http.Server{
		ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 60 * time.Second, MaxHeaderBytes: 32 * 1024,
		ConnState: func(_ net.Conn, state http.ConnState) {
			if state == http.StateClosed {
				_ = listener.Close()
			}
		},
		Handler: http.HandlerFunc(func(w http.ResponseWriter, request *http.Request) {
			if request.Method == http.MethodConnect {
				h.connect(w, request)
				return
			}
			if request.URL.Scheme != "http" || request.URL.Host == "" || request.URL.User != nil {
				http.Error(w, "An absolute HTTP proxy URL is required.", http.StatusBadRequest)
				return
			}
			request.RequestURI = ""
			removeProxyHeaders(request.Header)
			response, err := transport.RoundTrip(request)
			if err != nil {
				http.Error(w, "Upstream connection failed.", http.StatusBadGateway)
				return
			}
			defer response.Body.Close()
			removeProxyHeaders(response.Header)
			for name, values := range response.Header {
				for _, value := range values {
					w.Header().Add(name, value)
				}
			}
			w.WriteHeader(response.StatusCode)
			_, _ = io.Copy(w, response.Body)
		}),
	}
	defer server.Close()
	err := server.Serve(listener)
	if errors.Is(err, net.ErrClosed) || errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

func removeProxyHeaders(header http.Header) {
	for _, value := range header.Values("Connection") {
		for _, name := range strings.Split(value, ",") {
			header.Del(strings.TrimSpace(name))
		}
	}
	for _, name := range []string{"Connection", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate", "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade"} {
		header.Del(name)
	}
}

func (h *httpProxyConnection) connect(w http.ResponseWriter, request *http.Request) {
	if _, _, err := net.SplitHostPort(request.Host); err != nil {
		http.Error(w, "Invalid CONNECT target.", http.StatusBadRequest)
		return
	}
	output, _, err := h.upstream.DialTimeout("tcp", request.Host, 15*time.Second)
	if err != nil {
		http.Error(w, "Upstream connection failed.", http.StatusBadGateway)
		return
	}
	defer output.Close()
	input, buffered, err := w.(http.Hijacker).Hijack()
	if err != nil {
		return
	}
	defer input.Close()
	if _, err := fmt.Fprint(buffered, "HTTP/1.1 200 Connection Established\r\n\r\n"); err != nil {
		return
	}
	if err := buffered.Flush(); err != nil {
		return
	}
	result := make(chan error, 2)
	go func() { _, err := io.Copy(output, buffered); result <- err }()
	go func() { _, err := io.Copy(input, output); result <- err }()
	<-result
}

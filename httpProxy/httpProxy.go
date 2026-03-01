package main

import (
	"bufio"
	"crypto/tls"
	"flag"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/golang/glog"
	"github.com/inconshreveable/go-vhost"
)

/*
http 代理服务器
proxylient 专用，只支持 CCONNECT 命令。
放置到 nginx 前面，只处理 httpsDomain 域名的 CONNECT 请求。未知的域名会转发给 httpsForwardAddr 。
*/

type Flusher interface {
	Flush() error
}

// http服务器
type HttpSrever struct {
	httpAddr string
}

func NewHttpServer(httpAddr string) (s *HttpSrever, err error) {
	return &HttpSrever{httpAddr}, nil
}

type Body struct {
	io.Reader
}

func (b Body) Close() error {
	return nil
}

func (s *HttpSrever) Error(w io.Writer, StatusCode int, body string) {
	r := http.Response{StatusCode: StatusCode, Body: Body{strings.NewReader(body)}}
	r.Write(w)
}

var HTTP_200 = []byte("HTTP/1.1 200 Connection Established\r\n\r\n")

// 处理http请求
func (s *HttpSrever) HandlerHttp(conn net.Conn) {
	defer conn.Close()

	rb := bufio.NewReader(conn)
	r, err := http.ReadRequest(rb)
	if err != nil {
		s.Error(conn, http.StatusBadRequest, "Bad Request")
	}

	if r.Method == "CONNECT" {

		host := r.RequestURI

		remoteConn, err := net.DialTimeout("tcp", host, 5*time.Second)
		if err != nil {
			fmt.Println("Bad Gateway", err)
			s.Error(conn, http.StatusBadGateway, "Bad Gateway")
			return
		}

		// Don't forget to close the connection:
		defer remoteConn.Close()
		conn.Write(HTTP_200)
		if flush, ok := conn.(Flusher); ok == true {
			flush.Flush()
		}

		//TODO： 这里需要设置下超时机制。
		//TODO: 同样需要注意缓存的问题，也就是需要手工实现代码。
		go io.Copy(remoteConn, rb)
		io.Copy(conn, remoteConn)
	} else {
		s.Error(conn, http.StatusBadRequest, "Bad Request")
		return
	}
}

func (s *HttpSrever) ListenAndServe() error {
	l, err := net.Listen("tcp", s.httpAddr)
	if err != nil {
		return err
	}
	for {
		c, err := l.Accept()
		if err != nil {
			if ne, ok := err.(net.Error); ok && ne.Temporary() {
				time.Sleep(5 * time.Millisecond)
				continue
			}
			return err
		}
		go s.HandlerHttp(c)
	}
}

type TlsServer struct {
	httpsAddr, httpsDomain, forwardAddr string
	http                                *HttpSrever
	tlsConfig                           *tls.Config
}

// 处理 tls 请求
func (s *TlsServer) HandlerTls(conn net.Conn) {
	c, err := vhost.TLS(conn)

	if err != nil || c.Host() != s.httpsDomain {
		fmt.Println("err != nil || c.Host() != s.httpsDomain")
		fmt.Println(err)
		fmt.Println(c.Host())
		// 不匹配，直接转发
		defer c.Close()

		remoteConn, err := net.Dial("tcp", s.forwardAddr)
		if err != nil {
			glog.Warning(fmt.Printf("[ERR] dial(\"tcp\",%v):%v", s.forwardAddr, err))
			return
		}
		defer remoteConn.Close()

		go io.Copy(c, remoteConn)
		io.Copy(remoteConn, c)
	} else {
		tlsConn := tls.Server(c, s.tlsConfig)

		err := tlsConn.Handshake()
		if err != nil {
			glog.Warning(err)
			return
		}

		s.http.HandlerHttp(tlsConn)
	}
}

func NewTlsServer(httpsAddr, httpsDomain, forwardAddr, httpsCertFile, httpsKeyFile string, httpServer *HttpSrever) (s *TlsServer, err error) {
	cert, err := tls.LoadX509KeyPair(httpsCertFile, httpsKeyFile)
	if err != nil {
		return nil, err
	}
	tlsConfig := tls.Config{Certificates: []tls.Certificate{cert}}

	s = &TlsServer{httpsAddr: httpsAddr, httpsDomain: httpsDomain, forwardAddr: forwardAddr, http: httpServer, tlsConfig: &tlsConfig}

	return
}

func (s *TlsServer) ListenAndServe() error {
	l, err := net.Listen("tcp", s.httpsAddr)
	if err != nil {
		return err
	}
	for {
		c, err := l.Accept()
		if err != nil {
			if ne, ok := err.(net.Error); ok && ne.Temporary() {
				time.Sleep(5 * time.Millisecond)
				continue
			}
			return err
		}
		go s.HandlerTls(c)
	}
}

func main() {

	//var addr = flag.String("addr",":8080","绑定地址，例子： 0.0.0.0:8080 、:8080")

	httpAddr := flag.String("httpAddr", "", "http 代理绑定地址，例子：0.0.0.0:8080、:8080")
	httpsAddr := flag.String("httpsAddr", "", "https 代理绑定地址，例子：0.0.0.0:443、:443")
	httpsForwardAddr := flag.String("httpsForwardAddr", "", "https 不匹配httpsDomain时转发地址，例子：127.0.0.1:4443")
	httpsDomain := flag.String("httpsDomain", "", "https 代理域名，例子：proxy.com")
	httpsCertFile := flag.String("httpsCertFile", "tls.crt", "https 证书，例子：tls.crt")
	httpsKey := flag.String("httpsKeyFile", "tls.key", "https 证书，例子：tls.key")

	flag.Parse()

	httpServer, err := NewHttpServer(*httpAddr)
	if err != nil {
		panic(err)
	}

	wg := sync.WaitGroup{}

	if *httpAddr != "" {
		wg.Add(1)
		go func() {
			httpServer.ListenAndServe()
			wg.Done()
		}()
	}

	if *httpsAddr != "" {
		wg.Add(1)
		go func() {
			httpsServer, err := NewTlsServer(*httpsAddr, *httpsDomain, *httpsForwardAddr, *httpsCertFile, *httpsKey, httpServer)
			if err != nil {
				panic(err)
			}

			httpsServer.ListenAndServe()
			wg.Done()
		}()
	}

	wg.Wait()
}

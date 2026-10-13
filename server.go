package main

import (
	"io"
	"log"
	"net"
	"time"
)

const (
	// 尝试创建处理器时的conn.read 的timeout
	// 实际是一次性读取数据，所以这个超时指的是客户端必须10秒内发出第一和数据
	handlerNewTimeout = 10 * time.Second

	// 默认一个连接的总处理时间，一般都会被实际的处理器修改掉。
	handlerBaseTimeout = 10 * time.Minute
)

type Server struct {
	Addr        string       // TCP 监听地址
	hNewer      HandlerNewer // 请求处理器
	upStream    UpStreamDial // 上层代理
	ln          net.Listener
	errConn     *ErrConnService //错误连接统计
	OnListening func()
}

func NewServer(addr string, upStream UpStreamDial) *Server {
	srv := Server{}
	srv.Addr = addr

	// 错误连接记录
	srv.errConn = NewErrConnService()

	if upStream == nil {
		// 基本上层代理
		localUpStream, err := NewBaseUpStream(&srv)
		if err != nil {
			panic(err)
		}
		upStream = localUpStream
	}
	srv.upStream = upStream

	// 处理器
	h := NewSwitchHandlerNewer()
	hs := NewSocksHandlerNewer(srv.upStream)
	h.AppendHandlerNewer(hs)
	h.AppendHandlerNewer(NewHTTPProxyHandler(srv.upStream))
	srv.hNewer = h

	return &srv

}

func (srv *Server) ListAndServe() error {
	if srv.Addr == "" {
		srv.Addr = ":7070"
	}

	ln, err := net.Listen("tcp", srv.Addr)
	if err != nil {
		panic(err)
	}
	srv.ln = ln
	if srv.OnListening != nil {
		srv.OnListening()
	}

	return srv.Server()
}

func (srv *Server) Server() error {
	ln := srv.ln
	defer ln.Close()
	var tempDelay time.Duration
	for {
		rw, e := ln.Accept()
		if e != nil {
			if ne, ok := e.(net.Error); ok && ne.Temporary() {
				if tempDelay == 0 {
					tempDelay = 5 * time.Millisecond
				} else {
					tempDelay *= 2
				}
				if max := 1 * time.Second; tempDelay > max {
					tempDelay = max
				}
				log.Printf("Accept error: %v; retrying in %v", e, tempDelay)
				time.Sleep(tempDelay)
				continue
			}
			return e
		}
		tempDelay = 0

		go srv.handlerConn(rw)
	}
}

func (srv *Server) handlerConn(conn net.Conn) {
	defer func() {
		if err := recover(); err != nil {
			log.Printf("work failed: %v", err)
		}
	}()
	// 是这里调用关闭还是 Handler() 负责？
	defer conn.Close()

	if tcpConn, ok := conn.(*net.TCPConn); ok == true {
		// 设置关闭连接时最多等待多少秒
		tcpConn.SetLinger(5)
	}
	conn.SetDeadline(time.Now().Add(handlerNewTimeout))

	h, _, err := srv.hNewer.New(conn)
	if h == nil {
		log.Printf("无法识别请求的协议类型，远端地址：%v，近端地址：%v，详细错误：%v", conn.RemoteAddr(), conn.LocalAddr(), err)
		return
	}

	conn.SetDeadline(time.Now().Add(handlerBaseTimeout))
	if err := h.Handle(); err != nil {
		if err != io.EOF {
			log.Printf("协议处理错误：%v", err)
		}
	}
}

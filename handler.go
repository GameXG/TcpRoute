package main

import (
	"fmt"
	"github.com/inconshreveable/go-vhost"
	"io"
	"net"
	"strconv"
	"strings"
	"sync/atomic"
	"time"
)

const (
	preProtocolUnknown = iota
	preProtocolHTTP
	preProtocolHTTPS
)

var preHttpPorts = []int{80}
var preHttpsPorts = []int{443}

const (
	handlerTimeoutHello   = 10 * time.Second // 握手 timeout 鉴定 + 接受 CMD 的总允许时间
	handlerTimeoutConnect = 10 * time.Second // 连接目标地址超时
	handlerTimeoutForward = 5 * time.Minute  // 转发超时 每次转发数据都会重置这个超时时间
)

// NoHandle 无法处理的协议类型
// 尝试通过 New 对连接创建 Handler 时，如果协议不匹配无法处理，那么就返回这个错误。
type NoHandleError string

func (e NoHandleError) Error() string {
	return fmt.Sprintf("协议无法处理：%v", string(e))
}

type Handler interface {
	String() string
	// Handler 是实际处理请求的函数
	// 注意：如果返回那么连接就会被关闭。
	// 注意：默认设置了10分钟后连接超时，如需修改请自行处理。
	Handle() error
}

// Newer 是创建处理器接口
// 如果处理器识别到当前连接可以处理，那么就会返回创建的处理器，否则返回 nil
type HandlerNewer interface {
	// New 创建处理器
	// 有可能由于协议不正确会创建失败，这时 h==nil , error 可以返回详细信息
	// 在创建处理器失败时调用方负责回退已经从 stream 读取的数据
	// 在创建处理器成功时根据reset的值确定是否复位预读。true 复位预读的数据，flase不复位预读的数据。
	// 注意：函数内部不允许调用会引起副作用的方法，例如 close、 write 等函数 。
	New(conn net.Conn) (h Handler, rePre bool, err error)
}

// CheckPre enables the v0.5.14 DNS-correction path only when a SOCKS client
// supplied an IP address for a configured HTTP or HTTPS port.
func CheckPre(network, address string) int {
	if !strings.HasPrefix(network, "tcp") {
		return preProtocolUnknown
	}
	host, port, err := net.SplitHostPort(address)
	if err != nil || net.ParseIP(host) == nil {
		return preProtocolUnknown
	}
	portNumber, err := strconv.Atoi(port)
	if err != nil {
		return preProtocolUnknown
	}
	if containsPort(preHttpPorts, portNumber) {
		return preProtocolHTTP
	}
	if containsPort(preHttpsPorts, portNumber) {
		return preProtocolHTTPS
	}
	return preProtocolUnknown
}

// Pre reads HTTP Host or TLS SNI from the pre-read connection and restores
// the domain name while preserving the original destination port.
func Pre(conn net.Conn, address string, protocol int) (net.Conn, string, bool) {
	_, port, err := net.SplitHostPort(address)
	if err != nil {
		return conn, address, false
	}
	var rawHost string
	switch protocol {
	case preProtocolHTTP:
		parsed, err := vhost.HTTP(conn)
		if err != nil {
			return parsed, address, false
		}
		conn = parsed
		rawHost = parsed.Host()
		parsed.Free()
	case preProtocolHTTPS:
		parsed, err := vhost.TLS(conn)
		if err != nil {
			return parsed, address, false
		}
		conn = parsed
		rawHost = parsed.Host()
		parsed.Free()
	default:
		return conn, address, false
	}
	if rawHost == "" {
		return conn, address, false
	}
	if host, _, err := net.SplitHostPort(rawHost); err == nil {
		rawHost = host
	}
	return conn, net.JoinHostPort(rawHost, port), true
}

func containsPort(ports []int, target int) bool {
	for _, port := range ports {
		if port == target {
			return true
		}
	}
	return false
}

// 转发计数
// 使用 atomic 实现原子操作
type forwardCount struct {
	send, recv uint64
}

func forwardConn(sConn, oConn net.Conn, timeout time.Duration, count *forwardCount) error {
	errChan := make(chan error, 10)

	go _forwardConn(sConn, oConn, timeout, errChan, &count.send)
	go _forwardConn(oConn, sConn, timeout, errChan, &count.recv)

	return <-errChan
}

func _forwardConn(sConn, oConn net.Conn, timeout time.Duration, errChan chan error, count *uint64) {
	buf := make([]byte, forwardBufSize)
	for {
		sConn.SetDeadline(time.Now().Add(timeout))
		oConn.SetDeadline(time.Now().Add(timeout))
		// 虽然存在 WriteTo 等方法，但是由于无法刷新超时时间，所以还是需要使用标准的 Read、Write。

		if n, err := sConn.Read(buf[:forwardBufSize]); err != nil {
			if err == io.EOF {
				errChan <- err
			} else {
				errChan <- fmt.Errorf("转发读错误：%v", err)
			}
			return
		} else {
			buf = buf[:n]
		}

		wbuf := buf
		for {
			if len(wbuf) == 0 {
				break
			}

			if n, err := oConn.Write(wbuf); err != nil {
				if err == io.EOF {
					errChan <- err
				} else {
					errChan <- fmt.Errorf("转发写错误：%v", err)
				}
				return
			} else {
				wbuf = wbuf[n:]
			}
		}

		// 记录转发计数
		atomic.AddUint64(count, uint64(len(buf)))
	}
}

//go:build windows

package main

import (
	"context"
	"encoding/binary"
	"fmt"
	"net"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"sync/atomic"
	"syscall"
	"time"

	proxyclient "github.com/GameXG/ProxyClient"
	"github.com/GameXG/TcpRoute2/netchan"
	"github.com/xtls/xray-core/transport/internet"
	"golang.org/x/sys/windows"
)

var physicalIndex uint32

// Windows socket options (also used by Xray's sockopt_windows.go).
const unicastInterfaceOption = 31

func initializeWindowsNetwork() error {
	if os.Getenv("TCPROUTE_WINTUN_NAME") == "" {
		return nil
	}
	index, err := strconv.ParseUint(os.Getenv("TCPROUTE_OUTBOUND_INTERFACE"), 10, 32)
	if err != nil || index == 0 {
		return fmt.Errorf("Wintun requires a valid physical interface index")
	}
	physicalIndex = uint32(index)
	dnsServers := strings.Split(os.Getenv("TCPROUTE_BOOTSTRAP_DNS"), ",")
	for i, server := range dnsServers {
		ip, parseErr := netip.ParseAddr(server)
		if parseErr != nil || ip.IsLoopback() || ip.IsUnspecified() || ip.WithZone("") == netip.MustParseAddr("198.18.0.1") {
			return fmt.Errorf("Wintun requires physical-interface DNS servers")
		}
		if ip.Is6() && ip.IsLinkLocalUnicast() && ip.Zone() == "" {
			dnsServers[i] = ip.WithZone(strconv.FormatUint(index, 10)).String()
		}
	}
	netchan.OutboundControl = bindPhysicalSocket
	var next atomic.Uint32
	net.DefaultResolver = &net.Resolver{PreferGo: true, Dial: func(ctx context.Context, network, _ string) (net.Conn, error) {
		server := dnsServers[(next.Add(1)-1)%uint32(len(dnsServers))]
		return netchan.OutboundDialer(0).DialContext(ctx, network, net.JoinHostPort(server, "53"))
	}}
	if err := internet.RegisterDialerController(bindXraySocket); err != nil {
		return err
	}
	return internet.RegisterListenerController(bindXraySocket)
}

func bindXraySocket(network, address string, raw syscall.RawConn) error {
	if err := bindPhysicalSocket(network, address, raw); err != nil {
		// Xray logs controller errors and continues. Stop the core instead of dialing unbound.
		fmt.Fprintln(os.Stderr, "Physical interface binding failed:", err)
		os.Exit(2)
	}
	return nil
}

func bindPhysicalSocket(network, address string, raw syscall.RawConn) error {
	if physicalIndex == 0 {
		return nil
	}
	host, _, _ := net.SplitHostPort(address)
	if ip := net.ParseIP(host); ip != nil && ip.IsLoopback() {
		return nil
	}
	var bindErr error
	err := raw.Control(func(fd uintptr) {
		if strings.HasSuffix(network, "6") {
			bindErr = windows.SetsockoptInt(windows.Handle(fd), windows.IPPROTO_IPV6, unicastInterfaceOption, int(physicalIndex))
			if bindErr == nil && strings.HasPrefix(network, "udp") {
				var bytes [4]byte
				binary.BigEndian.PutUint32(bytes[:], physicalIndex)
				bindErr = windows.SetsockoptInt(windows.Handle(fd), windows.IPPROTO_IP, unicastInterfaceOption, int(binary.LittleEndian.Uint32(bytes[:])))
			}
		} else {
			var bytes [4]byte
			binary.BigEndian.PutUint32(bytes[:], physicalIndex)
			bindErr = windows.SetsockoptInt(windows.Handle(fd), windows.IPPROTO_IP, unicastInterfaceOption, int(binary.LittleEndian.Uint32(bytes[:])))
		}
	})
	if err != nil {
		return err
	}
	return bindErr
}

type boundDirect struct{ proxyclient.ProxyClient }
type boundTCP struct {
	*net.TCPConn
	owner proxyclient.ProxyClient
}

func (c *boundTCP) ProxyClient() proxyclient.ProxyClient { return c.owner }

func (p *boundDirect) Dial(network, address string) (net.Conn, error) {
	return p.DialTimeout(network, address, 0)
}
func (p *boundDirect) DialTimeout(network, address string, timeout time.Duration) (net.Conn, error) {
	dialer := netchan.OutboundDialer(timeout)
	if values := p.GetProxyAddrQuery()["localaddr"]; len(values) > 0 {
		if strings.HasPrefix(network, "udp") {
			local, err := net.ResolveUDPAddr(network, values[0])
			if err != nil {
				return nil, err
			}
			dialer.LocalAddr = local
		} else {
			local, err := net.ResolveTCPAddr(network, values[0])
			if err != nil {
				return nil, err
			}
			dialer.LocalAddr = local
		}
	}
	return dialer.Dial(network, address)
}
func (p *boundDirect) DialTCP(network string, local, remote *net.TCPAddr) (net.Conn, error) {
	dialer := netchan.OutboundDialer(0)
	if local != nil {
		dialer.LocalAddr = local
	}
	return dialer.Dial(network, remote.String())
}
func (p *boundDirect) DialUDP(network string, local, remote *net.UDPAddr) (net.Conn, error) {
	dialer := netchan.OutboundDialer(0)
	if local != nil {
		dialer.LocalAddr = local
	}
	return dialer.Dial(network, remote.String())
}
func (p *boundDirect) DialTCPSAddr(network, address string) (proxyclient.ProxyTCPConn, error) {
	return p.DialTCPSAddrTimeout(network, address, 0)
}
func (p *boundDirect) DialTCPSAddrTimeout(network, address string, timeout time.Duration) (proxyclient.ProxyTCPConn, error) {
	conn, err := p.DialTimeout(network, address, timeout)
	if err != nil {
		return nil, err
	}
	tcp, ok := conn.(*net.TCPConn)
	if !ok {
		_ = conn.Close()
		return nil, fmt.Errorf("expected TCP connection")
	}
	return &boundTCP{TCPConn: tcp, owner: p}, nil
}

func bindUpstream(pc proxyclient.ProxyClient) (proxyclient.ProxyClient, error) {
	if physicalIndex == 0 {
		return pc, nil
	}
	up := pc.UpProxy()
	if up == nil {
		return &boundDirect{ProxyClient: pc}, nil
	}
	bound, err := bindUpstream(up)
	if err != nil {
		return nil, err
	}
	if err := pc.SetUpProxy(bound); err != nil {
		return nil, err
	}
	return pc, nil
}

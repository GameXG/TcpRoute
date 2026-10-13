package main

import (
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"
)

type socksUDPRoute struct {
	conn net.Conn
}

func (h *hSocksHandle) handleSocks5UDPAssociate(control net.Conn) error {
	localTCP, _ := control.LocalAddr().(*net.TCPAddr)
	bindIP := net.IPv4zero
	if localTCP != nil && localTCP.IP != nil {
		bindIP = localTCP.IP
	}
	relay, err := net.ListenUDP("udp", &net.UDPAddr{IP: bindIP})
	if err != nil {
		return fmt.Errorf("创建 UDP relay 失败: %w", err)
	}
	defer relay.Close()

	reply, err := socks5AddressReply(relay.LocalAddr())
	if err != nil {
		return err
	}
	if _, err := control.Write(reply); err != nil {
		return fmt.Errorf("回应 UDP ASSOCIATE 失败: %w", err)
	}
	control.SetDeadline(time.Time{})

	routes := make(map[string]*socksUDPRoute)
	var routesMu sync.Mutex
	defer func() {
		routesMu.Lock()
		defer routesMu.Unlock()
		for _, route := range routes {
			route.conn.Close()
		}
	}()

	done := make(chan struct{})
	go func() {
		_, _ = io.Copy(io.Discard, control)
		close(done)
		_ = relay.Close()
	}()

	var clientAddr *net.UDPAddr
	buffer := make([]byte, 64*1024)
	for {
		n, sender, readErr := relay.ReadFromUDP(buffer)
		if readErr != nil {
			select {
			case <-done:
				return nil
			default:
				return fmt.Errorf("读取 SOCKS UDP 数据失败: %w", readErr)
			}
		}
		if clientAddr == nil {
			clientAddr = sender
		} else if !clientAddr.IP.Equal(sender.IP) || clientAddr.Port != sender.Port {
			continue
		}

		destination, payload, parseErr := parseSocks5UDPDatagram(buffer[:n])
		if parseErr != nil || !udpDestinationAllowed(destination, payload) {
			continue
		}

		routesMu.Lock()
		route := routes[destination]
		routesMu.Unlock()
		if route == nil {
			conn, _, dialErr := h.hSocksServer.upStream.DialTimeout("udp", destination, handlerNewTimeout)
			if dialErr != nil {
				continue
			}
			route = &socksUDPRoute{conn: conn}
			routesMu.Lock()
			routes[destination] = route
			routesMu.Unlock()
			go forwardSocks5UDPResponses(relay, sender, destination, route, func() {
				routesMu.Lock()
				if routes[destination] == route {
					delete(routes, destination)
				}
				routesMu.Unlock()
				route.conn.Close()
			})
		}
		_ = route.conn.SetWriteDeadline(time.Now().Add(handlerTimeoutForward))
		if _, writeErr := route.conn.Write(payload); writeErr != nil {
			routesMu.Lock()
			delete(routes, destination)
			routesMu.Unlock()
			route.conn.Close()
		}
	}
}

func forwardSocks5UDPResponses(relay *net.UDPConn, client *net.UDPAddr, source string, route *socksUDPRoute, closeRoute func()) {
	defer closeRoute()
	buffer := make([]byte, 64*1024)
	for {
		_ = route.conn.SetReadDeadline(time.Now().Add(handlerTimeoutForward))
		n, err := route.conn.Read(buffer)
		if err != nil {
			return
		}
		packet, err := buildSocks5UDPDatagram(source, buffer[:n])
		if err == nil {
			_, _ = relay.WriteToUDP(packet, client)
		}
	}
}

func parseSocks5UDPDatagram(packet []byte) (string, []byte, error) {
	if len(packet) < 7 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0 {
		return "", nil, fmt.Errorf("invalid SOCKS UDP header")
	}
	host, offset, err := parseSocks5Address(packet, 3)
	if err != nil || len(packet) < offset+2 {
		return "", nil, fmt.Errorf("invalid SOCKS UDP address")
	}
	port := binary.BigEndian.Uint16(packet[offset : offset+2])
	return net.JoinHostPort(host, strconv.Itoa(int(port))), packet[offset+2:], nil
}

func buildSocks5UDPDatagram(address string, payload []byte) ([]byte, error) {
	host, portText, err := net.SplitHostPort(address)
	if err != nil {
		return nil, err
	}
	port, err := strconv.Atoi(portText)
	if err != nil || port < 0 || port > 65535 {
		return nil, fmt.Errorf("invalid UDP port")
	}
	packet := []byte{0, 0, 0}
	packet, err = appendSocks5Address(packet, host)
	if err != nil {
		return nil, err
	}
	packet = binary.BigEndian.AppendUint16(packet, uint16(port))
	return append(packet, payload...), nil
}

func parseSocks5Address(packet []byte, offset int) (string, int, error) {
	if len(packet) <= offset {
		return "", 0, io.ErrUnexpectedEOF
	}
	switch packet[offset] {
	case 1:
		if len(packet) < offset+5 {
			return "", 0, io.ErrUnexpectedEOF
		}
		return net.IP(packet[offset+1 : offset+5]).String(), offset + 5, nil
	case 4:
		if len(packet) < offset+17 {
			return "", 0, io.ErrUnexpectedEOF
		}
		return net.IP(packet[offset+1 : offset+17]).String(), offset + 17, nil
	case 3:
		if len(packet) < offset+2 {
			return "", 0, io.ErrUnexpectedEOF
		}
		length := int(packet[offset+1])
		if length == 0 || len(packet) < offset+2+length {
			return "", 0, io.ErrUnexpectedEOF
		}
		return string(packet[offset+2 : offset+2+length]), offset + 2 + length, nil
	default:
		return "", 0, fmt.Errorf("unsupported address type")
	}
}

func appendSocks5Address(packet []byte, host string) ([]byte, error) {
	if ip := net.ParseIP(host); ip != nil {
		if ip4 := ip.To4(); ip4 != nil {
			return append(append(packet, 1), ip4...), nil
		}
		return append(append(packet, 4), ip.To16()...), nil
	}
	if len(host) == 0 || len(host) > 255 {
		return nil, fmt.Errorf("invalid domain length")
	}
	return append(append(packet, 3, byte(len(host))), host...), nil
}

func socks5AddressReply(address net.Addr) ([]byte, error) {
	udp, ok := address.(*net.UDPAddr)
	if !ok {
		return nil, fmt.Errorf("invalid UDP relay address")
	}
	reply := []byte{5, 0, 0}
	var err error
	reply, err = appendSocks5Address(reply, udp.IP.String())
	if err != nil {
		return nil, err
	}
	return binary.BigEndian.AppendUint16(reply, uint16(udp.Port)), nil
}

func udpDestinationAllowed(destination string, payload []byte) bool {
	host, portText, err := net.SplitHostPort(destination)
	if err != nil {
		return false
	}
	if ip := net.ParseIP(host); ip != nil && ip.To4() == nil && !environmentEnabled("TCPROUTE_ENABLE_IPV6") {
		return false
	}
	port, _ := strconv.Atoi(portText)
	if !environmentEnabled("TCPROUTE_ALLOW_QUIC") && port == 443 {
		return false
	}
	if !environmentEnabled("TCPROUTE_ALLOW_STUN") && (isSTUNPacket(payload) || isCommonSTUNPort(port)) {
		return false
	}
	return true
}

func environmentEnabled(name string) bool {
	value := strings.TrimSpace(strings.ToLower(os.Getenv(name)))
	return value == "1" || value == "true" || value == "yes" || value == "on"
}

func isSTUNPacket(payload []byte) bool {
	return len(payload) >= 20 && payload[0]&0xc0 == 0 && binary.BigEndian.Uint32(payload[4:8]) == 0x2112a442
}

func isCommonSTUNPort(port int) bool {
	return port == 3478 || port == 5349 || (port >= 19302 && port <= 19309)
}

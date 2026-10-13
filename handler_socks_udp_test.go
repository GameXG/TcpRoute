package main

import (
	"encoding/binary"
	"io"
	"net"
	"testing"
	"time"
)

type directUDPTestUpstream struct{}

func (directUDPTestUpstream) DialTimeout(network, address string, timeout time.Duration) (net.Conn, UpStreamErrorReporting, error) {
	conn, err := net.DialTimeout(network, address, timeout)
	return conn, nil, err
}

func TestSocks5UDPDatagramRoundTrip(t *testing.T) {
	for _, address := range []string{"127.0.0.1:53", "example.com:443", "[2001:db8::1]:5353"} {
		packet, err := buildSocks5UDPDatagram(address, []byte("payload"))
		if err != nil {
			t.Fatalf("build %s: %v", address, err)
		}
		parsedAddress, payload, err := parseSocks5UDPDatagram(packet)
		if err != nil {
			t.Fatalf("parse %s: %v", address, err)
		}
		if parsedAddress != address || string(payload) != "payload" {
			t.Fatalf("round trip mismatch: %s %q", parsedAddress, payload)
		}
	}
}

func TestUDPFeaturePolicy(t *testing.T) {
	t.Setenv("TCPROUTE_ALLOW_STUN", "false")
	t.Setenv("TCPROUTE_ALLOW_QUIC", "false")
	t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
	stun := make([]byte, 20)
	binary.BigEndian.PutUint32(stun[4:8], 0x2112a442)
	if udpDestinationAllowed("example.com:443", []byte("quic")) {
		t.Fatal("QUIC must be blocked by default")
	}
	if udpDestinationAllowed("example.com:9999", stun) {
		t.Fatal("STUN payload must be blocked by default")
	}
	if !udpDestinationAllowed("example.com:123", []byte("ntp")) {
		t.Fatal("ordinary UDP must remain available")
	}
	if udpDestinationAllowed("[2001:db8::1]:123", []byte("ipv6")) {
		t.Fatal("IPv6 must be blocked by default")
	}
	t.Setenv("TCPROUTE_ALLOW_STUN", "true")
	t.Setenv("TCPROUTE_ALLOW_QUIC", "true")
	t.Setenv("TCPROUTE_ENABLE_IPV6", "true")
	if !udpDestinationAllowed("example.com:443", []byte("quic")) || !udpDestinationAllowed("example.com:3478", stun) {
		t.Fatal("enabled UDP features must be allowed")
	}
	if !udpDestinationAllowed("[2001:db8::1]:123", []byte("ipv6")) {
		t.Fatal("enabled IPv6 must be allowed")
	}
}

func TestSocks5UDPAssociateRelay(t *testing.T) {
	t.Setenv("TCPROUTE_ALLOW_STUN", "false")
	t.Setenv("TCPROUTE_ALLOW_QUIC", "false")

	echo, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatal(err)
	}
	defer echo.Close()
	go func() {
		buffer := make([]byte, 1024)
		n, peer, readErr := echo.ReadFromUDP(buffer)
		if readErr == nil {
			_, _ = echo.WriteToUDP(buffer[:n], peer)
		}
	}()

	controlListener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer controlListener.Close()
	handlerDone := make(chan error, 1)
	go func() {
		control, acceptErr := controlListener.Accept()
		if acceptErr != nil {
			handlerDone <- acceptErr
			return
		}
		handler := &hSocksHandle{hSocksServer: &hSocksServer{upStream: directUDPTestUpstream{}}, conn: control}
		handlerDone <- handler.handleSocks5()
	}()

	control, err := net.Dial("tcp4", controlListener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer control.Close()
	_, _ = control.Write([]byte{1, 0})
	authReply := make([]byte, 2)
	if _, err = io.ReadFull(control, authReply); err != nil || authReply[1] != 0 {
		t.Fatalf("auth reply: %v %v", authReply, err)
	}
	_, _ = control.Write([]byte{5, 3, 0, 1, 0, 0, 0, 0, 0, 0})
	reply := make([]byte, 10)
	if _, err = io.ReadFull(control, reply); err != nil || reply[1] != 0 {
		t.Fatalf("associate reply: %v %v", reply, err)
	}
	relayAddress := net.JoinHostPort(net.IP(reply[4:8]).String(), stringPort(reply[8:10]))
	relay, err := net.Dial("udp4", relayAddress)
	if err != nil {
		t.Fatal(err)
	}
	defer relay.Close()
	packet, _ := buildSocks5UDPDatagram(echo.LocalAddr().String(), []byte("udp-ok"))
	_ = relay.SetDeadline(time.Now().Add(2 * time.Second))
	if _, err = relay.Write(packet); err != nil {
		t.Fatal(err)
	}
	response := make([]byte, 1024)
	n, err := relay.Read(response)
	if err != nil {
		t.Fatal(err)
	}
	_, payload, err := parseSocks5UDPDatagram(response[:n])
	if err != nil || string(payload) != "udp-ok" {
		t.Fatalf("relay response: %q %v", payload, err)
	}
	_ = control.Close()
	select {
	case err := <-handlerDone:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("UDP association did not stop with its TCP control connection")
	}
}

func stringPort(port []byte) string {
	value := binary.BigEndian.Uint16(port)
	if value == 0 {
		return "0"
	}
	var digits [5]byte
	i := len(digits)
	for value > 0 {
		i--
		digits[i] = byte('0' + value%10)
		value /= 10
	}
	return string(digits[i:])
}

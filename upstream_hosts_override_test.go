package main

import (
	"fmt"
	"net"
	"testing"
	"time"

	proxyclient "github.com/GameXG/ProxyClient"
	"github.com/GameXG/TcpRoute2/netchan"
)

type hostsOverrideClient struct {
	proxyclient.ProxyClient
	attempts chan string
	fail     bool
}

func (client *hostsOverrideClient) DialTimeout(_ string, address string, _ time.Duration) (net.Conn, error) {
	client.attempts <- address
	if client.fail {
		return nil, fmt.Errorf("host connection failed")
	}
	conn, peer := net.Pipe()
	_ = peer.Close()
	return conn, nil
}

func TestHostsOverrideBypassesCachedDNSAddress(t *testing.T) {
	for _, fail := range []bool{false, true} {
		t.Run(fmt.Sprintf("connectionFailure=%v", fail), func(t *testing.T) {
			router := NewTcppingUpStream()
			rule, err := newHostDomainRule("base", "example.test")
			if err != nil {
				t.Fatal(err)
			}
			router.hosts = []*hostRuleSource{{
				remote:  true,
				entries: []hostRuleEntry{{rule: rule, ips: []string{"192.0.2.2"}}},
			}}
			netchan.SetHostsResolver(router.lookupHostRecords)
			t.Cleanup(func() { netchan.SetHostsResolver(nil) })
			client := &hostsOverrideClient{attempts: make(chan string, 4), fail: fail}
			router.dc = []DialClient{{name: "direct", dnsResolve: true, pc: client}}
			router.connCache.Updata("example.test:443", "203.0.113.1:443", time.Millisecond, &router.dc[0], "direct")
			conn, _, err := router.DialTimeout("tcp", "example.test:443", time.Second)
			if conn != nil {
				_ = conn.Close()
			}
			if (err != nil) != fail {
				t.Fatalf("connection error=%v, want failure=%v", err, fail)
			}
			select {
			case address := <-client.attempts:
				if address != "192.0.2.2:443" {
					t.Fatalf("used cached DNS address %s instead of hosts", address)
				}
			default:
				t.Fatal("hosts address was not attempted")
			}
			if len(client.attempts) != 0 {
				t.Fatal("hosts connection failure fell back to another address")
			}
		})
	}
}

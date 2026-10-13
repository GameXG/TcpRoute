package netchan

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"reflect"
	"testing"
	"time"

	"github.com/miekg/dns"
)

func dnsTestServer(t *testing.T, handler func(*dns.Msg, http.ResponseWriter)) *httptest.Server {
	t.Helper()
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" || r.Header.Get("Content-Type") != "application/dns-message" {
			t.Error("not a standard DoH POST")
		}
		body, _ := io.ReadAll(r.Body)
		query := new(dns.Msg)
		if err := query.Unpack(body); err != nil {
			t.Error(err)
			w.WriteHeader(400)
			return
		}
		handler(query, w)
	}))
	t.Cleanup(server.Close)
	return server
}

func dnsTestReply(query *dns.Msg, w http.ResponseWriter, code int, addresses ...string) {
	response := new(dns.Msg)
	response.SetReply(query)
	response.Rcode = code
	for _, address := range addresses {
		if query.Question[0].Qtype == dns.TypeA {
			response.Answer = append(response.Answer, &dns.A{Hdr: dns.RR_Header{Name: query.Question[0].Name, Rrtype: dns.TypeA, Class: dns.ClassINET, Ttl: 60}, A: net.ParseIP(address)})
		} else {
			response.Answer = append(response.Answer, &dns.AAAA{Hdr: dns.RR_Header{Name: query.Question[0].Name, Rrtype: dns.TypeAAAA, Class: dns.ClassINET, Ttl: 60}, AAAA: net.ParseIP(address)})
		}
	}
	wire, _ := response.Pack()
	w.Header().Set("Content-Type", "application/dns-message")
	_, _ = w.Write(wire)
}

func TestDNSPriorityAndFallback(t *testing.T) {
	t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
	calls := make(chan string, 10)
	fail := dnsTestServer(t, func(q *dns.Msg, w http.ResponseWriter) { calls <- "first"; w.WriteHeader(503) })
	success := dnsTestServer(t, func(q *dns.Msg, w http.ResponseWriter) {
		calls <- "second"
		dnsTestReply(q, w, dns.RcodeSuccess, "192.0.2.1", "192.0.2.2")
	})
	p, err := newPriorityDNS([]DNSConfig{{Type: "doh", URL: success.URL, Priority: 20}, {Type: "doh", URL: fail.URL, Priority: 10}})
	if err != nil {
		t.Fatal(err)
	}
	p.endpoints[0].client = fail.Client()
	p.endpoints[1].client = success.Client()
	p.systemLookup = func(context.Context, string) ([]net.IP, error) {
		calls <- "system"
		return []net.IP{net.ParseIP("192.0.2.3")}, nil
	}
	ips, err := p.lookup(context.Background(), "example.test")
	if err != nil || len(ips) != 2 || len(calls) != 2 || !reflect.DeepEqual([]string{<-calls, <-calls}, []string{"first", "second"}) {
		t.Fatalf("ips=%v error=%v", ips, err)
	}
	p.endpoints = p.endpoints[:1]
	ips, err = p.lookup(context.Background(), "example.test")
	if err != nil || len(ips) != 1 || len(calls) != 2 || !reflect.DeepEqual([]string{<-calls, <-calls}, []string{"first", "system"}) {
		t.Fatalf("fallback ips=%v error=%v", ips, err)
	}
}

func TestDNSNegativeEmptyMalformedAndTimeout(t *testing.T) {
	t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
	for _, mode := range []string{"nxdomain", "empty", "malformed", "wrong-question", "timeout", "servfail"} {
		t.Run(mode, func(t *testing.T) {
			server := dnsTestServer(t, func(q *dns.Msg, w http.ResponseWriter) {
				switch mode {
				case "timeout":
					time.Sleep(40 * time.Millisecond)
					w.WriteHeader(503)
				case "malformed":
					w.Header().Set("Content-Type", "application/dns-message")
					_, _ = w.Write([]byte("invalid"))
				case "wrong-question":
					q.Question[0].Name = "other.test."
					dnsTestReply(q, w, dns.RcodeSuccess, "192.0.2.9")
				case "nxdomain":
					dnsTestReply(q, w, dns.RcodeNameError)
				case "servfail":
					dnsTestReply(q, w, dns.RcodeServerFailure)
				default:
					dnsTestReply(q, w, dns.RcodeSuccess)
				}
			})
			timeout := "2s"
			if mode == "timeout" {
				timeout = "20ms"
			}
			p, _ := newPriorityDNS([]DNSConfig{{Type: "doh", URL: server.URL, Timeout: timeout}})
			p.endpoints[0].client = server.Client()
			fallback := false
			p.systemLookup = func(context.Context, string) ([]net.IP, error) {
				fallback = true
				return []net.IP{net.ParseIP("192.0.2.8")}, nil
			}
			ips, err := p.lookup(context.Background(), "example.test")
			if mode == "nxdomain" {
				negative, ok := err.(*net.DNSError)
				if !ok || !negative.IsNotFound || fallback {
					t.Fatalf("NXDOMAIN ips=%v error=%v fallback=%v", ips, err, fallback)
				}
			} else if err != nil || !fallback || len(ips) != 1 {
				t.Fatalf("ips=%v error=%v fallback=%v", ips, err, fallback)
			}
		})
	}
}

func TestDNSDefaultsValidationAndCancellation(t *testing.T) {
	for _, config := range []DNSConfig{{Type: "invalid"}, {Type: "doh", URL: "http://dns.test"}, {Type: "doh", URL: "https://user:secret@dns.test"}, {Type: "system", Timeout: "0s"}, {Type: "system", Timeout: "invalid"}} {
		if _, err := newPriorityDNS([]DNSConfig{config}); err == nil {
			t.Fatalf("accepted %v", config)
		}
	}
	p, err := newPriorityDNS(nil)
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	p.systemLookup = func(context.Context, string) ([]net.IP, error) {
		calls++
		return []net.IP{net.ParseIP("192.0.2.4")}, nil
	}
	if ips, err := p.lookup(context.Background(), "example.test"); err != nil || len(ips) != 1 || calls != 1 {
		t.Fatalf("default ips=%v error=%v", ips, err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := p.lookup(ctx, "example.test"); err != context.Canceled || calls != 1 {
		t.Fatal("cancelled query used fallback")
	}
	p, _ = newPriorityDNS([]DNSConfig{{Type: "doh", URL: "https://dns.test/one", Priority: 10}, {Type: "doh", URL: "https://dns.test/two", Priority: 10}})
	if p.endpoints[0].config.URL != "https://dns.test/one" {
		t.Fatal("equal priority order changed")
	}
}

func TestDNSIPv6Candidates(t *testing.T) {
	t.Setenv("TCPROUTE_ENABLE_IPV6", "true")
	server := dnsTestServer(t, func(q *dns.Msg, w http.ResponseWriter) {
		if q.Question[0].Qtype == dns.TypeAAAA {
			dnsTestReply(q, w, dns.RcodeSuccess, "2001:db8::1")
		} else {
			dnsTestReply(q, w, dns.RcodeSuccess, "192.0.2.1")
		}
	})
	p, _ := newPriorityDNS([]DNSConfig{{Type: "doh", URL: server.URL}})
	p.endpoints[0].client = server.Client()
	p.systemLookup = func(context.Context, string) ([]net.IP, error) { return nil, fmt.Errorf("unexpected fallback") }
	if ips, err := p.lookup(context.Background(), "example.test"); err != nil || len(ips) != 2 {
		t.Fatalf("ips=%v error=%v", ips, err)
	}
}

func TestDNSCertificateValidation(t *testing.T) {
	server := dnsTestServer(t, func(q *dns.Msg, w http.ResponseWriter) { dnsTestReply(q, w, dns.RcodeSuccess, "192.0.2.1") })
	p, err := newPriorityDNS([]DNSConfig{{Type: "doh", URL: server.URL}})
	if err != nil {
		t.Fatal(err)
	}
	fallback := false
	p.systemLookup = func(context.Context, string) ([]net.IP, error) {
		fallback = true
		return []net.IP{net.ParseIP("192.0.2.2")}, nil
	}
	if _, err := p.lookup(context.Background(), "example.test"); err != nil || !fallback {
		t.Fatalf("untrusted test certificate did not cause fallback: %v", err)
	}
}

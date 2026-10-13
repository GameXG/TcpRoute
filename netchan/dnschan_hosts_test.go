package netchan

import (
	"fmt"
	"net"
	"reflect"
	"sync/atomic"
	"testing"
	"time"
)

type failingHostDial struct {
	attempts chan string
}

func (dial *failingHostDial) DialTimeout(_ string, address string, _ time.Duration) (net.Conn, error) {
	dial.attempts <- address
	return nil, fmt.Errorf("unreachable host")
}

func TestHostsFilteringDoesNotFallBackToDNS(t *testing.T) {
	for _, test := range []struct {
		name     string
		record   DnsRecord
		local    bool
		wantDial string
	}{
		{name: "low credit", record: DnsRecord{Ip: "192.0.2.1", Credit: -100}, local: true},
		{name: "IPv6 disabled", record: DnsRecord{Ip: "2001:db8::1"}, local: true},
		{name: "unreachable hosts", record: DnsRecord{Ip: "192.0.2.1"}, local: true, wantDial: "192.0.2.1:80"},
		{name: "remote resolution", record: DnsRecord{Ip: "192.0.2.1"}, wantDial: "example.test:80"},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
			dns := &recordingDNSQuery{}
			queryRWM.Lock()
			original := queries
			queries = []queryer{dns, &hostsResolverQuery{resolver: func(string) []DnsRecord { return []DnsRecord{test.record} }}}
			queryRWM.Unlock()
			t.Cleanup(func() {
				queryRWM.Lock()
				queries = original
				queryRWM.Unlock()
			})
			dial := &failingHostDial{attempts: make(chan string, 4)}
			exit := make(chan int)
			defer close(exit)
			_ = ChanDialTimeout(dial, 0, make(chan ConnRes), exit, test.local, nil, nil, "tcp", "example.test:80", time.Second)
			if dns.calls.Load() != 0 {
				t.Fatal("hosts mapping fell back to DNS")
			}
			if test.wantDial == "" {
				if len(dial.attempts) != 0 {
					t.Fatal("filtered hosts address was dialed")
				}
			} else {
				select {
				case address := <-dial.attempts:
					if address != test.wantDial || len(dial.attempts) != 0 {
						t.Fatalf("dialed %q; want only %q", address, test.wantDial)
					}
				default:
					t.Fatalf("did not dial %q", test.wantDial)
				}
			}
		})
	}
}

type recordingDNSQuery struct {
	calls atomic.Int32
}

func (q *recordingDNSQuery) query(_ string, records chan *DnsRecord, exit chan int) {
	q.calls.Add(1)
	select {
	case records <- &DnsRecord{Ip: "203.0.113.1"}:
	case <-exit:
	}
}

func TestHostsOverridesDNS(t *testing.T) {
	for _, matched := range []bool{true, false} {
		t.Run(map[bool]string{true: "match", false: "miss"}[matched], func(t *testing.T) {
			dns := &recordingDNSQuery{}
			hosts := []DnsRecord{{Ip: "192.0.2.1", Credit: -100}, {Ip: "192.0.2.2", Credit: 10}, {Ip: "2001:db8::1", Credit: 0}}
			queryRWM.Lock()
			original := queries
			queries = []queryer{dns}
			queryRWM.Unlock()
			t.Cleanup(func() {
				queryRWM.Lock()
				queries = original
				queryRWM.Unlock()
			})
			SetHostsResolver(func(domain string) []DnsRecord {
				if domain != "example.test" {
					t.Errorf("unexpected domain %q", domain)
				}
				if matched {
					return hosts
				}
				return nil
			})
			query := NewDnsQuery("example.test")
			var got []DnsRecord
			timeout := time.NewTimer(time.Second)
			defer timeout.Stop()
			for {
				select {
				case record, open := <-query.RecordChan:
					if !open {
						want := hosts
						wantCalls := int32(0)
						if !matched {
							want = []DnsRecord{{Ip: "203.0.113.1"}}
							wantCalls = 1
						}
						if !reflect.DeepEqual(got, want) || dns.calls.Load() != wantCalls {
							t.Fatalf("records=%v, DNS calls=%d; want %v, %d", got, dns.calls.Load(), want, wantCalls)
						}
						return
					}
					got = append(got, *record)
				case <-timeout.C:
					query.Stop()
					t.Fatal("local resolution did not finish")
				}
			}
		})
	}
}

func TestHostsQueryCancellation(t *testing.T) {
	queryRWM.Lock()
	original := queries
	dns := &recordingDNSQuery{}
	queries = []queryer{dns, &hostsResolverQuery{resolver: func(string) []DnsRecord {
		return []DnsRecord{{Ip: "192.0.2.1"}}
	}}}
	queryRWM.Unlock()
	t.Cleanup(func() {
		queryRWM.Lock()
		queries = original
		queryRWM.Unlock()
	})
	query := &DnsQuery{RecordChan: make(chan *DnsRecord), exitChan: make(chan int), Domain: "example.test"}
	close(query.exitChan)
	done := make(chan struct{})
	go func() { query.query(); close(done) }()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("cancelled hosts lookup blocked")
	}
	if dns.calls.Load() != 0 {
		t.Fatal("cancelled hosts lookup fell back to DNS")
	}
}

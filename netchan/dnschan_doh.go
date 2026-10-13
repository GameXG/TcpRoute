package netchan

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"mime"
	"net"
	"net/http"
	"net/url"
	"sort"
	"strings"
	"time"

	"github.com/miekg/dns"
)

type DNSConfig struct {
	Type     string
	URL      string
	Priority int
	Timeout  string
}

type dnsEndpoint struct {
	config  DNSConfig
	timeout time.Duration
	client  *http.Client
}

type priorityDNS struct {
	endpoints    []dnsEndpoint
	systemLookup func(context.Context, string) ([]net.IP, error)
}

// ConfigureDNS replaces the DNS provider while retaining the Hosts override.
func ConfigureDNS(configs []DNSConfig) error {
	provider, err := newPriorityDNS(configs)
	if err != nil {
		return err
	}
	queryRWM.Lock()
	defer func() {
		queryRWM.Unlock()
		blackIPMonitorOnce.Do(func() { go searchBlackIP() })
	}()
	retained := []queryer{provider}
	for _, query := range queries {
		if _, ok := query.(*hostsResolverQuery); ok {
			retained = append(retained, query)
		}
	}
	queries = retained
	return nil
}

func newPriorityDNS(configs []DNSConfig) (*priorityDNS, error) {
	provider := &priorityDNS{systemLookup: func(ctx context.Context, domain string) ([]net.IP, error) {
		return net.DefaultResolver.LookupIP(ctx, "ip", domain)
	}}
	for i, config := range configs {
		config.Type = strings.ToLower(strings.TrimSpace(config.Type))
		if config.Type != "doh" && config.Type != "system" {
			return nil, fmt.Errorf("Dns %d: unsupported Type %q", i+1, config.Type)
		}
		timeout := 3 * time.Second
		if config.Timeout != "" {
			var err error
			timeout, err = time.ParseDuration(config.Timeout)
			if err != nil || timeout <= 0 {
				return nil, fmt.Errorf("Dns %d: invalid Timeout %q", i+1, config.Timeout)
			}
		}
		endpoint := dnsEndpoint{config: config, timeout: timeout}
		if config.Type == "doh" {
			parsed, err := url.Parse(config.URL)
			if err != nil || parsed.Scheme != "https" || parsed.Hostname() == "" || parsed.User != nil || parsed.Fragment != "" {
				return nil, fmt.Errorf("Dns %d: URL must be an HTTPS endpoint without credentials or fragment", i+1)
			}
			// Use system bootstrap and direct HTTPS; never inherit the application's proxy.
			transport := http.DefaultTransport.(*http.Transport).Clone()
			transport.Proxy = nil
			transport.DialContext = OutboundDialer(0).DialContext
			endpoint.client = &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
		}
		provider.endpoints = append(provider.endpoints, endpoint)
	}
	sort.SliceStable(provider.endpoints, func(i, j int) bool {
		return provider.endpoints[i].config.Priority < provider.endpoints[j].config.Priority
	})
	return provider, nil
}

func (p *priorityDNS) lookup(ctx context.Context, domain string) ([]net.IP, error) {
	systemTried := false
	for _, endpoint := range p.endpoints {
		if err := ctx.Err(); err != nil {
			return nil, err
		}
		attempt, cancel := context.WithTimeout(ctx, endpoint.timeout)
		var ips []net.IP
		var err error
		if endpoint.config.Type == "system" {
			systemTried = true
			ips, err = p.systemLookup(attempt, domain)
		} else {
			ips, err = endpoint.lookup(attempt, domain)
		}
		cancel()
		if usable := usableDNSAddresses(ips); len(usable) > 0 {
			return usable, nil
		}
		if negative, ok := err.(*net.DNSError); ok && negative.IsNotFound {
			return nil, err
		}
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	if systemTried {
		return nil, fmt.Errorf("all configured DNS sources failed for %s", domain)
	}
	fallback, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	ips, err := p.systemLookup(fallback, domain)
	return usableDNSAddresses(ips), err
}

func usableDNSAddresses(ips []net.IP) []net.IP {
	rwm.RLock()
	defer rwm.RUnlock()
	seen := make(map[string]bool)
	result := make([]net.IP, 0, len(ips))
	for _, ip := range ips {
		if ip == nil || (ip.To4() == nil && !ipv6Enabled()) {
			continue
		}
		key := ip.String()
		if !blackIP[key] && !seen[key] {
			seen[key] = true
			result = append(result, ip)
		}
	}
	return result
}

func (p *priorityDNS) query(domain string, recordChan chan *DnsRecord, exitChan chan int) {
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	go func() {
		select {
		case <-exitChan:
			cancel()
		case <-ctx.Done():
		}
	}()
	ips, _ := p.lookup(ctx, domain)
	for _, ip := range ips {
		select {
		case <-ctx.Done():
			return
		case recordChan <- &DnsRecord{Ip: ip.String(), Credit: 0}:
		}
	}
}

func (e dnsEndpoint) lookup(ctx context.Context, domain string) ([]net.IP, error) {
	types := []uint16{dns.TypeA}
	if ipv6Enabled() {
		types = append(types, dns.TypeAAAA)
	}
	var ips []net.IP
	var lastError error
	for _, kind := range types {
		addresses, err := e.exchange(ctx, domain, kind)
		ips = append(ips, addresses...)
		if err != nil {
			lastError = err
			if negative, ok := err.(*net.DNSError); ok && negative.IsNotFound {
				break
			}
		}
	}
	return ips, lastError
}

func (e dnsEndpoint) exchange(ctx context.Context, domain string, kind uint16) ([]net.IP, error) {
	question := new(dns.Msg)
	question.SetQuestion(dns.Fqdn(domain), kind)
	wire, err := question.Pack()
	if err != nil {
		return nil, err
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, e.config.URL, bytes.NewReader(wire))
	if err != nil {
		return nil, err
	}
	request.Header.Set("Content-Type", "application/dns-message")
	request.Header.Set("Accept", "application/dns-message")
	response, err := e.client.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	contentType, _, err := mime.ParseMediaType(response.Header.Get("Content-Type"))
	if response.StatusCode != http.StatusOK || err != nil || contentType != "application/dns-message" {
		return nil, fmt.Errorf("invalid DoH HTTP response")
	}
	body, err := io.ReadAll(io.LimitReader(response.Body, 65536))
	if err != nil || len(body) > 65535 {
		return nil, fmt.Errorf("invalid DoH response size: %v", err)
	}
	answer := new(dns.Msg)
	if err := answer.Unpack(body); err != nil {
		return nil, err
	}
	if !answer.Response || answer.Truncated || answer.Id != question.Id || answer.Opcode != dns.OpcodeQuery || len(answer.Question) != 1 ||
		!strings.EqualFold(answer.Question[0].Name, question.Question[0].Name) || answer.Question[0].Qtype != kind || answer.Question[0].Qclass != dns.ClassINET {
		return nil, fmt.Errorf("DoH response does not match the query")
	}
	if answer.Rcode == dns.RcodeNameError {
		return nil, &net.DNSError{Err: "no such host", Name: domain, IsNotFound: true}
	}
	if answer.Rcode != dns.RcodeSuccess {
		return nil, fmt.Errorf("DoH DNS status %d", answer.Rcode)
	}
	var ips []net.IP
	for _, record := range answer.Answer {
		switch record := record.(type) {
		case *dns.A:
			if kind == dns.TypeA {
				ips = append(ips, record.A)
			}
		case *dns.AAAA:
			if kind == dns.TypeAAAA {
				ips = append(ips, record.AAAA)
			}
		}
	}
	return ips, nil
}

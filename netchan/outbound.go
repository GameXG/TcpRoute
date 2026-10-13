package netchan

import (
	"context"
	"net"
	"syscall"
	"time"
)

// OutboundControl is a no-op except when the Windows client selects a physical interface.
var OutboundControl func(string, string, syscall.RawConn) error

func OutboundDialer(timeout time.Duration) *net.Dialer {
	return &net.Dialer{Timeout: timeout, Control: OutboundControl}
}

// LookupDNS exposes the configured scheduler to the TUN DNS gateway.
func LookupDNS(ctx context.Context, domain string) ([]net.IP, error) {
	for _, query := range querySnapshot() {
		if provider, ok := query.(*priorityDNS); ok {
			return provider.lookup(ctx, domain)
		}
	}
	return net.DefaultResolver.LookupIP(ctx, "ip", domain)
}

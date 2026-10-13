package netchan

import "testing"

func TestIPv6FeaturePolicy(t *testing.T) {
	filter := NewDialFilter(nil)
	t.Setenv("TCPROUTE_ENABLE_IPV6", "false")
	if !filter.DialFilter("tcp", "example.com", "192.0.2.1", 443, 0, 0) {
		t.Fatal("IPv4 candidate should remain available")
	}
	if filter.DialFilter("tcp", "example.com", "2001:db8::1", 443, 0, 0) {
		t.Fatal("IPv6 candidate should be blocked by default")
	}
	t.Setenv("TCPROUTE_ENABLE_IPV6", "true")
	if !filter.DialFilter("tcp", "example.com", "2001:db8::1", 443, 0, 0) {
		t.Fatal("IPv6 candidate should be available when enabled")
	}
}

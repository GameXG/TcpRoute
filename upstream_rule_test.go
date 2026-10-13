package main

import (
	"github.com/koding/multiconfig"
	xrouter "github.com/xtls/xray-core/app/router"
	"google.golang.org/protobuf/proto"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestParseDomainRuleTypes(t *testing.T) {
	tests := []struct {
		ruleType string
		line     string
		matches  string
		rejects  string
	}{
		{"base", "api.example.com", "api.example.com", "www.api.example.com"},
		{"suffix", "example.org", "www.example.org", "notexample.org"},
		{"pan", "*.example.net", "cdn.example.net", "a.b.example.net"},
		{"regex", `^api[0-9]+\.example\.io$`, "api12.example.io", "api.example.io"},
	}
	for _, test := range tests {
		rules, err := parseDomainRules("# comment\n"+test.line+"\n", test.ruleType)
		if err != nil || len(rules) != 1 {
			t.Fatalf("parseDomainRules(%s) = %#v, %v", test.ruleType, rules, err)
		}
		if !rules[0].matches(test.matches) || rules[0].matches(test.rejects) {
			t.Fatalf("%s rule match behavior is incorrect", test.ruleType)
		}
	}
}

func TestNestedUpstreamListsParseFromTOML(t *testing.T) {
	directory := t.TempDir()
	configPath := filepath.Join(directory, "config.toml")
	content := `Addr=":5050"

[[UpStreams]]
Name="proxy"
ProxyUrl="socks5://127.0.0.1:1080"

[[UpStreams.Whitelist]]
Path="rules/proxy.txt"
Type="suffix"

[[UpStreams.Whitelist]]
Path="https://example.com/proxy.txt"
Type="base"
UpdateInterval="2h"

[[UpStreams.Blacklist]]
Type="geosite:gfw"
Path="geosite.dat"
`
	if err := os.WriteFile(configPath, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	var config ServerConfig
	loader := multiconfig.MultiLoader(&multiconfig.TagLoader{}, &multiconfig.TOMLLoader{Path: configPath})
	if err := loader.Load(&config); err != nil {
		t.Fatal(err)
	}
	if len(config.UpStreams) != 1 || len(config.UpStreams[0].Whitelist) != 2 || len(config.UpStreams[0].Blacklist) != 1 {
		t.Fatalf("unexpected nested lists: %#v", config.UpStreams)
	}
	if config.UpStreams[0].Whitelist[0].Type != "suffix" || config.UpStreams[0].Blacklist[0].Type != "geosite:gfw" || config.UpStreams[0].Blacklist[0].Path != "geosite.dat" {
		t.Fatalf("nested list values were not preserved: %#v", config.UpStreams[0])
	}
}

func TestHostMappingsParseAndApply(t *testing.T) {
	directory := t.TempDir()
	configPath := filepath.Join(directory, "config.toml")
	hostsPath := filepath.Join(directory, "hosts.txt")
	if err := os.WriteFile(hostsPath, []byte("192.0.2.10 example.com\n192.0.2.11 example.com\n2001:db8::10 ipv6.example.com\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	content := `Addr=":5050"

[[Hosts]]
Path="hosts.txt"
Type="base"
Credit=-100

[[UpStreams]]
Name="direct"
ProxyUrl="direct://0.0.0.0:0000"
`
	if err := os.WriteFile(configPath, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	var config ServerConfig
	loader := multiconfig.MultiLoader(&multiconfig.TagLoader{}, &multiconfig.TOMLLoader{Path: configPath})
	if err := loader.Load(&config); err != nil {
		t.Fatal(err)
	}
	router := NewTcppingUpStream()
	if err := router.SetHostSources(config.Hosts, directory); err != nil {
		t.Fatal(err)
	}
	records := router.lookupHostRecords("Example.COM.")
	if len(records) != 2 || records[0].Ip != "192.0.2.10" || records[1].Ip != "192.0.2.11" {
		t.Fatalf("host candidates = %#v", records)
	}
	if records[0].Credit != -100 || records[1].Credit != -100 {
		t.Fatalf("host candidate credit was not preserved: %#v", records)
	}
	if got := router.lookupHostRecords("ipv6.example.com"); len(got) != 1 || got[0].Ip != "2001:db8::10" {
		t.Fatalf("IPv6 host candidates = %#v", got)
	}
	if got := router.lookupHostRecords("other.example.com"); len(got) != 0 {
		t.Fatalf("unmapped host returned %#v", got)
	}
}

func TestHostMappingsKeepMultipleCandidatesAndSkipMalformedRows(t *testing.T) {
	hosts, err := parseHostRules(strings.NewReader("192.0.2.1 example.com\ninvalid.example cname.example\nbroken-line\n192.0.2.2 example.com\n"))
	if err != nil {
		t.Fatal(err)
	}
	if got := hosts["example.com"]; len(got) != 2 || got[0] != "192.0.2.1" || got[1] != "192.0.2.2" {
		t.Fatalf("multiple candidates were not retained: %#v", got)
	}
	if got := hosts["cname.example"]; len(got) != 1 || got[0] != "invalid.example" {
		t.Fatalf("CNAME-style candidate was not retained: %#v", got)
	}
}

func TestHostSourceMatchingTypes(t *testing.T) {
	directory := t.TempDir()
	if err := os.WriteFile(filepath.Join(directory, "hosts.txt"), []byte("192.0.2.20 example.com\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	source, err := newHostRuleSource(ServerConfigHostSource{Path: "hosts.txt", Type: "suffix", Credit: 8}, directory)
	if err != nil {
		t.Fatal(err)
	}
	if got := source.lookup("www.example.com"); len(got) != 1 || got[0].Credit != 8 {
		t.Fatalf("suffix Host source did not match with credit: %#v", got)
	}
}

func TestUpstreamWhitelistAndBlacklistSelection(t *testing.T) {
	proxyRules, err := parseDomainRules(`
# comment

proxy.example.com
shared.example.com
`, "suffix")
	if err != nil {
		t.Fatal(err)
	}
	blockedRules, err := parseDomainRules("blocked.example.com\nshared.example.com\n", "suffix")
	if err != nil {
		t.Fatal(err)
	}
	router := &tcppingUpStream{dc: []DialClient{
		{name: "direct", blacklist: []*domainRuleSource{{rules: blockedRules}}},
		{name: "proxy", whitelist: []*domainRuleSource{{rules: proxyRules}}},
	}}

	tests := []struct {
		address string
		want    []string
		manual  bool
	}{
		{"www.proxy.example.com:443", []string{"proxy"}, true},
		{"blocked.example.com:443", []string{"proxy"}, true},
		{"shared.example.com:443", []string{"proxy"}, true},
		{"ordinary.example.net:443", []string{"direct", "proxy"}, false},
	}
	for _, test := range tests {
		selected, manual := router.selectDialClients(test.address)
		if manual != test.manual || len(selected) != len(test.want) {
			t.Fatalf("select(%s) = %#v, %v; want %v, %v", test.address, selected, manual, test.want, test.manual)
		}
		for index, name := range test.want {
			if selected[index].name != name {
				t.Fatalf("select(%s)[%d] = %s, want %s", test.address, index, selected[index].name, name)
			}
		}
	}
}

func TestWhitelistWinsOverBlacklistOnSameUpstream(t *testing.T) {
	rules, err := parseDomainRules("shared.example.com\n", "base")
	if err != nil {
		t.Fatal(err)
	}
	router := &tcppingUpStream{dc: []DialClient{
		{name: "direct"},
		{name: "proxy", whitelist: []*domainRuleSource{{rules: rules}}, blacklist: []*domainRuleSource{{rules: rules}}},
	}}
	selected, manual := router.selectDialClients("shared.example.com:443")
	if !manual || len(selected) != 1 || selected[0].name != "proxy" {
		t.Fatalf("whitelist did not win over blacklist: %#v, manual=%v", selected, manual)
	}
}

func TestGeoSiteReferenceUsesNamedList(t *testing.T) {
	directory := t.TempDir()
	list := &xrouter.GeoSiteList{Entry: []*xrouter.GeoSite{
		{
			CountryCode: "GFW",
			Domain: []*xrouter.Domain{
				{Type: xrouter.Domain_Domain, Value: "example.com"},
				{Type: xrouter.Domain_Full, Value: "exact.example.net"},
			},
		},
		{
			CountryCode: "CN",
			Domain: []*xrouter.Domain{
				{Type: xrouter.Domain_Domain, Value: "example.cn"},
			},
		},
	}}
	data, err := proto.Marshal(list)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(directory, "custom-geosite.dat"), data, 0o600); err != nil {
		t.Fatal(err)
	}

	source, err := newDomainRuleSource(ServerConfigDomainList{Type: "geosite:gfw", Path: "custom-geosite.dat"}, directory)
	if err != nil {
		t.Fatal(err)
	}
	if !source.matches("www.example.com") || !source.matches("exact.example.net") {
		t.Fatal("geosite:gfw did not load the GFW list")
	}
	if source.matches("www.example.cn") {
		t.Fatal("geosite:gfw incorrectly loaded another geosite list")
	}
	if _, err := newDomainRuleSource(ServerConfigDomainList{Type: "geosite:missing", Path: "custom-geosite.dat"}, directory); err == nil {
		t.Fatal("missing geosite list was accepted")
	}
	if _, err := newDomainRuleSource(ServerConfigDomainList{Type: "geosite:gfw"}, directory); err == nil {
		t.Fatal("geosite reference in Type was accepted without Path")
	}
}

func TestGeoSiteReferenceCombinesCommaSeparatedLists(t *testing.T) {
	directory := t.TempDir()
	data, err := proto.Marshal(&xrouter.GeoSiteList{Entry: []*xrouter.GeoSite{
		{CountryCode: "GFW", Domain: []*xrouter.Domain{{Type: xrouter.Domain_Domain, Value: "google.example"}}},
		{CountryCode: "GOOGLE", Domain: []*xrouter.Domain{{Type: xrouter.Domain_Full, Value: "search.example"}}},
		{CountryCode: "BING", Domain: []*xrouter.Domain{{Type: xrouter.Domain_Domain, Value: "bing.example"}}},
	}})
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(directory, "geosite.dat")
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatal(err)
	}
	source, err := newDomainRuleSource(ServerConfigDomainList{
		Type: "geosite:gfw,google,bing", Path: path, UpdateInterval: "24h",
	}, directory)
	if err != nil {
		t.Fatal(err)
	}
	for _, domain := range []string{"www.google.example", "search.example", "www.bing.example"} {
		if !source.matches(domain) {
			t.Fatalf("combined Geosite lists did not match %s", domain)
		}
	}
	if _, err := parseGeoSiteReference("geosite:gfw,,bing"); err == nil {
		t.Fatal("empty Geosite list name was accepted")
	}
}

func TestGeoIPReferenceMatchesLiteralIPAndRoutesUpstream(t *testing.T) {
	directory := t.TempDir()
	data, err := proto.Marshal(&xrouter.GeoIPList{Entry: []*xrouter.GeoIP{
		{CountryCode: "TEST", Cidr: []*xrouter.CIDR{{Ip: []byte{203, 0, 113, 0}, Prefix: 24}}},
		{CountryCode: "PRIVATE", Cidr: []*xrouter.CIDR{{Ip: []byte{10, 0, 0, 0}, Prefix: 8}}},
	}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(directory, "geoip.dat"), data, 0o600); err != nil {
		t.Fatal(err)
	}
	source, err := newDomainRuleSource(ServerConfigDomainList{Type: "geoip:test"}, directory)
	if err != nil {
		t.Fatal(err)
	}
	if !source.matches("203.0.113.42") || source.matches("198.51.100.1") || source.matches("example.com") {
		t.Fatal("geoip:test did not match only the configured literal IP range")
	}
	router := &tcppingUpStream{dc: []DialClient{
		{name: "direct"},
		{name: "proxy", whitelist: []*domainRuleSource{source}},
	}}
	selected, manual := router.selectDialClients("203.0.113.42:443")
	if !manual || len(selected) != 1 || selected[0].name != "proxy" {
		t.Fatalf("GeoIP whitelist route = %#v, manual=%v", selected, manual)
	}
	if _, err := parseGeoIPReference("geoip:test,,private"); err == nil {
		t.Fatal("empty GeoIP selector was accepted")
	}
}

func TestBundledGeoSiteGFWReference(t *testing.T) {
	source, err := newDomainRuleSource(ServerConfigDomainList{Type: "geosite:gfw", Path: "geosite.dat"}, "assets")
	if err != nil {
		t.Fatal(err)
	}
	if !source.matches("www.google.com") {
		t.Fatal("bundled geosite:gfw does not match a known GFW list domain")
	}
}

func TestBundledGeoIPCNReference(t *testing.T) {
	source, err := newDomainRuleSource(ServerConfigDomainList{Type: "geoip:cn", Path: "geoip.dat"}, "assets")
	if err != nil {
		t.Fatal(err)
	}
	if !source.matches("223.5.5.5") {
		t.Fatal("bundled geoip:cn does not match a known China IP address")
	}
}

func TestRemoteGeoSiteDataPath(t *testing.T) {
	data, err := proto.Marshal(&xrouter.GeoSiteList{Entry: []*xrouter.GeoSite{{
		CountryCode: "GFW",
		Domain: []*xrouter.Domain{{
			Type:  xrouter.Domain_Domain,
			Value: "remote.example",
		}},
	}}})
	if err != nil {
		t.Fatal(err)
	}
	server := httptest.NewServer(http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		response.Header().Set("Content-Type", "application/octet-stream")
		_, _ = response.Write(data)
	}))

	source, err := newDomainRuleSource(ServerConfigDomainList{
		Type:           "geosite:gfw",
		Path:           server.URL + "/geosite.dat",
		UpdateInterval: "24h",
	}, t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	if !source.remote || !source.matches("www.remote.example") {
		t.Fatal("remote geosite.dat Path was not downloaded and matched")
	}
	server.Close()
	if err := source.reload(); err != nil || !source.matches("www.remote.example") {
		t.Fatalf("cached remote geosite.dat was not available after download failure: %v", err)
	}
	source.mu.RLock()
	refreshFailed := source.remoteRefreshFailed
	source.mu.RUnlock()
	if !refreshFailed {
		t.Fatal("cached remote fallback did not request the v0.5.14 one-minute retry path")
	}
}

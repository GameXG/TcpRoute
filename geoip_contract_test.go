package main

import (
	"os"
	"path/filepath"
	"testing"

	xrouter "github.com/xtls/xray-core/app/router"
	"google.golang.org/protobuf/proto"
)

func TestGeoIPExecutableLocalContract(t *testing.T) {
	for _, invalid := range []string{"geoip:", "geoip:cn,", "geoip:cn,geosite:private"} {
		if _, err := parseGeoIPReference(invalid); err == nil {
			t.Fatalf("accepted %q", invalid)
		}
	}
	tags, err := parseGeoIPReference("geoip:cn,geoip:private")
	if err != nil || len(tags) != 2 || tags[0] != "cn" || tags[1] != "private" {
		t.Fatalf("tags=%v error=%v", tags, err)
	}
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	path, err := executableGeoDataPath("geoip.dat")
	if err != nil || path != filepath.Join(filepath.Dir(executable), "geoip.dat") {
		t.Fatalf("path=%q error=%v", path, err)
	}
	directory := t.TempDir()
	if err := os.WriteFile(filepath.Join(directory, "geoip.dat"), []byte("wrong file"), 0600); err != nil {
		t.Fatal(err)
	}
	t.Setenv("xray.location.asset", directory)
	config := ServerConfigDomainList{Type: "geoip", Path: "geoip:cn,geoip:private"}
	if _, err := newDomainRuleSource(config, directory); err == nil {
		t.Fatal("missing executable-local geoip.dat was accepted")
	}
	data, err := proto.Marshal(&xrouter.GeoIPList{Entry: []*xrouter.GeoIP{
		{CountryCode: "cn", Cidr: []*xrouter.CIDR{{Ip: []byte{203, 0, 113, 0}, Prefix: 24}}},
		{CountryCode: "private", Cidr: []*xrouter.CIDR{{Ip: []byte{10, 0, 0, 0}, Prefix: 8}}},
	}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Remove(path) })
	source, err := newDomainRuleSource(config, directory)
	if err != nil {
		t.Fatal(err)
	}
	if !source.matches("203.0.113.1") || !source.matches("10.1.2.3") || source.matches("192.0.2.1") {
		t.Fatal("GeoIP selector union did not match expected addresses")
	}
}

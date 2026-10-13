package main

import (
	"os"
	"path/filepath"
	"testing"

	xrouter "github.com/xtls/xray-core/app/router"
	"google.golang.org/protobuf/proto"
)

func TestGeoSiteRepeatedPrefixes(t *testing.T) {
	selectors, err := parseGeoSiteReference("geosite:apple,geosite:google")
	if err != nil || len(selectors) != 2 || selectors[0].tag != "apple" || selectors[1].tag != "google" {
		t.Fatalf("selectors=%v, error=%v", selectors, err)
	}
	for _, invalid := range []string{"geosite:", "geosite:apple,", "geosite:apple,geoip:cn"} {
		if _, err := parseGeoSiteReference(invalid); err == nil {
			t.Fatalf("accepted %q", invalid)
		}
	}
}

func TestGeoSiteUsesExecutableDirectory(t *testing.T) {
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	path, err := executableGeoSitePath()
	if err != nil || path != filepath.Join(filepath.Dir(executable), "geosite.dat") {
		t.Fatalf("path=%q, error=%v", path, err)
	}
	// A config-relative or asset-environment copy must not mask a missing executable-local file.
	directory := t.TempDir()
	if err := os.WriteFile(filepath.Join(directory, "geosite.dat"), []byte("wrong file"), 0600); err != nil {
		t.Fatal(err)
	}
	t.Setenv("xray.location.asset", directory)
	if _, err := newDomainRuleSource(ServerConfigDomainList{Type: "geosite", Path: "geosite:apple,geosite:google"}, directory); err == nil {
		t.Fatal("missing executable-local geosite.dat was accepted")
	}
	data, err := proto.Marshal(&xrouter.GeoSiteList{Entry: []*xrouter.GeoSite{
		{CountryCode: "apple", Domain: []*xrouter.Domain{{Type: xrouter.Domain_Full, Value: "apple.test"}}},
		{CountryCode: "google", Domain: []*xrouter.Domain{{Type: xrouter.Domain_Full, Value: "google.test"}}},
	}})
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = os.Remove(path) })
	source, err := newDomainRuleSource(ServerConfigDomainList{Type: "geosite", Path: "geosite:apple,geosite:google"}, directory)
	if err != nil {
		t.Fatal(err)
	}
	if !source.matches("apple.test") || !source.matches("google.test") || source.matches("unrelated.test") {
		t.Fatal("executable-local selectors did not match the requested union")
	}
}

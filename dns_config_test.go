package main

import (
	"os"
	"path/filepath"
	"testing"

	"github.com/koding/multiconfig"
)

func TestDNSTablesLoadFromTOML(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.toml")
	if err := os.WriteFile(path, []byte("addr=\"127.0.0.1:7070\"\n[[Dns]]\nType=\"doh\"\nURL=\"https://dns.example/dns-query\"\nPriority=10\nTimeout=\"3s\"\n[[Dns]]\nType=\"system\"\nPriority=20\n"), 0600); err != nil {
		t.Fatal(err)
	}
	var config ServerConfig
	if err := (&multiconfig.TOMLLoader{Path: path}).Load(&config); err != nil {
		t.Fatal(err)
	}
	if len(config.Dns) != 2 || config.Dns[0].URL != "https://dns.example/dns-query" || config.Dns[0].Priority != 10 || config.Dns[0].Timeout != "3s" || config.Dns[1].Type != "system" {
		t.Fatalf("DNS configuration was not decoded: %+v", config.Dns)
	}
}

func TestRuntimeConfigKeepsSavedConfigurationDirectory(t *testing.T) {
	saved := t.TempDir()
	runtime := filepath.Join(t.TempDir(), "runtime.toml")
	t.Setenv("TCPROUTE_CONFIG_BASE", saved)
	if base, err := configurationBaseDirectory(runtime); err != nil || base != saved {
		t.Fatalf("base=%s error=%v", base, err)
	}
	t.Setenv("TCPROUTE_CONFIG_BASE", "")
	if base, err := configurationBaseDirectory(runtime); err != nil || base != filepath.Dir(runtime) {
		t.Fatalf("base=%s error=%v", base, err)
	}
	t.Setenv("TCPROUTE_CONFIG_BASE", filepath.Join(saved, "missing"))
	if _, err := configurationBaseDirectory(runtime); err == nil {
		t.Fatal("missing config base was accepted")
	}
}

package main

import (
	"reflect"
	"testing"
)

func TestParseAppArguments(t *testing.T) {
	tests := []struct {
		name        string
		args        []string
		wantPath    string
		wantVersion bool
		wantConfig  []string
	}{
		{
			name:       "defaults",
			wantPath:   "config.toml",
			wantConfig: []string{},
		},
		{
			name:        "separates application and config flags",
			args:        []string{"-config", "/tmp/android.toml", "-addr", "127.0.0.1:7070", "--version"},
			wantPath:    "/tmp/android.toml",
			wantVersion: true,
			wantConfig:  []string{"-addr", "127.0.0.1:7070"},
		},
		{
			name:       "supports equals syntax",
			args:       []string{"--config=route.toml", "-upstreams", "direct"},
			wantPath:   "route.toml",
			wantConfig: []string{"-upstreams", "direct"},
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			path, version, configArgs, err := parseAppArguments(test.args)
			if err != nil {
				t.Fatalf("parseAppArguments() error = %v", err)
			}
			if path != test.wantPath || version != test.wantVersion || !reflect.DeepEqual(configArgs, test.wantConfig) {
				t.Fatalf("parseAppArguments() = (%q, %v, %v), want (%q, %v, %v)", path, version, configArgs, test.wantPath, test.wantVersion, test.wantConfig)
			}
		})
	}
}

func TestParseAppArgumentsRejectsMissingConfigPath(t *testing.T) {
	if _, _, _, err := parseAppArguments([]string{"-config"}); err == nil {
		t.Fatal("parseAppArguments() accepted -config without a path")
	}
}

func TestCheckPreUsesConfiguredPortsOnlyForIPTargets(t *testing.T) {
	oldHTTP, oldHTTPS := preHttpPorts, preHttpsPorts
	preHttpPorts, preHttpsPorts = []int{8080}, []int{8443}
	defer func() { preHttpPorts, preHttpsPorts = oldHTTP, oldHTTPS }()

	if got := CheckPre("tcp", "192.0.2.1:8080"); got != preProtocolHTTP {
		t.Fatalf("HTTP correction protocol = %d", got)
	}
	if got := CheckPre("tcp", "[2001:db8::1]:8443"); got != preProtocolHTTPS {
		t.Fatalf("HTTPS correction protocol = %d", got)
	}
	if got := CheckPre("tcp", "example.com:8080"); got != preProtocolUnknown {
		t.Fatalf("domain target unexpectedly enabled correction: %d", got)
	}
}

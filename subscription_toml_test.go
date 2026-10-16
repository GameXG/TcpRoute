package main

import (
	"strings"
	"testing"

	btoml "github.com/BurntSushi/toml"
)

func sampleSubscriptionImport() subscriptionImport {
	return subscriptionImport{SourceID: "mysub", NodeID: "a", Upstream: ServerConfigUpStream{Name: "sub_mysub_a", ProxyUrl: "socks5://127.0.0.1:1080", Sleep: 80}}
}

func TestSubscriptionTOMLImportPreservesUserFile(t *testing.T) {
	original := "# preserve this\r\naddr='127.0.0.1:7070'\r\nnotes='''\r\n[[UpStreams]]\r\nName=\"fake\"\r\n'''\r\n[[UpStreams]] # manual\r\nName='direct'\r\nProxyUrl='direct://0.0.0.0:0'\r\nDnsResolve=true\r\n"
	imported := sampleSubscriptionImport()
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: original, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.HasPrefix(result.Content, original) || !strings.Contains(result.Content, `Name="sub_mysub_a"`) {
		t.Fatal("original content changed or subscription was not written")
	}
	if len(result.Selections) != 1 || result.Selections[0].SourceID != "mysub" {
		t.Fatal("TOML ownership was not recorded")
	}
	again, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if again.Content != result.Content {
		t.Fatalf("reselect modified an unchanged import:\n%s", again.Content)
	}
}

func TestSubscriptionTOMLRefreshKeepsCustomizations(t *testing.T) {
	imported := sampleSubscriptionImport()
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: "addr=\"127.0.0.1:7070\"\n", Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	custom := strings.Replace(result.Content, "Sleep=80", "Sleep=15 # user override", 1) + "\n[[UpStreams.Whitelist]]\nPath='custom.txt'\nType='suffix'\n"
	imported.Upstream.ProxyUrl = "socks5://127.0.0.1:1081"
	result, err = editSubscriptionTOML(subscriptionTOMLRequest{Content: custom, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result.Content, "Sleep=15 # user override") || !strings.Contains(result.Content, "Path='custom.txt'") || !strings.Contains(result.Content, "127.0.0.1:1081") {
		t.Fatalf("refresh lost customizations or failed to update untouched URL:\n%s", result.Content)
	}
	imported.LockedFields = []string{"Sleep"}
	result, err = editSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(result.Content, "Sleep=80 # user override") {
		t.Fatal("locked field was not restored")
	}
}

func TestSubscriptionTOMLChainRenameAndReferencedRemoval(t *testing.T) {
	imported := sampleSubscriptionImport()
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: "addr=\"127.0.0.1:7070\"\n", Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	withChain := result.Content + "\n[[UpStreams]]\nName='chain'\nProxyUrl='http://127.0.0.1:8080?upProxy=sub_mysub_a'\n"
	softRemoved, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: withChain, Removals: []subscriptionIdentity{{SourceID: "mysub", NodeID: "a"}}})
	if err != nil || !strings.Contains(softRemoved.Content, "Remove=true") || !strings.Contains(softRemoved.Content, "upProxy=sub_mysub_a") {
		t.Fatal("referenced upstream was not soft-removed")
	}
	imported.Upstream.Name = "sub_office_a"
	renamed, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: withChain, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(renamed.Content, "upProxy=sub_office_a") {
		t.Fatal("managed rename broke chain references")
	}
	removed, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: result.Content, Removals: []subscriptionIdentity{{SourceID: "mysub", NodeID: "a"}}})
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(removed.Content, "Remove=true") || len(removed.Selections) != 0 {
		t.Fatal("unreferenced managed upstream was not soft-removed")
	}
	queried, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: removed.Content})
	if err != nil || len(queried.Selections) != 0 {
		t.Fatalf("disabled node appeared selected: %v", err)
	}
	restored, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: removed.Content, Imports: []subscriptionImport{sampleSubscriptionImport()}})
	if err != nil || len(restored.Selections) != 1 || !strings.Contains(restored.Content, "Remove=false") {
		t.Fatalf("reselection failed to restore node: %v", err)
	}
}

func TestSubscriptionTOMLRejectsManualNameCollision(t *testing.T) {
	_, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: "[[UpStreams]]\nName='sub_mysub_a'\nProxyUrl='http://127.0.0.1:9999'\n", Imports: []subscriptionImport{sampleSubscriptionImport()}})
	if err == nil {
		t.Fatal("manual upstream was overwritten")
	}
}

func TestSubscriptionOwnershipSurvivesVisualSave(t *testing.T) {
	imported := sampleSubscriptionImport()
	initial, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: "addr=\"127.0.0.1:7070\"\n", Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	lines := strings.Split(initial.Content, "\n")
	for i, line := range lines {
		if strings.HasPrefix(line, "[[UpStreams]]") {
			lines[i] = "[[UpStreams]]"
		}
	}
	draft := strings.Replace(strings.Join(lines, "\n"), "Sleep=80", "Sleep=17", 1)
	retained, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: draft, RetainOwnershipFrom: initial.Content})
	if err != nil || len(retained.Selections) != 1 {
		t.Fatalf("ownership lost: %v", err)
	}
	imported.Upstream.Sleep = 120
	updated, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: retained.Content, Imports: []subscriptionImport{imported}})
	if err != nil || !strings.Contains(updated.Content, "Sleep=17") {
		t.Fatalf("visual override lost after refresh: %v", err)
	}
}

func TestSubscriptionTOMLURLRefreshPreservesCustomQuery(t *testing.T) {
	imported := sampleSubscriptionImport()
	imported.Upstream.ProxyUrl = "http://127.0.0.1:8080?upProxy=front"
	imported.Upstream.Credit = -100
	base := "[[UpStreams]]\nName='front'\nProxyUrl='socks5://127.0.0.1:1080'\n"
	result, err := editSubscriptionTOML(subscriptionTOMLRequest{Content: base, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	custom := strings.Replace(result.Content, "http://127.0.0.1:8080?upProxy=front", "http://127.0.0.1:9000?credit=999&extra=yes", 1)
	imported.LockedFields = []string{"Credit", "UpProxy"}
	result, err = editSubscriptionTOML(subscriptionTOMLRequest{Content: custom, Imports: []subscriptionImport{imported}})
	if err != nil {
		t.Fatal(err)
	}
	var config ServerConfig
	if _, err := btoml.Decode(result.Content, &config); err != nil {
		t.Fatal(err)
	}
	url := config.UpStreams[1].ProxyUrl
	if strings.Contains(url, "credit") || !strings.Contains(url, "8080") || !strings.Contains(url, "upProxy=front") || !strings.Contains(url, "extra=yes") {
		t.Fatalf("custom URL locks failed: %s", url)
	}
}

package netchan

import "testing"

func TestBlackIPQuerySnapshotExcludesHostsResolver(t *testing.T) {
	queryRWM.Lock()
	original := queries
	system := systemDNS("")
	hosts := &hostsResolverQuery{resolver: func(string) []DnsRecord { return nil }}
	queries = []queryer{&system, hosts}
	queryRWM.Unlock()
	t.Cleanup(func() {
		queryRWM.Lock()
		queries = original
		queryRWM.Unlock()
	})

	snapshot := blackIPQuerySnapshot()
	if len(snapshot) != 1 {
		t.Fatalf("expected only the system DNS probe, got %d queries", len(snapshot))
	}
	if snapshot[0] != &system {
		t.Fatal("system DNS probe was not preserved")
	}
}

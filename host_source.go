package main

import (
	"bufio"
	"errors"
	"fmt"
	"github.com/GameXG/TcpRoute2/netchan"
	"io"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type hostRuleEntry struct {
	rule routeRule
	ips  []string
}

type hostRuleSource struct {
	config              ServerConfigHostSource
	baseDir             string
	remote              bool
	mu                  sync.RWMutex
	entries             []hostRuleEntry
	modTime             time.Time
	fileSize            int64
	remoteRefreshFailed bool
}

func loadHostRuleSources(configs []ServerConfigHostSource, baseDir string) ([]*hostRuleSource, error) {
	result := make([]*hostRuleSource, 0, len(configs))
	for index, config := range configs {
		source, err := newHostRuleSource(config, baseDir)
		if err != nil {
			return nil, fmt.Errorf("host source %d: %w", index+1, err)
		}
		result = append(result, source)
	}
	return result, nil
}

func newHostRuleSource(config ServerConfigHostSource, baseDir string) (*hostRuleSource, error) {
	config.Path = strings.TrimSpace(config.Path)
	config.Type = strings.ToLower(strings.TrimSpace(config.Type))
	if config.Path == "" {
		return nil, fmt.Errorf("Path is required")
	}
	if config.Type == "" {
		config.Type = "base"
	}
	switch config.Type {
	case "base", "suffix", "pan", "regex":
	default:
		return nil, fmt.Errorf("unsupported Type %q", config.Type)
	}

	source := &hostRuleSource{
		config:  config,
		baseDir: baseDir,
		remote:  isRemoteRulePath(config.Path),
	}
	intervalText := strings.TrimSpace(config.UpdateInterval)
	if intervalText == "" {
		intervalText = "24h"
	}
	interval, err := time.ParseDuration(intervalText)
	if err != nil || interval < time.Minute {
		return nil, fmt.Errorf("invalid UpdateInterval %q (minimum 1m)", intervalText)
	}
	if err := source.reload(); err != nil {
		if source.remote {
			source.mu.Lock()
			source.remoteRefreshFailed = true
			source.mu.Unlock()
			log.Printf("远程 Host 规则 %s 首次载入失败，将在 1 分钟后重试: %v", source.config.Path, err)
		} else if !errors.Is(err, os.ErrNotExist) {
			return nil, err
		} else {
			log.Printf("本地 Host 规则 %s 尚不存在，文件创建后将自动载入", source.localPath())
		}
	}
	if source.remote {
		go source.refreshEvery(interval)
	}
	return source, nil
}

func (source *hostRuleSource) localPath() string {
	if filepath.IsAbs(source.config.Path) {
		return source.config.Path
	}
	return filepath.Join(source.baseDir, source.config.Path)
}

func (source *hostRuleSource) refreshEvery(interval time.Duration) {
	for {
		source.mu.RLock()
		failed := source.remoteRefreshFailed
		source.mu.RUnlock()
		delay := interval
		if failed {
			delay = time.Minute
		}
		timer := time.NewTimer(delay)
		<-timer.C
		if err := source.reload(); err != nil {
			log.Printf("更新 Host 规则 %s 失败: %v", source.config.Path, err)
		}
	}
}

func (source *hostRuleSource) lookup(host string) []netchan.DnsRecord {
	if !source.remote {
		if info, err := os.Stat(source.localPath()); err == nil {
			source.mu.RLock()
			changed := !info.ModTime().Equal(source.modTime) || info.Size() != source.fileSize
			source.mu.RUnlock()
			if changed {
				_ = source.reload()
			}
		}
	}
	host = addressHost(host)
	source.mu.RLock()
	defer source.mu.RUnlock()
	records := make([]netchan.DnsRecord, 0)
	for _, entry := range source.entries {
		if !entry.rule.matches(host) {
			continue
		}
		for _, address := range entry.ips {
			records = append(records, netchan.DnsRecord{Ip: address, Credit: source.config.Credit})
		}
	}
	return records
}

func (source *hostRuleSource) reload() error {
	var (
		data []byte
		err  error
		info os.FileInfo
	)
	if source.remote {
		var fresh bool
		data, fresh, err = downloadRuleSourceWithStatus(source.config.Path, source.baseDir)
		source.mu.Lock()
		source.remoteRefreshFailed = !fresh
		source.mu.Unlock()
	} else {
		path := source.localPath()
		info, err = os.Stat(path)
		if err == nil {
			if info.Size() > maxRuleSourceSize {
				return fmt.Errorf("host source exceeds %d bytes", maxRuleSourceSize)
			}
			data, err = os.ReadFile(path)
		}
	}
	if err != nil {
		return err
	}
	hosts, err := parseHostRules(strings.NewReader(string(data)))
	if err != nil {
		return err
	}
	entries := make([]hostRuleEntry, 0, len(hosts))
	for domain, addresses := range hosts {
		rule, err := newHostDomainRule(source.config.Type, domain)
		if err != nil {
			return fmt.Errorf("host %q: %w", domain, err)
		}
		entries = append(entries, hostRuleEntry{rule: rule, ips: append([]string(nil), addresses...)})
	}
	source.mu.Lock()
	source.entries = entries
	if info != nil {
		source.modTime = info.ModTime()
		source.fileSize = info.Size()
	}
	source.mu.Unlock()
	return nil
}

func newHostDomainRule(ruleType, value string) (routeRule, error) {
	switch ruleType {
	case "base":
		return newDomainRule(ruleDomain, value)
	case "suffix":
		return newDomainRule(ruleDomainSuffix, strings.TrimPrefix(value, "."))
	case "pan":
		return newPanRule(value)
	case "regex":
		return newRegexRule(value)
	default:
		return routeRule{}, fmt.Errorf("unsupported host Type %q", ruleType)
	}
}

// parseHostRules intentionally follows the v0.5.14 hosts format: the first
// field is the candidate address and the rest of the line is one match key.
// Invalid rows are skipped, and repeated keys retain all candidate addresses.
func parseHostRules(reader io.Reader) (map[string][]string, error) {
	hosts := make(map[string][]string)
	scanner := bufio.NewScanner(reader)
	for scanner.Scan() {
		rawLine := scanner.Text()
		line := strings.TrimSpace(rawLine)
		if line == "" || strings.HasPrefix(line, "#") {
			continue
		}
		fields := strings.Fields(line)
		if len(fields) < 2 {
			log.Printf("解析 hosts 文件时跳过错误行: %s", rawLine)
			continue
		}
		address := fields[0]
		domain := strings.TrimSpace(strings.TrimPrefix(line, address))
		if domain == "" {
			log.Printf("解析 hosts 文件时跳过错误行: %s", rawLine)
			continue
		}
		hosts[domain] = append(hosts[domain], address)
	}
	if err := scanner.Err(); err != nil {
		return nil, err
	}
	return hosts, nil
}

func (su *tcppingUpStream) lookupHostRecords(domain string) []netchan.DnsRecord {
	records := make([]netchan.DnsRecord, 0)
	for _, source := range su.hosts {
		records = append(records, source.lookup(domain)...)
	}
	return records
}

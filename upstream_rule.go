package main

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"time"

	xrouter "github.com/xtls/xray-core/app/router"
	"github.com/xtls/xray-core/common/platform"
	"google.golang.org/protobuf/proto"
)

const maxRuleSourceSize = 64 << 20

type ruleKind uint8

const (
	ruleDomain ruleKind = iota
	ruleDomainSuffix
	ruleDomainKeyword
	ruleDomainRegex
	ruleIPCIDR
)

type routeRule struct {
	kind    ruleKind
	value   string
	regex   *regexp.Regexp
	network *net.IPNet
}

type domainRuleSource struct {
	config              ServerConfigDomainList
	baseDir             string
	remote              bool
	geosite             bool
	geoip               bool
	mu                  sync.RWMutex
	rules               []routeRule
	geoMatch            *xrouter.DomainMatcher
	ipMatch             xrouter.GeoIPMatcher
	modTime             time.Time
	fileSize            int64
	remoteRefreshFailed bool
}

func loadDomainRuleSources(configs []ServerConfigDomainList, baseDir string) ([]*domainRuleSource, error) {
	result := make([]*domainRuleSource, 0, len(configs))
	for index, config := range configs {
		source, err := newDomainRuleSource(config, baseDir)
		if err != nil {
			return nil, fmt.Errorf("rule source %d: %w", index+1, err)
		}
		result = append(result, source)
	}
	return result, nil
}

func newDomainRuleSource(config ServerConfigDomainList, baseDir string) (*domainRuleSource, error) {
	config.Type = strings.ToLower(strings.TrimSpace(config.Type))
	config.Path = strings.TrimSpace(config.Path)
	if config.Type == "geosite" {
		selectors, err := parseGeoSiteReference(config.Path)
		if err != nil {
			return nil, err
		}
		parts := make([]string, len(selectors))
		for i, selector := range selectors {
			parts[i] = selector.tag
			for _, attr := range selector.attrs {
				parts[i] += "@" + attr
			}
		}
		config.Type = "geosite:" + strings.Join(parts, ",")
		path, err := executableGeoSitePath()
		if err != nil {
			return nil, err
		}
		config.Path = path
	}
	if config.Type == "geoip" {
		tags, err := parseGeoIPReference(config.Path)
		if err != nil {
			return nil, err
		}
		config.Type = "geoip:" + strings.Join(tags, ",")
		path, err := executableGeoDataPath("geoip.dat")
		if err != nil {
			return nil, err
		}
		config.Path = path
	}
	isGeoSite := isGeoSiteReference(config.Type)
	isGeoIP := isGeoIPReference(config.Type)
	if config.Path == "" && isGeoSite {
		config.Path = "geosite.dat"
	}
	if config.Path == "" && isGeoIP {
		config.Path = "geoip.dat"
	}
	if config.Path == "" {
		return nil, fmt.Errorf("Path is required")
	}
	if !isGeoSite && !isGeoIP {
		if config.Type == "" {
			config.Type = "base"
		}
		switch config.Type {
		case "base", "suffix", "pan", "regex":
		default:
			return nil, fmt.Errorf("unsupported Type %q", config.Type)
		}
	}

	source := &domainRuleSource{
		config:  config,
		baseDir: baseDir,
		remote:  isRemoteRulePath(config.Path),
		geosite: isGeoSite,
		geoip:   isGeoIP,
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
			log.Printf("远程规则 %s 首次载入失败，将在 1 分钟后重试: %v", source.config.Path, err)
		} else if source.geosite || source.geoip || !errors.Is(err, os.ErrNotExist) {
			return nil, err
		} else {
			log.Printf("本地规则 %s 尚不存在，文件创建后将自动载入", source.localPath())
		}
	}
	if source.remote {
		go source.refreshEvery(interval)
	}
	return source, nil
}

func isRemoteRulePath(path string) bool {
	parsed, err := url.Parse(path)
	return err == nil && (parsed.Scheme == "http" || parsed.Scheme == "https")
}

func (source *domainRuleSource) refreshEvery(interval time.Duration) {
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
			log.Printf("更新规则 %s 失败: %v", source.config.Path, err)
		}
	}
}

func (source *domainRuleSource) matches(host string) bool {
	if !source.remote {
		path := source.resolvedLocalPath()
		if info, err := os.Stat(path); err == nil {
			source.mu.RLock()
			changed := !info.ModTime().Equal(source.modTime) || info.Size() != source.fileSize
			source.mu.RUnlock()
			if changed {
				_ = source.reload()
			}
		}
	}
	source.mu.RLock()
	defer source.mu.RUnlock()
	if source.geoMatch != nil {
		return source.geoMatch.ApplyDomain(host)
	}
	if source.ipMatch != nil {
		return source.ipMatch.Match(net.ParseIP(host))
	}
	for _, rule := range source.rules {
		if rule.matches(host) {
			return true
		}
	}
	return false
}

func (source *domainRuleSource) localPath() string {
	if filepath.IsAbs(source.config.Path) {
		return source.config.Path
	}
	return filepath.Join(source.baseDir, source.config.Path)
}

func (source *domainRuleSource) resolvedLocalPath() string {
	path := source.localPath()
	if (source.geosite || source.geoip) && !filepath.IsAbs(source.config.Path) {
		if _, err := os.Stat(path); err != nil {
			path = platform.GetAssetLocation(source.config.Path)
		}
	}
	return path
}

func (source *domainRuleSource) reload() error {
	if source.geosite {
		matcher, fresh, err := loadGeoSiteMatcherWithStatus(source.config.Type, source.config.Path, source.baseDir)
		if err != nil {
			return err
		}
		source.mu.Lock()
		source.geoMatch = matcher
		source.ipMatch = nil
		source.rules = nil
		source.remoteRefreshFailed = source.remote && !fresh
		source.captureLocalFileState()
		source.mu.Unlock()
		return nil
	}
	if source.geoip {
		matcher, fresh, err := loadGeoIPMatcherWithStatus(source.config.Type, source.config.Path, source.baseDir)
		if err != nil {
			return err
		}
		source.mu.Lock()
		source.ipMatch = matcher
		source.geoMatch = nil
		source.rules = nil
		source.remoteRefreshFailed = source.remote && !fresh
		source.captureLocalFileState()
		source.mu.Unlock()
		return nil
	}

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
				return fmt.Errorf("rule source exceeds %d bytes", maxRuleSourceSize)
			}
			data, err = os.ReadFile(path)
		}
	}
	if err != nil {
		return err
	}
	rules, err := parseDomainRules(string(data), source.config.Type)
	if err != nil {
		return err
	}
	source.mu.Lock()
	source.rules = rules
	source.geoMatch = nil
	source.ipMatch = nil
	if info != nil {
		source.modTime = info.ModTime()
		source.fileSize = info.Size()
	}
	source.mu.Unlock()
	return nil
}

func (source *domainRuleSource) captureLocalFileState() {
	if source.remote {
		return
	}
	if info, err := os.Stat(source.resolvedLocalPath()); err == nil {
		source.modTime = info.ModTime()
		source.fileSize = info.Size()
	}
}

func (su *tcppingUpStream) selectDialClients(address string) ([]DialClient, bool) {
	host := addressHost(address)
	result := make([]DialClient, 0, len(su.dc))
	whitelistSelected := false
	for _, client := range su.dc {
		if anyRuleSourceMatches(client.whitelist, host) {
			if !whitelistSelected {
				whitelistSelected = true
				result = result[:0]
			}
			result = append(result, client)
			continue
		}
		if !whitelistSelected && !anyRuleSourceMatches(client.blacklist, host) {
			result = append(result, client)
		}
	}
	return result, len(result) != len(su.dc)
}

func anyRuleSourceMatches(sources []*domainRuleSource, host string) bool {
	for _, source := range sources {
		if source.matches(host) {
			return true
		}
	}
	return false
}

func addressHost(address string) string {
	host := address
	if parsed, _, err := net.SplitHostPort(address); err == nil {
		host = parsed
	}
	return strings.TrimSuffix(strings.ToLower(strings.Trim(host, "[]")), ".")
}

func (r routeRule) matches(host string) bool {
	switch r.kind {
	case ruleDomain:
		return host == r.value
	case ruleDomainSuffix:
		return host == r.value || strings.HasSuffix(host, "."+r.value)
	case ruleDomainKeyword:
		return strings.Contains(host, r.value)
	case ruleDomainRegex:
		return r.regex != nil && r.regex.MatchString(host)
	case ruleIPCIDR:
		return r.network != nil && r.network.Contains(net.ParseIP(host))
	default:
		return false
	}
}

func downloadRuleSource(source, baseDir string) ([]byte, error) {
	data, _, err := downloadRuleSourceWithStatus(source, baseDir)
	return data, err
}

// downloadRuleSourceWithStatus reports whether the data came from the remote
// endpoint. A cached fallback remains usable, but callers retry it after one
// minute as TcpRoute2 v0.5.14 did after a failed download.
func downloadRuleSourceWithStatus(source, baseDir string) ([]byte, bool, error) {
	parsed, err := url.Parse(source)
	if err != nil || (parsed.Scheme != "http" && parsed.Scheme != "https") {
		return nil, false, fmt.Errorf("invalid remote URL %q", source)
	}
	cacheDir := filepath.Join(baseDir, ".rule-cache")
	hash := sha256.Sum256([]byte(source))
	cachePath := filepath.Join(cacheDir, hex.EncodeToString(hash[:])+".rules")

	client := &http.Client{Timeout: 15 * time.Second}
	response, requestErr := client.Get(source)
	if requestErr == nil {
		defer response.Body.Close()
		if response.StatusCode >= 200 && response.StatusCode < 300 {
			data, readErr := io.ReadAll(io.LimitReader(response.Body, maxRuleSourceSize+1))
			if readErr == nil && len(data) <= maxRuleSourceSize {
				if err := os.MkdirAll(cacheDir, 0o700); err == nil {
					_ = os.WriteFile(cachePath, data, 0o600)
				}
				return data, true, nil
			}
			if readErr != nil {
				requestErr = readErr
			} else {
				requestErr = fmt.Errorf("rule source exceeds %d bytes", maxRuleSourceSize)
			}
		} else {
			requestErr = fmt.Errorf("remote server returned %s", response.Status)
		}
	}
	if cached, cacheErr := os.ReadFile(cachePath); cacheErr == nil {
		log.Printf("远程规则更新失败，使用缓存 %s: %v", source, requestErr)
		return cached, false, nil
	}
	return nil, false, requestErr
}

func parseDomainRules(content, ruleType string) ([]routeRule, error) {
	rules := make([]routeRule, 0)
	for lineNumber, rawLine := range strings.Split(content, "\n") {
		line := strings.TrimSpace(strings.TrimSuffix(rawLine, "\r"))
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, "//") || strings.HasPrefix(line, ";") {
			continue
		}
		if comment := strings.Index(line, " #"); comment >= 0 {
			line = strings.TrimSpace(line[:comment])
		}
		var rule routeRule
		var err error
		switch ruleType {
		case "base":
			rule, err = newDomainRule(ruleDomain, line)
		case "suffix":
			rule, err = newDomainRule(ruleDomainSuffix, strings.TrimPrefix(line, "."))
		case "pan":
			rule, err = newPanRule(line)
		case "regex":
			rule, err = newRegexRule(line)
		default:
			err = fmt.Errorf("unsupported rule type %q", ruleType)
		}
		if err != nil {
			return nil, fmt.Errorf("line %d: %w", lineNumber+1, err)
		}
		rules = append(rules, rule)
	}
	return rules, nil
}

func newPanRule(value string) (routeRule, error) {
	value = strings.ToLower(strings.TrimSpace(value))
	if value == "" {
		return routeRule{}, fmt.Errorf("empty pan rule")
	}
	var expression strings.Builder
	expression.WriteString("^")
	for _, current := range value {
		switch current {
		case '*':
			expression.WriteString("[^.]+")
		case '?':
			expression.WriteString("[^.]")
		default:
			expression.WriteString(regexp.QuoteMeta(string(current)))
		}
	}
	expression.WriteString("$")
	return newRegexRule(expression.String())
}

func newDomainRule(kind ruleKind, value string) (routeRule, error) {
	value = strings.TrimSuffix(strings.ToLower(strings.TrimSpace(value)), ".")
	if value == "" {
		return routeRule{}, fmt.Errorf("empty domain rule")
	}
	return routeRule{kind: kind, value: value}, nil
}

func newRegexRule(value string) (routeRule, error) {
	compiled, err := regexp.Compile(value)
	if err != nil {
		return routeRule{}, fmt.Errorf("invalid regex %q: %w", value, err)
	}
	return routeRule{kind: ruleDomainRegex, regex: compiled}, nil
}

func newCIDRRule(value string) (routeRule, error) {
	_, network, err := net.ParseCIDR(strings.TrimSpace(value))
	if err != nil {
		return routeRule{}, fmt.Errorf("invalid CIDR %q", value)
	}
	return routeRule{kind: ruleIPCIDR, network: network}, nil
}

func isGeoSiteReference(value string) bool {
	return strings.HasPrefix(strings.ToLower(strings.TrimSpace(value)), "geosite:")
}

func isGeoIPReference(value string) bool {
	return strings.HasPrefix(strings.ToLower(strings.TrimSpace(value)), "geoip:")
}

type geoSiteSelector struct {
	tag   string
	attrs []string
}

func parseGeoSiteReference(value string) ([]geoSiteSelector, error) {
	value = strings.TrimSpace(value)
	if !isGeoSiteReference(value) {
		return nil, fmt.Errorf("invalid geosite rule %q", value)
	}
	rawSelectors := strings.Split(value[len("geosite:"):], ",")
	selectors := make([]geoSiteSelector, 0, len(rawSelectors))
	for _, rawSelector := range rawSelectors {
		rawSelector = strings.TrimSpace(rawSelector)
		if isGeoSiteReference(rawSelector) {
			rawSelector = rawSelector[len("geosite:"):]
		}
		parts := strings.Split(strings.TrimSpace(rawSelector), "@")
		tag := strings.ToLower(strings.TrimSpace(parts[0]))
		if tag == "" || strings.ContainsAny(tag, "/\\:, ") {
			return nil, fmt.Errorf("invalid geosite rule %q", value)
		}
		attrs := make([]string, 0, len(parts)-1)
		for _, raw := range parts[1:] {
			attr := strings.ToLower(strings.TrimSpace(raw))
			if attr == "" || strings.ContainsAny(attr, "/\\:, ") {
				return nil, fmt.Errorf("invalid geosite rule %q", value)
			}
			attrs = append(attrs, attr)
		}
		selectors = append(selectors, geoSiteSelector{tag: tag, attrs: attrs})
	}
	return selectors, nil
}

func executableGeoSitePath() (string, error) {
	return executableGeoDataPath("geosite.dat")
}

func executableGeoDataPath(name string) (string, error) {
	// Android installs executables in a read-only native-library directory.
	if runtime.GOOS == "android" {
		return platform.GetAssetLocation(name), nil
	}
	executable, err := os.Executable()
	if err != nil {
		return "", fmt.Errorf("locate TcpRoute2 executable: %w", err)
	}
	return filepath.Join(filepath.Dir(executable), name), nil
}

func parseGeoIPReference(value string) ([]string, error) {
	value = strings.TrimSpace(value)
	if !isGeoIPReference(value) {
		return nil, fmt.Errorf("invalid geoip rule %q", value)
	}
	parts := strings.Split(value[len("geoip:"):], ",")
	result := make([]string, 0, len(parts))
	for _, raw := range parts {
		tag := strings.ToLower(strings.TrimSpace(raw))
		tag = strings.TrimPrefix(tag, "geoip:")
		if tag == "" || strings.ContainsAny(tag, "/\\:, @") {
			return nil, fmt.Errorf("invalid geoip rule %q", value)
		}
		result = append(result, tag)
	}
	return result, nil
}

func loadGeoSiteMatcher(reference, dataPath, baseDir string) (*xrouter.DomainMatcher, error) {
	matcher, _, err := loadGeoSiteMatcherWithStatus(reference, dataPath, baseDir)
	return matcher, err
}

func loadGeoSiteMatcherWithStatus(reference, dataPath, baseDir string) (*xrouter.DomainMatcher, bool, error) {
	selectors, err := parseGeoSiteReference(reference)
	if err != nil {
		return nil, false, err
	}

	var data []byte
	fresh := true
	if isRemoteRulePath(dataPath) {
		data, fresh, err = downloadRuleSourceWithStatus(dataPath, baseDir)
	} else {
		path := dataPath
		if !filepath.IsAbs(path) {
			path = filepath.Join(baseDir, path)
		}
		if _, err := os.Stat(path); err != nil && !filepath.IsAbs(dataPath) {
			path = platform.GetAssetLocation(dataPath)
		}
		info, statErr := os.Stat(path)
		if statErr != nil {
			return nil, false, fmt.Errorf("load %s from %s: data file not found: %w", reference, dataPath, statErr)
		}
		if info.Size() > maxRuleSourceSize {
			return nil, false, fmt.Errorf("geosite data file %s exceeds %d bytes", dataPath, maxRuleSourceSize)
		}
		data, err = os.ReadFile(path)
	}
	if err != nil {
		return nil, false, fmt.Errorf("read geosite data from %s: %w", dataPath, err)
	}
	var list xrouter.GeoSiteList
	if err := proto.Unmarshal(data, &list); err != nil {
		return nil, false, fmt.Errorf("parse geosite data file %s: %w", dataPath, err)
	}

	domains := make([]*xrouter.Domain, 0)
	for _, selector := range selectors {
		found := false
		for _, site := range list.Entry {
			if !strings.EqualFold(site.CountryCode, selector.tag) {
				continue
			}
			found = true
			selected := site.Domain
			if len(selector.attrs) > 0 {
				filtered := make([]*xrouter.Domain, 0, len(selected))
				for _, domain := range selected {
					if geoSiteDomainHasAttrs(domain, selector.attrs) {
						filtered = append(filtered, domain)
					}
				}
				selected = filtered
			}
			if len(selected) == 0 {
				return nil, false, fmt.Errorf("geosite rule %q contains no domains", selector.tag)
			}
			domains = append(domains, selected...)
			break
		}
		if !found {
			return nil, false, fmt.Errorf("geosite rule %q not found in geosite.dat", selector.tag)
		}
	}
	matcher, err := xrouter.NewMphMatcherGroup(domains)
	if err != nil {
		return nil, false, fmt.Errorf("build geosite matcher %q: %w", reference, err)
	}
	return matcher, fresh, nil
}

func loadGeoIPMatcherWithStatus(reference, dataPath, baseDir string) (xrouter.GeoIPMatcher, bool, error) {
	tags, err := parseGeoIPReference(reference)
	if err != nil {
		return nil, false, err
	}
	var data []byte
	fresh := true
	if isRemoteRulePath(dataPath) {
		data, fresh, err = downloadRuleSourceWithStatus(dataPath, baseDir)
	} else {
		path := dataPath
		if !filepath.IsAbs(path) {
			path = filepath.Join(baseDir, path)
		}
		if _, statErr := os.Stat(path); statErr != nil && !filepath.IsAbs(dataPath) {
			path = platform.GetAssetLocation(dataPath)
		}
		info, statErr := os.Stat(path)
		if statErr != nil {
			return nil, false, fmt.Errorf("load %s from %s: data file not found: %w", reference, dataPath, statErr)
		}
		if info.Size() > maxRuleSourceSize {
			return nil, false, fmt.Errorf("geoip data file %s exceeds %d bytes", dataPath, maxRuleSourceSize)
		}
		data, err = os.ReadFile(path)
	}
	if err != nil {
		return nil, false, fmt.Errorf("read geoip data from %s: %w", dataPath, err)
	}
	var list xrouter.GeoIPList
	if err := proto.Unmarshal(data, &list); err != nil {
		return nil, false, fmt.Errorf("parse geoip data file %s: %w", dataPath, err)
	}
	selected := make([]*xrouter.GeoIP, 0, len(tags))
	for _, tag := range tags {
		found := false
		for _, entry := range list.Entry {
			if strings.EqualFold(entry.CountryCode, tag) {
				selected = append(selected, entry)
				found = true
			}
		}
		if !found {
			return nil, false, fmt.Errorf("geoip rule %q not found in geoip.dat", tag)
		}
	}
	matcher, err := xrouter.BuildOptimizedGeoIPMatcher(selected...)
	if err != nil {
		return nil, false, fmt.Errorf("build geoip matcher %q: %w", reference, err)
	}
	return matcher, fresh, nil
}

func geoSiteDomainHasAttrs(domain *xrouter.Domain, required []string) bool {
	for _, expected := range required {
		found := false
		for _, attr := range domain.Attribute {
			if strings.EqualFold(attr.Key, expected) {
				found = true
				break
			}
		}
		if !found {
			return false
		}
	}
	return true
}

package main

import (
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/url"
	"os"
	"sort"
	"strings"
	"unicode/utf8"

	btoml "github.com/BurntSushi/toml"
	pt "github.com/pelletier/go-toml"
)

const subscriptionMarker = "# TcpRoute2 subscription "

type subscriptionImport struct {
	SourceID     string `json:"SourceId"`
	NodeID       string `json:"NodeId"`
	NodeName     string
	Upstream     ServerConfigUpStream
	LockedFields []string
}
type subscriptionIdentity struct {
	SourceID string `json:"SourceId"`
	NodeID   string `json:"NodeId"`
	NodeName string `json:",omitempty"`
	Delete   bool   `json:",omitempty"`
	Remove   bool   `json:",omitempty"`
}
type subscriptionTOMLRequest struct {
	Content             string
	Imports             []subscriptionImport
	Removals            []subscriptionIdentity
	RetainOwnershipFrom string
	IncludeRemoved      bool
}
type subscriptionTOMLResult struct {
	Content    string
	Selections []subscriptionIdentity
}
type subscriptionMetadata struct {
	SourceID      string `json:"SourceId"`
	NodeID        string `json:"NodeId"`
	Baseline      map[string]string
	NodeName      string            `json:",omitempty"`
	QueryBaseline map[string]string `json:",omitempty"`
}
type subscriptionTextEdit struct {
	start, end int
	value      string
}

func subscriptionFingerprint(value any) string {
	data, _ := json.Marshal(value)
	hash := sha256.Sum256(data)
	return hex.EncodeToString(hash[:16])
}

func editSubscriptionTOML(request subscriptionTOMLRequest) (subscriptionTOMLResult, error) {
	if request.RetainOwnershipFrom != "" {
		if len(request.Imports) != 0 || len(request.Removals) != 0 {
			return subscriptionTOMLResult{}, fmt.Errorf("cannot combine ownership retention with subscription imports")
		}
		content, err := retainSubscriptionOwnership(request.Content, request.RetainOwnershipFrom)
		if err != nil {
			return subscriptionTOMLResult{}, err
		}
		return editSubscriptionTOML(subscriptionTOMLRequest{Content: content})
	}
	result := subscriptionTOMLResult{Content: request.Content, Selections: []subscriptionIdentity{}}
	tree, err := pt.Load(request.Content)
	if err != nil {
		return result, fmt.Errorf("invalid TOML; fix the configuration before importing subscriptions")
	}
	entries, _ := tree.Get("UpStreams").([]*pt.Tree)
	if tree.Get("UpStreams") != nil && entries == nil {
		return result, fmt.Errorf("subscription imports require [[UpStreams]] tables")
	}
	lines := strings.SplitAfter(request.Content, "\n")
	lineOffsets := make([]int, len(lines))
	for i := 1; i < len(lines); i++ {
		lineOffsets[i] = lineOffsets[i-1] + len(lines[i-1])
	}
	lineOffset := func(pos pt.Position) int {
		if pos.Line < 1 || pos.Line > len(lines) {
			return -1
		}
		text := lines[pos.Line-1]
		byteOffset := 0
		for col := 1; col < pos.Col && byteOffset < len(text); col++ {
			_, n := utf8.DecodeRuneInString(text[byteOffset:])
			byteOffset += n
		}
		return lineOffsets[pos.Line-1] + byteOffset
	}
	newline := "\n"
	if strings.Contains(request.Content, "\r\n") {
		newline = "\r\n"
	}
	owned := map[string]*pt.Tree{}
	metadata := map[*pt.Tree]subscriptionMetadata{}
	for _, entry := range entries {
		pos := entry.Position()
		if pos.Line < 1 || pos.Line > len(lines) {
			return result, fmt.Errorf("invalid upstream source position")
		}
		header := lines[pos.Line-1]
		if !strings.HasPrefix(strings.TrimSpace(header), "[[") {
			return result, fmt.Errorf("subscription imports require [[UpStreams]] tables")
		}
		marker := strings.Index(header, subscriptionMarker)
		if marker < 0 {
			continue
		}
		data, decodeErr := base64.RawURLEncoding.DecodeString(strings.TrimSpace(header[marker+len(subscriptionMarker):]))
		var meta subscriptionMetadata
		if decodeErr != nil || json.Unmarshal(data, &meta) != nil || meta.SourceID == "" || meta.NodeID == "" || meta.Baseline == nil {
			return result, fmt.Errorf("invalid subscription ownership marker")
		}
		key := meta.SourceID + "/" + meta.NodeID
		if owned[key] != nil {
			return result, fmt.Errorf("duplicate imported subscription node")
		}
		owned[key], metadata[entry] = entry, meta
	}
	if len(request.Imports) == 0 && len(request.Removals) == 0 {
		for _, entry := range entries {
			if meta, ok := metadata[entry]; ok {
				if request.IncludeRemoved || entry.Get("Remove") != true {
					result.Selections = append(result.Selections, subscriptionIdentity{SourceID: meta.SourceID, NodeID: meta.NodeID, NodeName: meta.NodeName, Remove: entry.Get("Remove") == true})
				}
			}
		}
		return result, nil
	}
	var edits []subscriptionTextEdit
	removed := map[*pt.Tree]bool{}
	disabled := map[*pt.Tree]bool{}
	for _, identity := range request.Removals {
		if entry := owned[identity.SourceID+"/"+identity.NodeID]; entry != nil {
			if identity.Delete {
				removed[entry] = true
			} else {
				disabled[entry] = true
			}
		}
	}
	renamed := map[string]string{}
	for _, imported := range request.Imports {
		if entry := owned[imported.SourceID+"/"+imported.NodeID]; entry != nil {
			if oldName, ok := entry.Get("Name").(string); ok && oldName != "" && oldName != imported.Upstream.Name {
				renamed[oldName] = imported.Upstream.Name
			}
		}
	}
	fieldValues := func(config ServerConfigUpStream) map[string]any {
		return map[string]any{"Name": config.Name, "ProxyUrl": config.ProxyUrl, "DialProxyFor": config.DialProxyFor, "Remove": config.Remove, "DnsResolve": config.DnsResolve, "Credit": int64(config.Credit), "Sleep": int64(config.Sleep), "CorrectDelay": int64(config.CorrectDelay)}
	}
	setScalar := func(entry *pt.Tree, key string, value any) error {
		encoded, encodeErr := json.Marshal(value)
		if encodeErr != nil {
			return encodeErr
		}
		if !entry.Has(key) {
			pos := entry.Position()
			at := lineOffsets[pos.Line-1] + len(lines[pos.Line-1])
			separator := ""
			if !strings.HasSuffix(lines[pos.Line-1], "\n") {
				separator = newline
			}
			edits = append(edits, subscriptionTextEdit{at, at, separator + key + "=" + string(encoded) + newline})
			return nil
		}
		start := lineOffset(entry.GetPosition(key))
		if start < 0 {
			return fmt.Errorf("invalid upstream field position")
		}
		from, to, spanErr := subscriptionScalarSpan(request.Content, start)
		if spanErr != nil {
			return spanErr
		}
		edits = append(edits, subscriptionTextEdit{from, to, string(encoded)})
		return nil
	}
	appendix := ""
	for _, imported := range request.Imports {
		if imported.SourceID == "" || imported.NodeID == "" || !strings.HasPrefix(imported.Upstream.Name, "sub_") {
			return result, fmt.Errorf("invalid subscription import identity or managed name")
		}
		updatedURL, rewriteErr := renameSubscriptionReferences(imported.Upstream.ProxyUrl, renamed, 0)
		if rewriteErr != nil {
			return result, rewriteErr
		}
		imported.Upstream.ProxyUrl = updatedURL
		if replacement, ok := renamed[imported.Upstream.DialProxyFor]; ok {
			imported.Upstream.DialProxyFor = replacement
		}
		values := fieldValues(imported.Upstream)
		meta := subscriptionMetadata{SourceID: imported.SourceID, NodeID: imported.NodeID, NodeName: imported.NodeName, Baseline: map[string]string{}}
		parsedURL, parseErr := url.Parse(imported.Upstream.ProxyUrl)
		if parseErr != nil {
			return result, fmt.Errorf("invalid subscribed URL")
		}
		for key, values := range parsedURL.Query() {
			if meta.QueryBaseline == nil {
				meta.QueryBaseline = map[string]string{}
			}
			meta.QueryBaseline[key] = subscriptionFingerprint(values)
		}
		for key, value := range values {
			meta.Baseline[key] = subscriptionFingerprint(value)
		}
		entry := owned[imported.SourceID+"/"+imported.NodeID]
		metaJSON, _ := json.Marshal(meta)
		header := "[[UpStreams]] " + subscriptionMarker + base64.RawURLEncoding.EncodeToString(metaJSON) + newline
		if entry == nil {
			for _, other := range entries {
				if other.Get("Name") == imported.Upstream.Name {
					return result, fmt.Errorf("upstream name %q already belongs to an existing entry", imported.Upstream.Name)
				}
			}
			appendix += newline + header
			for _, key := range []string{"Name", "ProxyUrl", "DialProxyFor", "Remove", "DnsResolve", "Credit", "Sleep", "CorrectDelay"} {
				if key == "DialProxyFor" && values[key] == "" {
					continue
				}
				encoded, _ := json.Marshal(values[key])
				appendix += key + "=" + string(encoded) + newline
			}
			continue
		}
		if removed[entry] || disabled[entry] {
			return result, fmt.Errorf("cannot import and remove the same subscription node")
		}
		oldName, _ := entry.Get("Name").(string)
		if oldName != "" && oldName != imported.Upstream.Name {
			renamed[oldName] = imported.Upstream.Name
		}
		old := metadata[entry]
		for _, key := range []string{"Name", "ProxyUrl", "DialProxyFor", "Remove", "DnsResolve", "Credit", "Sleep", "CorrectDelay"} {
			if key == "DialProxyFor" && values[key] == "" && !entry.Has(key) {
				continue
			}
			locked := false
			for _, field := range imported.LockedFields {
				if field == key {
					locked = true
				}
			}
			value := values[key]
			if key == "ProxyUrl" {
				current, _ := entry.Get(key).(string)
				updated, mergeErr := mergeSubscriptionURL(current, imported.Upstream.ProxyUrl, old)
				if mergeErr != nil {
					return result, mergeErr
				}
				updated, mergeErr = applySubscriptionURLLocks(updated, imported.Upstream.ProxyUrl, imported.LockedFields)
				if mergeErr != nil {
					return result, mergeErr
				}
				if setErr := setScalar(entry, key, updated); setErr != nil {
					return result, setErr
				}
			} else if key == "Name" || key == "Remove" || locked || !entry.Has(key) || subscriptionFingerprint(entry.Get(key)) == old.Baseline[key] {
				if setErr := setScalar(entry, key, value); setErr != nil {
					return result, setErr
				}
			}
		}
		pos := entry.Position()
		edits = append(edits, subscriptionTextEdit{lineOffsets[pos.Line-1], lineOffsets[pos.Line-1] + len(lines[pos.Line-1]), header})
	}
	for entry := range disabled {
		if err := setScalar(entry, "Remove", true); err != nil {
			return result, err
		}
	}
	// Tree positions identify real tables, so multiline strings containing fake
	// table headers cannot affect ownership, deletion boundaries, or edits.
	var tables []*pt.Tree
	var collect func(*pt.Tree)
	collect = func(node *pt.Tree) {
		if removed[node] {
			return
		}
		tables = append(tables, node)
		for _, key := range node.Keys() {
			switch child := node.GetPath([]string{key}).(type) {
			case *pt.Tree:
				collect(child)
			case []*pt.Tree:
				for _, item := range child {
					collect(item)
				}
			}
		}
	}
	collect(tree)
	for entry := range removed {
		start := lineOffsets[entry.Position().Line-1]
		end := len(request.Content)
		for _, other := range tables {
			pos := other.Position()
			if pos.Line > entry.Position().Line && pos.Line <= len(lines) {
				at := lineOffsets[pos.Line-1]
				if at < end {
					end = at
				}
			}
		}
		// Consecutive removed entries form one deletion, avoiding overlapping edits.
		for other := range removed {
			if other != entry && other.Position().Line > entry.Position().Line {
				at := lineOffsets[other.Position().Line-1]
				if at < end {
					end = at
				}
			}
		}
		edits = append(edits, subscriptionTextEdit{start, end, ""})
	}
	for _, entry := range entries {
		if removed[entry] {
			continue
		}
		if current, ok := entry.Get("DialProxyFor").(string); ok {
			if replacement, renaming := renamed[current]; renaming {
				from, to, spanErr := subscriptionScalarSpan(request.Content, lineOffset(entry.GetPosition("DialProxyFor")))
				if spanErr != nil {
					return result, spanErr
				}
				found := false
				for index := range edits {
					if edits[index].start == from && edits[index].end == to {
						var value string
						if json.Unmarshal([]byte(edits[index].value), &value) == nil {
							if renamedValue, ok := renamed[value]; ok {
								encoded, _ := json.Marshal(renamedValue)
								edits[index].value = string(encoded)
							}
							found = true
						}
						break
					}
				}
				if !found {
					if err := setScalar(entry, "DialProxyFor", replacement); err != nil {
						return result, err
					}
				}
			}
		}
		current, _ := entry.Get("ProxyUrl").(string)
		updated, rewriteErr := renameSubscriptionReferences(current, renamed, 0)
		if rewriteErr != nil {
			return result, rewriteErr
		}
		if updated != current {
			// Imports may already have replaced this field; rewrite its new value.
			start := lineOffset(entry.GetPosition("ProxyUrl"))
			from, to, spanErr := subscriptionScalarSpan(request.Content, start)
			if spanErr != nil {
				return result, spanErr
			}
			found := false
			for index := range edits {
				if edits[index].start == from && edits[index].end == to {
					var value string
					if json.Unmarshal([]byte(edits[index].value), &value) == nil {
						updated, rewriteErr = renameSubscriptionReferences(value, renamed, 0)
						if rewriteErr != nil {
							return result, rewriteErr
						}
						encoded, _ := json.Marshal(updated)
						edits[index].value = string(encoded)
						found = true
					}
					break
				}
			}
			if !found {
				if setErr := setScalar(entry, "ProxyUrl", updated); setErr != nil {
					return result, setErr
				}
			}
		}
	}
	sort.SliceStable(edits, func(i, j int) bool { return edits[i].start > edits[j].start })
	next := request.Content
	boundary := len(next) + 1
	for _, edit := range edits {
		if edit.end > boundary {
			return result, fmt.Errorf("overlapping subscription edits")
		}
		next = next[:edit.start] + edit.value + next[edit.end:]
		boundary = edit.start
	}
	if appendix != "" {
		if !strings.HasSuffix(next, "\n") {
			next += newline
		}
		next += appendix
	}
	var config ServerConfig
	if _, err := btoml.Decode(next, &config); err != nil {
		return result, fmt.Errorf("subscription edit produced invalid TOML")
	}
	if _, err := resolveUpstreamProxyReferences(config.UpStreams); err != nil {
		return result, fmt.Errorf("subscription edit rejected: %w", err)
	}
	parsed, err := pt.Load(next)
	if err != nil {
		return result, fmt.Errorf("subscription edit produced invalid TOML")
	}
	updatedEntries, _ := parsed.Get("UpStreams").([]*pt.Tree)
	newLines := strings.Split(next, "\n")
	for _, entry := range updatedEntries {
		line := newLines[entry.Position().Line-1]
		at := strings.Index(line, subscriptionMarker)
		if at < 0 {
			continue
		}
		data, decodeErr := base64.RawURLEncoding.DecodeString(strings.TrimSpace(line[at+len(subscriptionMarker):]))
		var meta subscriptionMetadata
		if decodeErr == nil && json.Unmarshal(data, &meta) == nil {
			if entry.Get("Remove") != true {
				result.Selections = append(result.Selections, subscriptionIdentity{SourceID: meta.SourceID, NodeID: meta.NodeID, NodeName: meta.NodeName})
			}
		}
	}
	result.Content = next
	counts := map[string]int{}
	for _, selection := range result.Selections {
		counts[selection.SourceID]++
		if counts[selection.SourceID] > 2048 {
			return result, fmt.Errorf("at most 2048 nodes per subscription may be imported into a profile")
		}
	}
	if len(result.Selections) > 4096 {
		return result, fmt.Errorf("at most 4096 subscription upstreams may be imported into a profile")
	}
	return result, nil
}

// Visual editors may render scalar values again, but ownership and refresh
// baselines must survive that save. Match actual parsed tables by Name.
func retainSubscriptionOwnership(content, previous string) (string, error) {
	oldTree, err := pt.Load(previous)
	if err != nil {
		return "", fmt.Errorf("invalid previous subscription configuration")
	}
	newTree, err := pt.Load(content)
	if err != nil {
		return "", fmt.Errorf("invalid edited subscription configuration")
	}
	oldEntries, _ := oldTree.Get("UpStreams").([]*pt.Tree)
	newEntries, _ := newTree.Get("UpStreams").([]*pt.Tree)
	oldLines, lines := strings.Split(previous, "\n"), strings.Split(content, "\n")
	markers := map[string]string{}
	for _, entry := range oldEntries {
		name, _ := entry.Get("Name").(string)
		line := oldLines[entry.Position().Line-1]
		at := strings.Index(line, subscriptionMarker)
		if name != "" && at >= 0 {
			markers[name] = strings.TrimSpace(line[at:])
		}
	}
	for _, entry := range newEntries {
		name, _ := entry.Get("Name").(string)
		if marker := markers[name]; marker != "" {
			index := entry.Position().Line - 1
			lines[index] = "[[UpStreams]] " + marker
		}
	}
	return strings.Join(lines, "\n"), nil
}

func subscriptionScalarSpan(content string, start int) (int, int, error) {
	if start < 0 || start >= len(content) {
		return 0, 0, fmt.Errorf("invalid upstream field location")
	}
	at := strings.IndexByte(content[start:], '=')
	if at < 0 {
		return 0, 0, fmt.Errorf("invalid upstream assignment")
	}
	at += start + 1
	for at < len(content) && (content[at] == ' ' || content[at] == '\t') {
		at++
	}
	from := at
	if at >= len(content) {
		return 0, 0, fmt.Errorf("missing upstream value")
	}
	if content[at] == '"' || content[at] == '\'' {
		quote := content[at]
		delimiter := string(quote)
		if strings.HasPrefix(content[at:], strings.Repeat(delimiter, 3)) {
			delimiter = strings.Repeat(delimiter, 3)
		}
		at += len(delimiter)
		for at < len(content) {
			if quote == '"' && content[at] == '\\' {
				at += 2
				continue
			}
			if strings.HasPrefix(content[at:], delimiter) {
				return from, at + len(delimiter), nil
			}
			at++
		}
		return 0, 0, fmt.Errorf("unterminated upstream string")
	}
	for at < len(content) && !strings.ContainsRune(" \t\r\n#", rune(content[at])) {
		at++
	}
	return from, at, nil
}

func renameSubscriptionReferences(raw string, names map[string]string, depth int) (string, error) {
	if len(names) == 0 {
		return raw, nil
	}
	if depth > 64 {
		return "", fmt.Errorf("proxy chain is too deep")
	}
	parsed, err := url.Parse(raw)
	if err != nil {
		return "", fmt.Errorf("invalid upstream URL")
	}
	query, err := url.ParseQuery(parsed.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid upstream query")
	}
	changed := false
	for key, values := range query {
		if !strings.EqualFold(key, "upProxy") {
			continue
		}
		for i, value := range values {
			if replacement, ok := names[value]; ok {
				values[i] = replacement
				changed = true
			} else if strings.Contains(value, "://") {
				replacement, innerErr := renameSubscriptionReferences(value, names, depth+1)
				if innerErr != nil {
					return "", innerErr
				}
				if replacement != value {
					values[i] = replacement
					changed = true
				}
			}
		}
		query[key] = values
	}
	if !changed {
		return raw, nil
	}
	parsed.RawQuery = query.Encode()
	return parsed.String(), nil
}

func mergeSubscriptionURL(current, desired string, old subscriptionMetadata) (string, error) {
	if current == "" || subscriptionFingerprint(current) == old.Baseline["ProxyUrl"] {
		return desired, nil
	}
	parsed, err := url.Parse(current)
	if err != nil {
		return "", fmt.Errorf("invalid customized subscription URL")
	}
	next, err := url.Parse(desired)
	if err != nil {
		return "", fmt.Errorf("invalid subscribed URL")
	}
	currentQuery, err := url.ParseQuery(parsed.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid customized subscription query")
	}
	query, err := url.ParseQuery(next.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid subscribed query")
	}
	original := query.Encode()
	for key, hash := range old.QueryBaseline {
		if subscriptionFingerprint(currentQuery[key]) != hash {
			if values, ok := currentQuery[key]; ok {
				query[key] = values
			} else {
				delete(query, key)
			}
		}
	}
	for key, values := range currentQuery {
		if _, known := old.QueryBaseline[key]; !known {
			if old.QueryBaseline != nil {
				query[key] = values
			} else if _, remote := query[key]; !remote {
				query[key] = values
			}
		}
	}
	if query.Encode() == original {
		return desired, nil
	}
	next.RawQuery = query.Encode()
	return next.String(), nil
}

func applySubscriptionURLLocks(current, desired string, fields []string) (string, error) {
	lockParent, lockCredit := false, false
	for _, field := range fields {
		lockParent = lockParent || field == "UpProxy"
		lockCredit = lockCredit || field == "Credit"
	}
	if !lockParent && !lockCredit {
		return current, nil
	}
	parsed, err := url.Parse(current)
	if err != nil {
		return "", fmt.Errorf("invalid customized subscription URL")
	}
	target, err := url.Parse(desired)
	if err != nil {
		return "", fmt.Errorf("invalid generated subscription URL")
	}
	query, err := url.ParseQuery(parsed.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid customized subscription query")
	}
	targetQuery, err := url.ParseQuery(target.RawQuery)
	if err != nil {
		return "", fmt.Errorf("invalid generated subscription query")
	}
	for key := range query {
		if lockCredit && strings.EqualFold(key, "credit") || lockParent && strings.EqualFold(key, "upProxy") {
			delete(query, key)
		}
	}
	if lockParent {
		for key, value := range targetQuery {
			if strings.EqualFold(key, "upProxy") {
				query[key] = value
			}
		}
	}
	parsed.RawQuery = query.Encode()
	return parsed.String(), nil
}

func runSubscriptionTOMLEditor() int {
	var request subscriptionTOMLRequest
	decoder := json.NewDecoder(io.LimitReader(os.Stdin, 16*1024*1024))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&request); err != nil {
		fmt.Fprintln(os.Stderr, "invalid subscription edit request")
		return 2
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		fmt.Fprintln(os.Stderr, "unexpected subscription edit request data")
		return 2
	}
	result, err := editSubscriptionTOML(request)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 2
	}
	if err := json.NewEncoder(os.Stdout).Encode(result); err != nil {
		return 2
	}
	return 0
}

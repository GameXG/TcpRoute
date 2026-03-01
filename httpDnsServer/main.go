package main

import (
	"encoding/json"
	"fmt"
	"github.com/golang/glog"
	"github.com/golang/groupcache/lru"
	"io"
	"log"
	"net/http"
	"os"
	"regexp"
	"strings"
	"sync"
	"time"
)

/*
从文件载入 DNS 查询记录


*/

const DefaultCredit = 0

type SpreadRecordDomain struct {
	domainRegexp *regexp.Regexp
	ips          []*IpRecord
}

type FileRecord struct {
	Domain []string
	Ip     string
	Ping   time.Duration
}

type IpRecord struct {
	Ip     string
	Ping   time.Duration
	Credit int //信誉
}

func (r *IpRecord) String() string {
	return fmt.Sprintf("IpRecord{ip:%v, ping:%v, credit:%v}", r.Ip, r.Ping, r.Credit)
}

type FileDNS struct {
	// 普通的域名列表
	// k=域名  v=ips
	domains map[string][]*IpRecord

	// 泛解析的域名列表
	SpreadRecord map[string]*SpreadRecordDomain

	cache    *lru.Cache
	cacheRwm sync.RWMutex
}

func NewFileDns(fpath string) (*FileDNS, error) {
	fileDns := FileDNS{}
	fileDns.domains = make(map[string][]*IpRecord)
	fileDns.SpreadRecord = make(map[string]*SpreadRecordDomain)
	fileDns.cache = lru.New(500)

	f, err := os.Open(fpath)
	if err != nil {
		return nil, err
	}

	dec := json.NewDecoder(f)

	var r FileRecord
	for {
		if err := dec.Decode(&r); err == io.EOF {
			break
		} else if err != nil {
			glog.Warning(fmt.Sprintf("解析DNS记录文件错误：%v\r\n", err))
			continue
		}
		ip := IpRecord{r.Ip, r.Ping, DefaultCredit}

		for _, d := range r.Domain {
			d = strings.ToLower(d)
			if strings.ContainsAny(d, "*?") {
				// 泛解析类型
				v, ok := fileDns.SpreadRecord[d]
				if ok == false {
					quoteDomain := regexp.QuoteMeta(d)
					regexpDomain := strings.Replace(quoteDomain, `\*`, `[^.]+`, -1)
					regexpDomain = strings.Replace(regexpDomain, `\?`, `[^.]`, -1)
					regexpDomain = fmt.Sprint(`^`, regexpDomain, `$`)

					r, err := regexp.Compile(regexpDomain)
					if err != nil {
						return nil, err
					}

					v = &SpreadRecordDomain{r, make([]*IpRecord, 0, 1)}
					fileDns.SpreadRecord[d] = v
				}
				v.ips = append(v.ips, &ip)
			} else {
				//普通类型
				fileDns.domains[d] = append(fileDns.domains[d], &ip)
			}
		}
	}

	return &fileDns, nil
}

func (fdns *FileDNS) cacheGet(domain string) []*IpRecord {
	fdns.cacheRwm.RLock()
	defer fdns.cacheRwm.RUnlock()

	c, ok := fdns.cache.Get(domain)
	if ok != true {
		return nil
	}
	return c.([]*IpRecord)
}

func (fdns *FileDNS) cacheSet(domain string, ips []*IpRecord) {
	fdns.cacheRwm.Lock()
	defer fdns.cacheRwm.Unlock()
	fdns.cache.Add(domain, ips)
}

func (fdns *FileDNS) query(domain string) []*IpRecord {
	domain = strings.ToLower(domain)

	// 解决 google.com.hk 的问题。
	google_hk := "google.com.hk"
	if len(domain) > len(google_hk) {
		if domain[len(domain)-len(google_hk):] == google_hk {
			domain = domain[:len(domain)-3]
		}
	}

	ips := fdns.cacheGet(domain)
	if ips != nil {
		return ips
	}

	ips = fdns.domains[domain]
	fmt.Printf("%v 基本查询结果：%v\r\n", domain, ips)
	for k, r := range fdns.SpreadRecord {
		if r.domainRegexp.MatchString(domain) {
			fmt.Printf("%v 泛解析匹配：%v\r\n", domain, k)
			ips = append(ips, r.ips...)
		}
	}
	if ips != nil {
		fdns.cacheSet(domain, ips)
	}

	return ips
}

type HttpQueryRes struct {
	Domain  string
	Status  int
	Message string
	Ips     []*IpRecord
}

func (fdns *FileDNS) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	res := HttpQueryRes{"", 0, "", nil}
	enc := json.NewEncoder(w)

	r.ParseForm()
	d := r.Form.Get("d")
	if d == "" {
		res.Status = -1
		res.Message = "请输入参数。"
		enc.Encode(res)
		return
	}

	ips := fdns.query(d)
	res.Ips = ips
	res.Domain = d
	enc.Encode(res)
	return
}

func main() {
	// 载入 DNS 数据
	fdns, err := NewFileDns("ip-tcping-2.txt")
	if err != nil {
		panic(err)
	}

	http.Handle("/httpdns", fdns)

	log.Fatal(http.ListenAndServe(":5353", nil))

}

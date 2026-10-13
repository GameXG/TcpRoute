package netchan

import (
	"encoding/json"
	"fmt"
	"github.com/golang/glog"
	"net/http"
	"net/url"
	"time"
)

type httpDNS struct {
	surl      string // http dns 服务器 url
	url       url.URL
	url_query url.Values
}

type HttpQueryRes struct {
	Domain  string
	Status  int
	Message string
	Ips     []*IpRecord
}

type IpRecord struct {
	Ip     string
	Ping   time.Duration
	Credit int //信誉
}

func NewHttpDns(u string) (*httpDNS, error) {
	hd := httpDNS{}

	hd.surl = u
	_url, err := url.Parse(u)
	if err != nil {
		return nil, fmt.Errorf("url 解析错误：%v", err)
	}
	hd.url = *_url
	hd.url_query = _url.Query()

	return &hd, nil
}

// 执行dns查询(阻塞)
// 查询结果会通过 RecordChan 返回，注意可能被阻塞。
// 在需要提前结束时 ExitChan 会被关闭
// 注意：实现者需要处理 RecordChan 被关闭的情况。
func (hd *httpDNS) query(domain string, RecordChan chan *DnsRecord, ExitChan chan int) {

	select {
	case <-ExitChan:
		return
	default:
	}

	newUrl := hd.url
	newQuery := hd.url_query
	newQuery.Set("d", domain)
	newUrl.RawQuery = newQuery.Encode()

	resp, err := http.Get(newUrl.String())
	if err != nil {
		glog.Warning(fmt.Sprintf("httpDns query %v err: %v", domain, err))
		return
	}

	defer resp.Body.Close()

	dnsRes := HttpQueryRes{}

	dec := json.NewDecoder(resp.Body)

	if err := dec.Decode(&dnsRes); err != nil {
		glog.Warning(fmt.Sprintf("httpDns query %v err: %v", domain, err))
		return
	}

	select {
	case <-ExitChan:
		return
	default:
		for _, ip := range dnsRes.Ips {
			func() {
				defer func() { _ = recover() }()
				RecordChan <- &DnsRecord{ip.Ip, ip.Credit}

			}()
		}
	}
}

// 执行前的默认等待时间
func (hd *httpDNS) querySleep() time.Duration {
	return 0 * time.Millisecond
}

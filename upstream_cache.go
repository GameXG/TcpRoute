package main

import (
	"fmt"
	"github.com/golang/groupcache/lru"
	"sort"
	"sync"
	"time"
)

// up Stream 缓存
// 由于是并行连接，所以不会出现延迟。缓存只是为了降低线路及目标望着你的负担。

const cacheTimeout = 15 * time.Minute

// 保存连接耗时缓存
// 每一个ip、代理一个
type upStreamConnCacheAddrItem struct {
	// TODO: 对于连接被重置怎么处理？
	IpAddr     string        // IP:端口 格式的地址
	DomainAddr string        // 域名:端口 格式的地址
	TcpPing    time.Duration // 建立连接耗时
	dialClient *DialClient   // 使用的线路
	dialName   string
}

type upStreamConnCacheAddrItems []*upStreamConnCacheAddrItem

func (a upStreamConnCacheAddrItems) Len() int           { return len(a) }
func (a upStreamConnCacheAddrItems) Swap(i, j int)      { a[i], a[j] = a[j], a[i] }
func (a upStreamConnCacheAddrItems) Less(i, j int) bool { return a[i].TcpPing < a[j].TcpPing }

// 每域名1个
type upStreamConnCacheDomainItem struct {
	Expiredime time.Time                             // 过期时间
	itemsList  upStreamConnCacheAddrItems            // 排序好的连接记录
	itemDict   map[string]*upStreamConnCacheAddrItem // key = "%v-%v" % dialName-Ipaddr
}

type ErrCheck interface {
	Check(dialName, domainAddr, ipAddr string) bool
}

// 连接缓存
type upStreamConnCache struct {
	//domains map[string]upStreamConnCacheDomainItem
	domains  *lru.Cache // 域名 map ，类型是 *upStreamConnCacheDomainItem
	errCheck ErrCheck
	m        sync.Mutex
}

func NewUpStreamConnCache(errCheck ErrCheck) *upStreamConnCache {
	c := upStreamConnCache{}
	c.domains = lru.New(200)
	c.errCheck = errCheck
	return &c
}

// 更新记录
func (c *upStreamConnCache) Updata(domainAddr, ipAddr string, tcpping time.Duration, dialClient *DialClient, dialName string) {
	c.m.Lock()
	defer c.m.Unlock()

	// 先取得结果
	item := c.get(domainAddr)
	if item == nil {
		Expiredime := time.Now().Add(cacheTimeout)
		itemsList := make([]*upStreamConnCacheAddrItem, 0, 10)
		itemDict := make(map[string]*upStreamConnCacheAddrItem)

		item = &upStreamConnCacheDomainItem{Expiredime, itemsList, itemDict}
		c.set(domainAddr, item)
	}

	key := fmt.Sprintf("%v-%v", dialName, ipAddr)
	value, ok := item.itemDict[key]
	if ok != true {
		value = &upStreamConnCacheAddrItem{}
		item.itemDict[key] = value
		item.itemsList = append(item.itemsList, value)
	}

	value.IpAddr = ipAddr
	value.DomainAddr = domainAddr
	value.TcpPing = tcpping
	value.dialClient = dialClient
	value.dialName = dialName

	// TODO: 检查是否需要更新
	sort.Sort(item.itemsList)
}

// 获得指定的项
// 多线程不安全。
// 不存在返回 nil
func (c *upStreamConnCache) get(domainAddr string) *upStreamConnCacheDomainItem {
	v, ok := c.domains.Get(domainAddr)

	if ok == false {
		return nil
	}

	res := v.(*upStreamConnCacheDomainItem)

	if res.Expiredime.Before(time.Now()) {
		c.domains.Remove(domainAddr)
		return nil
	}

	return res
}

// 设置指定项
// 多线程不安全
func (c *upStreamConnCache) set(domainAddr string, item *upStreamConnCacheDomainItem) {
	c.domains.Add(domainAddr, item)
}

// 取得指定的缓存
// 存在时 err == nil ，否则 err != nil
// 返回值是值拷贝，不需要担心多线程复用问题
// 会尝试检查是否是是异常地址
func (c *upStreamConnCache) GetOptimal(domainAddr string) (upStreamConnCacheAddrItem, error) {
	c.m.Lock()
	defer c.m.Unlock()

	item := c.get(domainAddr)
	if item == nil {
		return upStreamConnCacheAddrItem{}, fmt.Errorf("不存在")
	}

	if len(item.itemsList) == 0 {
		return upStreamConnCacheAddrItem{}, fmt.Errorf("不存在")
	}

	for _, i := range item.itemsList {
		if c.errCheck == nil || c.errCheck.Check(i.dialName, i.DomainAddr, i.IpAddr) == true {
			return *i, nil
		}
	}
	return upStreamConnCacheAddrItem{}, fmt.Errorf("全部连接有异常。")
}

// 删除某个域的缓存记录
// 在每次进行全部连接重试时将会清空旧的缓存内容。
func (c *upStreamConnCache) Del(domainAddr string) {
	c.m.Lock()
	defer c.m.Unlock()

	c.domains.Remove(domainAddr)
}

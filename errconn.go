package main

import (
	"fmt"
	"github.com/golang/groupcache/lru"
	"sync"
	"time"
)

/*



连接被重置应该是关键字封锁造成的，但是关键字有两种可能：
	1、url 是关键字
	2、用户输入的内容是关键字

对于第一种情况，使用普通未加密线路注定次次撞墙，只能切换线路。

对于第二种情况，如果用户不变更输入的内容，还是次次撞墙，但是如果变更了内容，那么就不会碰到撞墙的问题了。

也就是碰到撞墙的情况一般只能切换线路了...

将故障记录保存下来，然后每次使用时(可设置过期时间优化)统计一下各个线路故障率来确定是用什么线路，是否使用当前ip等信息。

*/

const ErrConnLogTimeOut = 30 * time.Minute // 超过这个期限的错误日志会被删除

const (
	ErrConnTypeReset ErrConnType = 1 // 连接被重置
	ErrConnTypeRead0 ErrConnType = 2 // 读不到数据
)

type ErrConnType int

type ErrConnLog struct {
	IpAddr   string      // ip:端口
	DialName string      // dial 名称
	ErrType  ErrConnType // 错误类型
	time     time.Time   //错误时间
}

// 缓存
type ErrConnDomainCache struct {
	// dial 错误次数
	// addr 错误次数
	// 或者说是拒绝的 dial 及 addr
	dial   map[string]int // dial 错误次数
	ipAddr map[string]int // ipAddr 错误次数
}

type ErrConnDomain struct {
	domainAddr       string
	log              []*ErrConnLog
	cache            *ErrConnDomainCache
	cacheExpiredTime time.Time //到达这个时间表示 cache 需要根据 log 重新生成
}

// 异常连接 相关服务
type ErrConnService struct {
	lru *lru.Cache // key domainAddr value *ErrConnDomain
	m   sync.Mutex
}

func NewErrConnService() *ErrConnService {
	res := ErrConnService{}
	res.lru = lru.New(100)
	return &res
}

// 非线程安全
func (ec *ErrConnService) get(domainAddr string) *ErrConnDomain {
	v, ok := ec.lru.Get(domainAddr)
	if ok != true {
		return nil
	}
	res := v.(*ErrConnDomain)
	return res
}

// 非线程安全
func (ec *ErrConnService) set(domain *ErrConnDomain) {
	ec.lru.Add(domain.domainAddr, domain)
}

// 增加异常记录
func (ec *ErrConnService) AddErrLog(dialName, domainAddr, ipAddr string, errType ErrConnType) {
	ec.m.Lock()
	defer ec.m.Unlock()

	d := ec.get(domainAddr)
	if d == nil {
		d = &ErrConnDomain{domainAddr, make([]*ErrConnLog, 0, 1), nil, time.Unix(0, 0)}
		ec.set(d)
	}
	log := ErrConnLog{ipAddr, dialName, errType, time.Now()}

	d.log = append(d.log, &log)
	//d.cacheExpiredTime = time.Unix(0, 0)
	d.refresh()
}

// 确认连接是否为异常ip
// 不能单纯提供 ip ，还需要提供域名及 dialName
// 否则域名为阻断关键字时将会不断的阻断可用ip，知道全部ip都完蛋为止
// 如果启用了全球解析，那么需要挂掉很多ip。
// 返回值： true 表示安全 false 表示不建议使用
func (ec *ErrConnService) Check(dialName, domainAddr, ipAddr string) bool {
	ec.m.Lock()
	defer ec.m.Unlock()

	d := ec.get(domainAddr)
	if d == nil {
		return true
	}

	if d.cacheExpiredTime.Before(time.Now()) || d.cache == nil {
		d.refresh()
	}

	if d.cache.dial[dialName] >= 5 {
		fmt.Printf("%v 的 %v 线路的尝试连接IP %v ，由于线路属于经常故障线路，忽略本连接。\r\n", domainAddr, dialName, ipAddr)
		return false
	}

	if d.cache.ipAddr[ipAddr] >= 2 {
		fmt.Printf("%v 的 %v 线路的尝试连接IP %v ，由于ip属于经常故障ip，忽略本连接。\r\n", domainAddr, dialName, ipAddr)
		return false
	}

	return true
}

// 删除过期的内容
// 重新生成 cache
func (d *ErrConnDomain) refresh() {
	d.cache = &ErrConnDomainCache{}
	d.cache.dial = make(map[string]int)
	d.cache.ipAddr = make(map[string]int)
	d.cacheExpiredTime = time.Now().Add(ErrConnLogTimeOut)

	newLog := make([]*ErrConnLog, 0, len(d.log))

	// 这个日期之前的log会被删除
	logExpiredTime := time.Now().Add(-1 * ErrConnLogTimeOut)

	for _, l := range d.log {
		if l.time.Before(logExpiredTime) {
			continue
		}
		//TODO 其实这里可以优化下，默认 log 是按时间排序的，直接切片即可。
		newLog = append(newLog, l)

		d.cache.dial[l.DialName] += 1
		if l.IpAddr != d.domainAddr {
			d.cache.ipAddr[l.IpAddr] += 1
		}

		// 计算新的过期时间
		eTime := l.time.Add(ErrConnLogTimeOut)
		if eTime.Before(d.cacheExpiredTime) {
			d.cacheExpiredTime = eTime
		}
	}
}

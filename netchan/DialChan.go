package netchan

import (
	"fmt"
	"net"
	"strconv"
	"sync"
	"sync/atomic"
	"time"
)

const LocalConnGoCount = 5 // 本地连接时使用的线程数（只对dns解析结果生效）

type ConnRes struct {
	Dial       DialTimeouter
	Conn       net.Conn
	Ping       time.Duration // 连接耗时
	DomainAddr string
	IpAddr     string //当使用DNS解析时，保存着 ip:端口 格式的地址
	UserData   interface{}
}

type DialTimeouter interface {
	DialTimeout(network, address string, timeout time.Duration) (net.Conn, error)
}

/*
将标准的 Dial 接口转换成 Chan 返回。

可以通过选项指定是否本地dns解析，使用本地解析时会同时使用获得的多个ip连接，同样通过 chan 返回所有建立的连接。

filter 过滤器，确定是否使用dns解析得到的ip
*/
func ChanDialTimeout(dial DialTimeouter, dialCredit int, connChan chan ConnRes, exitChan chan int, dnsResolve bool, userData interface{}, filter DialFilterer, network, address string, timeout time.Duration) (err error) {
	if filter == nil {
		filter = NewDialFilter(nil)
	}

	var connErr error
	connErrLock := sync.Mutex{}

	myExitChan := make(chan int)

	// 安全结束本次连接，可以多次调用
	mySafeExit := func() {
		defer func() { _ = recover() }()
		close(myExitChan)
	}

	// 截止时间
	finalDeadline := time.Time{}
	if timeout != 0 {
		finalDeadline = time.Now().Add(timeout)
	}

	// 函数结束时(已经找到可靠连接时)不再尝试建立新连接。
	defer mySafeExit()
	// timeout 时安全退出。
	time.AfterFunc(timeout, mySafeExit)
	// 外部要求退出时安全退出
	go func() {
		<-exitChan
		mySafeExit()
	}()

	select {
	case <-myExitChan:
		return nil
	case <-exitChan:
		return nil
	default:
	}

	// 检查是否使用的ip地址。
	host, prot, err := net.SplitHostPort(address)
	if err != nil {
		return fmt.Errorf("地址错误：%v", err)
	}
	portInt, err := strconv.Atoi(prot)
	if err != nil || portInt < 0 {
		return fmt.Errorf("端口错误：%v", portInt)
	}
	ip := net.ParseIP(host)

	if dnsResolve == false || ip != nil {
		// 不允许本地 dns 解析 或 者本身就是 ip 地址不需要解析。

		// 检查是否符合安全标准
		if filter.DialFilter(network, host, ip.String(), portInt, dialCredit, dialCredit) == false {
			return nil
		}

		n := time.Now()
		c, err := dial.DialTimeout(network, address, timeout)
		if err != nil {
			return err
		} else {
			// 使用匿名函数包装的原因是 connChan 可能已经关闭
			func() {
				defer func() { _ = recover() }()
				connChan <- ConnRes{dial, c, time.Now().Sub(n), address, address, userData}
			}()
			return nil
		}
	} else {
		// 针对线路执行安全检查，通过后下面还有针对 ip 进行的安全检查
		if filter.DialFilter(network, host, "", portInt, dialCredit, dialCredit) == false {
			return nil
		}

		// 本地执行 DNS 解析
		dnsRes := NewDnsQuery(host)

		// 退出时及被要求终止时停止dns解析
		go func() {
			defer func() { _ = recover() }()
			<-myExitChan
			dnsRes.Stop()
		}()

		var wg sync.WaitGroup
		var okCount uint32 = 0
		wg.Add(LocalConnGoCount)

		// 启动多个连接线程连接
		for i := 0; i < LocalConnGoCount; i++ {
			go func() {
				defer func() { wg.Done() }()

				// 接收 dns 解析结果并执行连接
				for r := range dnsRes.RecordChan {
					select {
					case <-myExitChan:
						return
					default:
					}
					// 执行安全检查，对于不太安全的dns解析结果只用于 https 连接。
					if filter.DialFilter(network, host, r.Ip, portInt, dialCredit, r.Credit) == false {
						continue
					}

					// 根据当前时间生成新的 timeout
					// 不用担心 timeout 变成负数
					// 到达 finalDeadline 时会关闭 myExitChan, dial.DialTimeout 调用时 timeout 为负数会立刻超时。
					timeout := timeout
					if finalDeadline.IsZero() == false {
						timeout = finalDeadline.Sub(time.Now())
					}

					ipAddr := net.JoinHostPort(r.Ip, prot)
					n := time.Now()
					c, err := dial.DialTimeout("tcp", ipAddr, timeout)
					if err != nil {
						connErrLock.Lock()
						connErr = err
						connErrLock.Unlock()
						continue
					}
					atomic.AddUint32(&okCount, 1)

					func() { // 使用匿名函数捕获异常，防止 connChan 关闭时崩溃
						defer func() { _ = recover() }()
						connChan <- ConnRes{dial, c, time.Now().Sub(n), address, ipAddr, userData}
					}()
				}
			}()
		}

		// 等待所有线程运行完毕
		wg.Wait()

		if okCount > 0 {
			return nil
		}

		return connErr
	}
}

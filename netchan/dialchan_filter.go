package netchan

import (
	"fmt"
	"github.com/golang/glog"
)

/*
 一些默认安全级别

0		指的是默认线路的安全级别，默认 http 之类的非加密协议也是这个级别。
-500	是 https 等支持服务器验证的协议需要的安全级别。意味着即使线路、ip安全级别低些也允许操作。


*/

// 端口需要的安全等级
// 例如 80 端口的安全等级是0，表示 -100 的 ip 不能用在 80 端口的访问上面。
// 主要目的是通过一些渠道获得的ip不可靠，不建议 80 之类的不安全的协议使用不可靠的 ip。
var SAFE_PORET_LIST map[int]int = map[int]int{
	21:   0,    //ftp
	22:   0,    //ssh(虽然 ssh 有公钥加密，但是第一次连接时有可能碰到中间人攻击)
	23:   0,    //telnet
	25:   0,    //smtp
	53:   0,    //dns
	80:   0,    //http
	110:  0,    //pop3
	443:  -500, //https
	465:  -500, // smtp ssl
	993:  -500, //imap ssl
	995:  -500, // pop ssl
	3389: 0,    // windows 远程桌面
}

type DialFilterer interface {
	// DialFilter 连接过滤器
	// 只有返回值是 True 的才会执行连接。
	// 主要目的是某些线路、某些 dns 解析结果不可靠，对于不可靠的线路及dns解析结果只用于 https 、smtp ssl 等自带验证的协议。
	// 参数： host、ip、port 为连接目标
	//       dialCredit 为 线路 的可靠程度
	//       ipCredit 为 IP 的可靠程度。
	//                调用方不进行dns解析时将=dialCredit。
	DialFilter(network, host, ip string, port int, dialCredit, ipCredit int) bool
}

type dialFilter struct {
	port_list map[int]int
}

func NewDialFilter(port_list map[int]int) *dialFilter {
	d := dialFilter{make(map[int]int)}

	if port_list != nil {
		for k, v := range d.port_list {
			d.port_list[k] = v
		}
	}
	return &d
}

/*
过滤不安全
*/
func (d *dialFilter) DialFilter(network, host, ip string, port int, dialCredit, ipCredit int) bool {
	credit := dialCredit
	if ipCredit < credit {
		credit = ipCredit
	}

	v, ok := d.port_list[port]
	if ok == false {
		v, ok = SAFE_PORET_LIST[port]
	}
	if ok == false {
		// 对于未知的端口，安全级别设置为0
		v = 0
	}

	if credit >= v {
		return true
	} else {
		glog.Info(fmt.Sprintf("%v %v(%v):%v dialCreadit=%v ipCredit=%v 信任度低，拒绝访问。", network, host, ip, port, dialCredit, ipCredit))
		return false
	}
}
